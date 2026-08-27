package com.wds.sixstarsstandalone;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.time.OffsetDateTime;
import java.util.*;
import java.util.concurrent.*;

/**
 * Server-free Android conversation runtime. Direct foreground requests are serialized, the complete
 * primary memory review is protected/non-cancelable, and the next foreground waits for that review
 * to durably commit before it may build a new prompt.
 */
final class StandaloneRuntime implements AutoCloseable {
    interface Callbacks {
        void onStatus(String text);
        void onUserText(String starId, String text);
        void onAssistantStart(String starId);
        void onAssistantDelta(String starId, String delta);
        void onAssistantComplete(String starId, String text);
        void onStateChanged(String starId, JSONObject stateSnapshot);
        void onError(String operation, Throwable error);
    }

    interface PlaybackObserver {
        void onPlaybackStarted(String starId, long durationMs, long playbackElapsedMs, int[] speechActivitySegmentsMs);
        void onPlaybackFinished(String starId, boolean completedNormally);
    }

    private final StandaloneSettings settings;
    private final ContentRepository content;
    private final StarProfileRepository profiles;
    private final StarStateStore stateStore;
    private final AndroidPromptBuilder prompts;
    private final GoogleAiStudioClient llm;
    private final GroqAsrClient asr;
    private final StarTtsClient tts;
    private final AudioPlaybackService audio;
    private final MemoryStateUpdater memory;
    private final MaintenanceService maintenance;
    private final RuntimeTrace trace;
    private volatile PlaybackObserver playbackObserver;

    private final ExecutorService foregroundExecutor=Executors.newSingleThreadExecutor(named("SixStarsForeground"));
    private final ExecutorService memoryExecutor=Executors.newSingleThreadExecutor(named("SixStarsPrimaryReview"));
    private final ExecutorService maintenanceExecutor=Executors.newSingleThreadExecutor(named("SixStarsMaintenance"));
    private final ExecutorService ttsExecutor=Executors.newSingleThreadExecutor(named("SixStarsTts"));
    private final ExecutorService ioExecutor=Executors.newSingleThreadExecutor(named("SixStarsAsr"));
    private final Object barrierLock=new Object();
    private CompletableFuture<Void> primaryReviewBarrier=CompletableFuture.completedFuture(null);
    private volatile boolean closed;
    private final Random roughWritingRandom = new Random();

    StandaloneRuntime(Context context) throws Exception {
        settings=StandaloneSettings.load(context);
        content=new ContentRepository(context); content.ensureMaterialized();
        profiles=new StarProfileRepository(content);
        stateStore=new StarStateStore(context,settings);
        prompts=new AndroidPromptBuilder(content,settings);
        trace=new RuntimeTrace(context,settings);
        llm=new GoogleAiStudioClient(context,settings,trace);
        asr=new GroqAsrClient(settings);
        if (settings.useLocalTts()) {
            // Prefer the .NET bridge because it resolves the canonical desktop Star profile and
            // absolute voice-clone file path. Keep direct Python WebSocket TTS only as a legacy
            // developer route when LocalServiceUrl is not configured.
            tts = settings.localTtsServiceConfigured()
                    ? new DesktopLocalTtsServiceClient(settings)
                    : new LocalQwenTtsClient(settings);
        } else {
            tts = new AlibabaQwenTtsClient(context,settings,content);
        }
        audio=new AudioPlaybackService(context);
        audio.setListener(new AudioPlaybackService.Listener() {
            @Override public void onPlaybackStarted(String starId, String label, long durationMs, long playbackElapsedMs, int[] speechActivitySegmentsMs) {
                trace.log("tts.playback-start", "star=" + starId + " " + label + " durationMs=" + durationMs + " speechSegments=" + (speechActivitySegmentsMs == null ? 0 : speechActivitySegmentsMs.length / 2));
                PlaybackObserver observer = playbackObserver;
                if (observer != null) observer.onPlaybackStarted(starId, durationMs, playbackElapsedMs, speechActivitySegmentsMs == null ? new int[0] : speechActivitySegmentsMs);
            }
            @Override public void onPlaybackFinished(String starId, String label, boolean completedNormally) {
                trace.log("tts.playback-complete", "star=" + starId + " " + label + " completed=" + completedNormally + " queued=" + audio.queuedCount());
                PlaybackObserver observer = playbackObserver;
                if (observer != null) observer.onPlaybackFinished(starId, completedNormally);
            }
            @Override public void onPlaybackError(String starId, String label, Throwable error) { trace.log("tts.playback-failed", "star=" + starId + " " + label + " " + error); }
            @Override public void onQueueIdle() { trace.log("tts.playback-queue-idle", "true"); }
        });
        memory=new MemoryStateUpdater(settings);
        trace.log("runtime.start", settings.configurationSummary() + " ttsClient=" + tts.modeName());
        maintenance=new MaintenanceService(settings,prompts,llm,trace);
        recoverPendingPrimaryReviews();
    }

