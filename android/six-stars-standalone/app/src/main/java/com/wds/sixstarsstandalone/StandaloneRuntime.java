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

    private final StandaloneSettings settings;
    private final ContentRepository content;
    private final StarProfileRepository profiles;
    private final StarStateStore stateStore;
    private final AndroidPromptBuilder prompts;
    private final GoogleAiStudioClient llm;
    private final GroqAsrClient asr;
    private final AlibabaQwenTtsClient tts;
    private final AudioPlaybackService audio;
    private final MemoryStateUpdater memory;
    private final MaintenanceService maintenance;
    private final RuntimeTrace trace;

    private final ExecutorService foregroundExecutor=Executors.newSingleThreadExecutor(named("SixStarsForeground"));
    private final ExecutorService memoryExecutor=Executors.newSingleThreadExecutor(named("SixStarsPrimaryReview"));
    private final ExecutorService maintenanceExecutor=Executors.newSingleThreadExecutor(named("SixStarsMaintenance"));
    private final ExecutorService ttsExecutor=Executors.newSingleThreadExecutor(named("SixStarsTts"));
    private final ExecutorService ioExecutor=Executors.newSingleThreadExecutor(named("SixStarsAsr"));
    private final Object barrierLock=new Object();
    private CompletableFuture<Void> primaryReviewBarrier=CompletableFuture.completedFuture(null);
    private volatile boolean closed;

    StandaloneRuntime(Context context) throws Exception {
        settings=StandaloneSettings.load(context);
        content=new ContentRepository(context); content.ensureMaterialized();
        profiles=new StarProfileRepository(content);
        stateStore=new StarStateStore(context,settings);
        prompts=new AndroidPromptBuilder(content,settings);
        llm=new GoogleAiStudioClient(settings);
        asr=new GroqAsrClient(settings);
        tts=new AlibabaQwenTtsClient(context,settings,content);
        audio=new AudioPlaybackService(context);
        memory=new MemoryStateUpdater(settings);
        trace=new RuntimeTrace(context,settings);
        maintenance=new MaintenanceService(settings,prompts,llm,trace);
        recoverPendingPrimaryReviews();
    }

    StandaloneSettings settings(){return settings;}
    ContentRepository content(){return content;}
    StarStateStore stateStore(){return stateStore;}
    StarProfileRepository profiles(){return profiles;}
    RuntimeTrace trace(){return trace;}

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
                String text=asr.transcribe(wav); trace.log("asr.complete","chars="+text.length());
                if(cb!=null)cb.onUserText(starId,text);
                submitTextInternalUserAlreadyShown(starId,text,cb);
            }catch(Throwable e){trace.log("asr.failed",e.toString());error(cb,"ASR",e);}finally{if(wav!=null)wav.delete();}
        });
    }

    private void submitTextInternalUserAlreadyShown(String starId,String text,Callbacks cb){
        foregroundExecutor.submit(()->processForegroundInternal(starId,text,cb,false));
    }

    private void processForeground(String starId,String text,Callbacks cb){processForegroundInternal(starId,text,cb,true);}

    private void processForegroundInternal(String starId,String playerText,Callbacks cb,boolean showUser){
        try{
            if(showUser&&cb!=null)cb.onUserText(starId,playerText);
            waitForPrimaryReview(cb);
            if(closed)return;
            JSONObject profile=JsonUtil.deepCopy(profiles.get(starId));
            JSONObject stateBefore;
            synchronized(stateStore){stateBefore=JsonUtil.deepCopy(stateStore.star(starId));}
            String turnStartedAt=OffsetDateTime.now().toString();
            trace.log("foreground.llm-start","star="+starId+" turn="+turnStartedAt);
            status(cb,""+profile.optString("displayName",starId)+" is thinking...");
            if(cb!=null)cb.onAssistantStart(starId);

            SentenceChunker chunker=new SentenceChunker();
            final boolean[] firstSentence={false};
            List<ChatMessage> request=prompts.buildSpeech(profile,stateBefore,playerText);
            String spoken=llm.completeStreaming(request,delta->{
                try{
                    if(cb!=null)cb.onAssistantDelta(starId,delta);
                    for(String sentence:chunker.accept(delta)){
                        if(!firstSentence[0]){firstSentence[0]=true;trace.log("foreground.first-sentence-tts-queued","star="+starId+" chars="+sentence.length());}
                        queueTts(profile,sentence,cb);
                    }
                }catch(Exception e){error(cb,"TTS queue",e);}
            });
            for(String sentence:chunker.finish()){
                if(!firstSentence[0]){firstSentence[0]=true;trace.log("foreground.first-sentence-tts-queued","star="+starId+" chars="+sentence.length());}
                queueTts(profile,sentence,cb);
            }
            trace.log("foreground.llm-eos","star="+starId+" chars="+spoken.length());
            if(cb!=null)cb.onAssistantComplete(starId,spoken);

            // Persist a restart-safe marker BEFORE starting the protected review. No relationship/memory
            // mutation has happened yet; if Android dies here, startup replays this exact primary review.
            JSONObject pending=new JSONObject().put("StarId",starId).put("PlayerText",playerText).put("StarResponse",spoken)
                    .put("TurnStartedAt",turnStartedAt).put("QueuedAt",OffsetDateTime.now().toString()).put("RequestType","Chat");
            synchronized(stateStore){JSONObject live=stateStore.star(starId);live.put("StandalonePendingPrimaryReview",pending);stateStore.persist();}
            queueProtectedPrimaryReview(profile,stateBefore,pending,cb);
            status(cb,"Speech is flowing; protected memory review is running.");
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
                    trace.log("primary-review.llm-start","star="+starId+" attempt="+attempt);
                    List<ChatMessage> messages=prompts.buildReview(profile,stateBefore,pending.optString("PlayerText"),pending.optString("StarResponse"),pending.optString("TurnStartedAt"));
                    String json=llm.completeJson(messages,settings.reviewMaxTokens());
                    review=JsonUtil.parseObject(json); trace.log("primary-review.llm-complete","star="+starId+" attempt="+attempt+" chars="+json.length()); break;
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
            status(cb,"Memory review committed. Ready for the next turn.");
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
        if(!settings.ttsEnabled()||settings.ttsKey().isEmpty())return;
        final JSONObject p;
        try{p=JsonUtil.deepCopy(profile);}catch(Exception e){return;}
        ttsExecutor.submit(()->{try{trace.log("tts.start","star="+p.optString("starId")+" chars="+sentence.length());AlibabaQwenTtsClient.AudioResult generated=tts.synthesize(p,sentence);trace.log("tts.generated","bytes="+generated.bytes.length);audio.playBlocking(generated);trace.log("tts.playback-complete","star="+p.optString("starId"));}catch(Throwable e){trace.log("tts.failed",e.toString());error(cb,"TTS",e);}});
    }

    private void waitForPrimaryReview(Callbacks cb)throws Exception{
        if(!settings.blockForReview())return;
        CompletableFuture<Void> barrier; synchronized(barrierLock){barrier=primaryReviewBarrier;}
        if(barrier.isDone()){
            try{barrier.get();}catch(ExecutionException e){throw new IllegalStateException("The previous protected memory review did not commit. Restart after correcting provider/configuration errors; the pending review is preserved on device.",e.getCause());}
            return;
        }
        trace.log("foreground.wait-primary-review","waiting=true");status(cb,"Finishing the previous protected memory update...");
        try{barrier.get();trace.log("foreground.wait-primary-review-complete","ok=true");}
        catch(ExecutionException e){throw new IllegalStateException("The previous protected memory review did not commit. The next foreground is intentionally blocked.",e.getCause());}
    }

    private void recoverPendingPrimaryReviews() throws Exception {
        List<JSONObject> pendingStates=new ArrayList<>();
        for(String id:StarProfileRepository.IDS){JSONObject state=stateStore.star(id);JSONObject pending=state.optJSONObject("StandalonePendingPrimaryReview");if(pending!=null&&pending.length()>0)pendingStates.add(new JSONObject().put("StarId",id).put("Pending",JsonUtil.deepCopy(pending)).put("State",JsonUtil.deepCopy(state)));}
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
        JSONObject review=JsonUtil.parseObject(llm.completeJson(messages,settings.reviewMaxTokens()));
        synchronized(stateStore){JSONObject live=stateStore.star(starId);memory.apply(profile,live,review,pending.optString("PlayerText"),pending.optString("StarResponse"),pending.optString("TurnStartedAt"));live.remove("StandalonePendingPrimaryReview");stateStore.persist();}
        trace.log("primary-review.recovery-commit","star="+starId);
    }

    private static ThreadFactory named(String base){return r->{Thread t=new Thread(r,base);t.setDaemon(true);return t;};}
    private static void status(Callbacks cb,String text){if(cb!=null)try{cb.onStatus(text);}catch(Exception ignored){}}
    private static void error(Callbacks cb,String op,Throwable e){if(cb!=null)try{cb.onError(op,e);}catch(Exception ignored){}}

    @Override public void close(){closed=true;foregroundExecutor.shutdownNow();ioExecutor.shutdownNow();ttsExecutor.shutdownNow();maintenanceExecutor.shutdownNow();memoryExecutor.shutdown();}
}