    StandaloneSettings settings(){return settings;}
    ContentRepository content(){return content;}
    StarStateStore stateStore(){return stateStore;}
    StarProfileRepository profiles(){return profiles;}
    RuntimeTrace trace(){return trace;}
    void setPlaybackObserver(PlaybackObserver observer){ playbackObserver = observer; }

    void onAppResumed(){
        trace.log("runtime.app-resumed","refreshing hosted LLM network route");
        llm.onAppResumed();
    }

    JSONObject stateSnapshot(String starId) throws Exception { synchronized(stateStore){return JsonUtil.deepCopy(stateStore.star(starId));} }

    void submitText(String starId,String playerText,Callbacks cb){
        if(closed)return; String text=playerText==null?"":playerText.trim(); if(text.isEmpty())return;
        foregroundExecutor.submit(()->processForeground(starId,text,cb));
    }

    void transcribeAndSubmit(String starId,File wav,Callbacks cb){
        if(closed)return;
        ioExecutor.submit(()->{
            try{
                status(cb,"Transcribing with Groq..."); trace.log("asr.start",wav.getName());
                String text=asr.transcribe(wav);
                text=text==null?"":text.trim();
                trace.log("asr.complete","chars="+text.length());
                if(text.isEmpty()){
                    status(cb,"No speech was detected. Nothing was sent.");
                    trace.log("asr.empty","star="+starId);
                    return;
                }
                if(cb!=null)cb.onUserText(starId,text);
                submitTextInternalUserAlreadyShown(starId,text,cb);
            }catch(Throwable e){trace.log("asr.failed",e.toString());error(cb,"ASR",e);}finally{if(wav!=null)wav.delete();}
        });
    }

    void playRoughSpark(String starId, Callbacks cb){
        if(closed)return;
        foregroundExecutor.submit(()->runRoughSpark(starId,cb));
    }

    void playRoughStory(String starId, Callbacks cb){
        if(closed)return;
        foregroundExecutor.submit(()->runRoughStory(starId,cb));
    }

    private void runRoughSpark(String starId,Callbacks cb){
        try{
            JSONObject profile=JsonUtil.deepCopy(profiles.get(starId));
            boolean displacement;
            synchronized(roughWritingRandom){displacement=roughWritingRandom.nextBoolean();}
            String method=displacement?"verb-noun-displacement":"semantic-collision";
            trace.log("rough-spark.start","star="+starId+" method="+method);
            status(cb,profile.optString("displayName",starId)+" is making five rough Sparks...");
            if(cb!=null)cb.onAssistantStart(starId);
            List<ChatMessage> messages;
            synchronized(roughWritingRandom){messages=prompts.buildRoughSpark(profile,displacement,roughWritingRandom);}
            String json=llm.completeJson(messages,"rough-spark-"+method);
            JSONObject result=JsonUtil.parseObject(json);
            List<String> writings=new ArrayList<>();
            if(displacement){
                JSONArray items=result.optJSONArray("items");
                if(items!=null)for(int i=0;i<items.length();i++){
                    JSONObject item=items.optJSONObject(i);
                    if(item!=null){String writing=SpeechTextFormatter.normalize(item.optString("writing",""));if(!writing.isEmpty())writings.add(writing);}
                }
            }else{
                JSONArray jokes=result.optJSONArray("jokes");
                if(jokes!=null)for(int i=0;i<jokes.length();i++){
                    String writing=SpeechTextFormatter.normalize(jokes.optString(i,""));if(!writing.isEmpty())writings.add(writing);
                }
            }
            if(writings.isEmpty())throw new IllegalStateException("Rough Spark generator returned no speakable writings.");
            StringBuilder combined=new StringBuilder();
            for(int i=0;i<writings.size();i++){
                String writing=writings.get(i);
                if(combined.length()>0)combined.append("  ");
                combined.append("✦ ").append(writing);
                queueTts(profile,writing,cb);
            }
            String spoken=combined.toString();
            if(cb!=null){cb.onAssistantDelta(starId,spoken);cb.onAssistantComplete(starId,spoken);}
            trace.log("rough-spark.complete","star="+starId+" method="+method+" count="+writings.size());
            status(cb,"Rough Sparks queued for playback. Nothing was reviewed or saved.");
        }catch(Throwable e){trace.log("rough-spark.failed","star="+starId+" "+e);error(cb,"Rough Spark",e);}
    }

    private void runRoughStory(String starId,Callbacks cb){
        try{
            JSONObject profile=JsonUtil.deepCopy(profiles.get(starId));
            JSONObject state;
            synchronized(stateStore){state=JsonUtil.deepCopy(stateStore.star(starId));}
            trace.log("rough-story.start","star="+starId);
            status(cb,profile.optString("displayName",starId)+" is telling one rough story...");
            if(cb!=null)cb.onAssistantStart(starId);
            SentenceChunker chunker=new SentenceChunker();
            List<ChatMessage> request=prompts.buildRoughStory(profile,state);
            String raw=llm.completeStreaming(request,"rough-story",delta->{
                if(cb!=null)cb.onAssistantDelta(starId,delta);
                for(String chunk:chunker.accept(delta))queueTts(profile,SpeechTextFormatter.normalizeChunk(chunk),cb);
            });
            for(String chunk:chunker.finish())queueTts(profile,SpeechTextFormatter.normalizeChunk(chunk),cb);
            String spoken=SpeechTextFormatter.normalize(raw);
            if(cb!=null)cb.onAssistantComplete(starId,spoken);
            trace.log("rough-story.complete","star="+starId+" chars="+spoken.length());
            status(cb,"Rough story queued for playback. Nothing was reviewed or saved.");
        }catch(Throwable e){trace.log("rough-story.failed","star="+starId+" "+e);error(cb,"Rough Story",e);}
    }

    private void submitTextInternalUserAlreadyShown(String starId,String text,Callbacks cb){
        foregroundExecutor.submit(()->processForegroundInternal(starId,text,cb,false,"foreground-chat-voice"));
    }

    private void processForeground(String starId,String text,Callbacks cb){processForegroundInternal(starId,text,cb,true,"foreground-chat-text");}

    private void processForegroundInternal(String starId,String playerText,Callbacks cb,boolean showUser,String foregroundOperation){
        try{
            if(showUser&&cb!=null)cb.onUserText(starId,playerText);
            waitForPrimaryReview(cb);
            if(closed)return;
            JSONObject profile=JsonUtil.deepCopy(profiles.get(starId));
            JSONObject stateBefore;
            synchronized(stateStore){stateBefore=JsonUtil.deepCopy(stateStore.star(starId));}
            String turnStartedAt=OffsetDateTime.now().toString();
            trace.log("foreground.llm-start","star="+starId+" turn="+turnStartedAt);
            status(cb,""+profile.optString("displayName",starId)+" is answering...");
            if(cb!=null)cb.onAssistantStart(starId);

            SentenceChunker chunker=new SentenceChunker();
            final boolean[] firstSentence={false};
            List<ChatMessage> request=prompts.buildSpeech(profile,stateBefore,playerText);
            String rawSpoken=llm.completeStreaming(request,foregroundOperation,delta->{
                try{
                    if(cb!=null)cb.onAssistantDelta(starId,delta);
                    for(String sentence:chunker.accept(delta)){
                        String cleanSentence=SpeechTextFormatter.normalizeChunk(sentence);
                        if(cleanSentence.isEmpty())continue;
                        if(!firstSentence[0]){firstSentence[0]=true;trace.log("foreground.first-sentence-tts-queued","star="+starId+" chars="+cleanSentence.length());}
                        queueTts(profile,cleanSentence,cb);
                    }
                }catch(Exception e){error(cb,"TTS queue",e);}
            });
            for(String sentence:chunker.finish()){
                String cleanSentence=SpeechTextFormatter.normalizeChunk(sentence);
                if(cleanSentence.isEmpty())continue;
                if(!firstSentence[0]){firstSentence[0]=true;trace.log("foreground.first-sentence-tts-queued","star="+starId+" chars="+cleanSentence.length());}
                queueTts(profile,cleanSentence,cb);
            }
            String spoken=SpeechTextFormatter.normalize(rawSpoken);
            trace.log("foreground.llm-eos","star="+starId+" rawChars="+rawSpoken.length()+" cleanChars="+spoken.length());

            // Persist and launch the protected review immediately at Gemma EOS. This occurs while
            // the playback queue is still speaking; it never waits for voice playback to drain.
            JSONObject pending=new JSONObject().put("StarId",starId).put("PlayerText",playerText).put("StarResponse",spoken)
                    .put("TurnStartedAt",turnStartedAt).put("QueuedAt",OffsetDateTime.now().toString()).put("RequestType","Chat");
            synchronized(stateStore){JSONObject live=stateStore.star(starId);live.put("StandalonePendingPrimaryReview",pending);stateStore.persist();}
            queueProtectedPrimaryReview(profile,stateBefore,pending,cb);
            trace.log("primary-review.queued-at-eos","star="+starId+" audioQueued="+audio.queuedCount());

            if(cb!=null)cb.onAssistantComplete(starId,spoken);
            status(cb, settings.ttsEnabled() && settings.ttsConfigured()
                    ? profile.optString("displayName", starId) + " is speaking..."
                    : "Response ready.");
        }catch(Throwable e){trace.log("foreground.failed",e.toString());error(cb,"Foreground",e);}
    }

    private void queueProtectedPrimaryReview(JSONObject profile,JSONObject stateBefore,JSONObject pending,Callbacks cb)throws Exception{
        CompletableFuture<Void> barrier=new CompletableFuture<>();
        synchronized(barrierLock){primaryReviewBarrier=barrier;}
        memoryExecutor.submit(()->runProtectedReview(profile,stateBefore,pending,barrier,cb));
    }

    private void runProtectedReview(JSONObject profile,JSONObject stateBefore,JSONObject pending,CompletableFuture<Void> barrier,Callbacks cb){
        String starId=pending.optString("StarId",profile.optString("starId"));
        try{
            trace.log("primary-review.start","star="+starId+" turn="+pending.optString("TurnStartedAt"));
            JSONObject review=null;Throwable last=null;
            for(int attempt=1;attempt<=3;attempt++){
                try{
                    long reviewStarted=System.nanoTime();
                    trace.log("primary-review.llm-start","star="+starId+" attempt="+attempt);
                    List<ChatMessage> messages=prompts.buildReview(profile,stateBefore,pending.optString("PlayerText"),pending.optString("StarResponse"),pending.optString("TurnStartedAt"));
                    trace.log("primary-review.prompt-ready","star="+starId+" chars="+messageChars(messages)+" messages="+messages.size());
                    String json=llm.completeJson(messages,"background-review",BackgroundReviewContract.schema());
                    try {
                        review=BackgroundReviewContract.normalize(JsonUtil.parseObject(json));
                    } catch (Throwable parseError) {
                        trace.log("primary-review.json-parse-failed","star="+starId+" attempt="+attempt+" chars="+json.length()+" response="+debugPreview(json));
                        throw parseError;
                    }
                    long elapsedMs=TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-reviewStarted);
                    trace.log("primary-review.llm-complete","star="+starId+" attempt="+attempt+" chars="+json.length()+" elapsedMs="+elapsedMs); break;
                }catch(Throwable e){last=e;trace.log("primary-review.llm-attempt-failed","star="+starId+" attempt="+attempt+" "+e);if(attempt<3)Thread.sleep(attempt*400L);}
            }
            if(review==null)throw new IllegalStateException("Protected primary review failed after retries",last);

            JSONObject snapshot;
            trace.log("primary-review.commit-start","star="+starId);
            synchronized(stateStore){
                JSONObject live=stateStore.star(starId);
                memory.apply(profile,live,review,pending.optString("PlayerText"),pending.optString("StarResponse"),pending.optString("TurnStartedAt"));
                live.remove("StandalonePendingPrimaryReview");
                stateStore.persist(); snapshot=JsonUtil.deepCopy(live);
            }
            trace.log("primary-review.commit-complete","star="+starId);
            barrier.complete(null);
            if(cb!=null)cb.onStateChanged(starId,snapshot);
            status(cb, audio.isIdle() ? "Ready." : profile.optString("displayName", starId) + " is speaking...");
            queueMaintenance(profile,snapshot,cb);
        }catch(Throwable e){
            trace.log("primary-review.failed-protected","star="+starId+" "+e);
            // Deliberately keep the persisted pending review and fail the barrier. A later foreground is
            // not allowed to silently skip a missing primary memory update.
            barrier.completeExceptionally(e); error(cb,"Primary memory review",e);
        }
    }

    private void queueMaintenance(JSONObject profile,JSONObject stateSnapshot,Callbacks cb){
        maintenanceExecutor.submit(()->{
            try{
                MaintenanceService.Result result=maintenance.refreshIfNeeded(profile,stateSnapshot);
                if(!result.hasChanges())return;
                JSONObject snapshot;
                synchronized(stateStore){JSONObject live=stateStore.star(profile.optString("starId"));if(result.todayState!=null)live.put("TodayState",result.todayState);if(result.girlfriendHourlyState!=null)live.put("GirlfriendHourlyState",result.girlfriendHourlyState);stateStore.persist();snapshot=JsonUtil.deepCopy(live);}
                if(cb!=null)cb.onStateChanged(profile.optString("starId"),snapshot);
            }catch(Throwable e){trace.log("maintenance.commit-failed",e.toString());}
        });
    }

    private void queueTts(JSONObject profile,String sentence,Callbacks cb){
        if(sentence==null||sentence.trim().isEmpty())return;
        if(!settings.ttsEnabled()||!settings.ttsConfigured())return;
        final JSONObject p;
        try{p=JsonUtil.deepCopy(profile);}catch(Exception e){return;}
        final String spoken=SpeechTextFormatter.normalizeChunk(sentence);
        if(spoken.isEmpty())return;
        // IMPORTANT: this executor performs synthesis only. AudioPlaybackService owns a separate
        // playback queue/thread, so the moment sentence N's WAV comes back we enqueue it and begin
        // synthesizing sentence N+1 immediately while N is still speaking. This is the same
        // generation-ahead/playback-behind pipeline used by the working thin client.
        ttsExecutor.submit(()->{
            try{
                String star=p.optString("starId",p.optString("displayName","Star"));
                trace.log("tts.generate-start","mode="+tts.modeName()+" star="+star+" chars="+spoken.length()+" playbackQueued="+audio.queuedCount());
                long started=System.nanoTime();
                TtsAudioResult generated=tts.synthesize(p,spoken);
                long elapsedMs=TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-started);
                trace.log("tts.generate-complete","mode="+tts.modeName()+" star="+star+" bytes="+generated.bytes.length+" elapsedMs="+elapsedMs);
                WavSpeechActivityAnalyzer.Analysis activity=audio.enqueue(generated,star,star+" • "+spoken);
                trace.log("tts.lipsync-analysis","star="+star+" "+activity.debugSummary());
                trace.log("tts.playback-enqueued","star="+star+" queue="+audio.queuedCount()+"; next synthesis may begin immediately");
            }catch(Throwable e){
                trace.log("tts.failed","mode="+tts.modeName()+" "+e);
                error(cb,"TTS",e);
            }
        });
    }

    private void waitForPrimaryReview(Callbacks cb)throws Exception{
        if(!settings.blockForReview())return;
        CompletableFuture<Void> barrier; synchronized(barrierLock){barrier=primaryReviewBarrier;}
        if(!barrier.isDone()){
            trace.log("foreground.wait-primary-review","waiting=true");
            status(cb,"Finishing the previous protected memory update...");
            try{
                barrier.get();
                trace.log("foreground.wait-primary-review-complete","ok=true");
                return;
            }catch(ExecutionException e){
                trace.log("foreground.wait-primary-review-failed","willRetry=true error="+e.getCause());
            }
        }else{
            try{
                barrier.get();
                return;
            }catch(ExecutionException e){
                trace.log("foreground.previous-primary-review-failed","willRetry=true error="+e.getCause());
            }
        }

        // A protected review failure must never permanently poison the app. The pending turn is
        // persisted on-device, so retry it synchronously here before allowing the next foreground.
        // This preserves the invariant the app wants: primary memory commit first, next turn second.
        status(cb,"Retrying the previous protected memory update before continuing...");
        retryPendingPrimaryReviewsBlocking(cb);
    }

    boolean hasPendingPrimaryReview(){
        try{
            synchronized(stateStore){
                for(String id:StarProfileRepository.IDS){
                    JSONObject pending=stateStore.star(id).optJSONObject("StandalonePendingPrimaryReview");
                    if(pending!=null&&pending.length()>0)return true;
                }
            }
        }catch(Exception ignored){}
        return false;
    }

    void retryPendingPrimaryReviews(Callbacks cb){
        if(closed)return;
        ioExecutor.submit(()->{
            try{retryPendingPrimaryReviewsBlocking(cb);status(cb,"Pending memory review committed.");}
            catch(Throwable e){trace.log("primary-review.manual-retry-failed",e.toString());error(cb,"Primary memory review retry",e);}
        });
    }

    private void retryPendingPrimaryReviewsBlocking(Callbacks cb)throws Exception{
        List<JSONObject> pendingStates=collectPendingPrimaryReviews();
        if(pendingStates.isEmpty()){
            synchronized(barrierLock){primaryReviewBarrier=CompletableFuture.completedFuture(null);}
            return;
        }
        trace.log("primary-review.retry-before-foreground","count="+pendingStates.size());
        for(JSONObject item:pendingStates){
            String id=item.optString("StarId");
            status(cb,"Retrying protected memory for "+id+"...");
            runRecoveryReview(profiles.get(id),item.getJSONObject("State"),item.getJSONObject("Pending"));
        }
        synchronized(barrierLock){primaryReviewBarrier=CompletableFuture.completedFuture(null);}
        trace.log("primary-review.retry-before-foreground-complete","count="+pendingStates.size());
    }

    private List<JSONObject> collectPendingPrimaryReviews() throws Exception {
        List<JSONObject> pendingStates=new ArrayList<>();
        synchronized(stateStore){
            for(String id:StarProfileRepository.IDS){
                JSONObject state=stateStore.star(id);
                JSONObject pending=state.optJSONObject("StandalonePendingPrimaryReview");
                if(pending!=null&&pending.length()>0){
                    pendingStates.add(new JSONObject().put("StarId",id).put("Pending",JsonUtil.deepCopy(pending)).put("State",JsonUtil.deepCopy(state)));
                }
            }
        }
        return pendingStates;
    }

    private void recoverPendingPrimaryReviews() throws Exception {
        List<JSONObject> pendingStates=collectPendingPrimaryReviews();
        if(pendingStates.isEmpty())return;
        trace.log("primary-review.recovery-found","count="+pendingStates.size());
        CompletableFuture<Void> recovery=new CompletableFuture<>(); synchronized(barrierLock){primaryReviewBarrier=recovery;}
        memoryExecutor.submit(()->{
            try{
                for(JSONObject item:pendingStates){String id=item.optString("StarId");JSONObject pending=item.getJSONObject("Pending");JSONObject state=item.getJSONObject("State");runRecoveryReview(profiles.get(id),state,pending);}
                recovery.complete(null);trace.log("primary-review.recovery-complete","count="+pendingStates.size());
            }catch(Throwable e){trace.log("primary-review.recovery-failed",e.toString());recovery.completeExceptionally(e);}
        });
    }

    private void runRecoveryReview(JSONObject profile,JSONObject stateBefore,JSONObject pending)throws Exception{
        String starId=profile.optString("starId");trace.log("primary-review.recovery-start","star="+starId);
        List<ChatMessage> messages=prompts.buildReview(profile,stateBefore,pending.optString("PlayerText"),pending.optString("StarResponse"),pending.optString("TurnStartedAt"));
        trace.log("primary-review.recovery-prompt-ready","star="+starId+" chars="+messageChars(messages)+" messages="+messages.size());
        String json=llm.completeJson(messages,"background-review-recovery",BackgroundReviewContract.schema());
        JSONObject review;
        try {
            review=BackgroundReviewContract.normalize(JsonUtil.parseObject(json));
        } catch (Throwable parseError) {
            trace.log("primary-review.recovery-json-parse-failed","star="+starId+" chars="+json.length()+" response="+debugPreview(json));
            throw parseError;
        }
        synchronized(stateStore){JSONObject live=stateStore.star(starId);memory.apply(profile,live,review,pending.optString("PlayerText"),pending.optString("StarResponse"),pending.optString("TurnStartedAt"));live.remove("StandalonePendingPrimaryReview");stateStore.persist();}
        trace.log("primary-review.recovery-commit","star="+starId);
    }

    private static int messageChars(List<ChatMessage> messages){
        int total=0;
        if(messages!=null)for(ChatMessage message:messages)if(message!=null&&message.content!=null)total+=message.content.length();
        return total;
    }

    private static String debugPreview(String value){
        String x=value==null?"":value.replace('\r',' ').replace('\n',' ').trim();
        return x.length()<=1800?x:x.substring(0,1800)+"…";
    }

    private static ThreadFactory named(String base){return r->{Thread t=new Thread(r,base);t.setDaemon(true);return t;};}
    private static void status(Callbacks cb,String text){if(cb!=null)try{cb.onStatus(text);}catch(Exception ignored){}}
    private static void error(Callbacks cb,String op,Throwable e){if(cb!=null)try{cb.onError(op,e);}catch(Exception ignored){}}

    @Override public void close(){closed=true;foregroundExecutor.shutdownNow();ioExecutor.shutdownNow();ttsExecutor.shutdownNow();maintenanceExecutor.shutdownNow();memoryExecutor.shutdownNow();audio.close();}
}
