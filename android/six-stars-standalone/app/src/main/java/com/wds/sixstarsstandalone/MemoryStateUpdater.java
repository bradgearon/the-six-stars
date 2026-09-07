package com.wds.sixstarsstandalone;

import org.json.JSONArray;
import org.json.JSONObject;

import java.time.OffsetDateTime;
import java.time.ZoneOffset;
import java.time.temporal.ChronoUnit;
import java.util.*;

/**
 * Android equivalent of the direct-turn portion of StarMemoryApi.UpdateStarWithInteractionAsync.
 * The private review supplies evidence; this class owns durable state mutation and deterministic H/T/R.
 */
final class MemoryStateUpdater {
    private final StandaloneSettings settings;
    private final DeterministicRelationshipEngine relationship;

    MemoryStateUpdater(StandaloneSettings settings) {
        this.settings = settings;
        this.relationship = new DeterministicRelationshipEngine(settings);
    }

    synchronized JSONObject apply(JSONObject profile, JSONObject state, JSONObject review,
                                  String playerText, String spokenText, String turnStartedAt) throws Exception {
        OffsetDateTime now = OffsetDateTime.now(ZoneOffset.UTC);
        OffsetDateTime logical = parseTime(turnStartedAt, now).withOffsetSameInstant(ZoneOffset.UTC);
        pruneShortTerm(state, now);

        boolean isLatest = true;
        OffsetDateTime previousLast = parseNullableTime(state.opt("LastInteractionAt"));
        if (previousLast != null && logical.isBefore(previousLast)) isLatest = false;

        double connectionBefore = state.optDouble("EmotionalConnectionPercent", 0);
        DeterministicRelationshipEngine.Result calculated = relationship.calculate(profile, state, review, playerText);
        JSONObject delta = calculated.delta;
        state.put("Heart", clampInt(state.optInt("Heart",0) + delta.optInt("Heart",0), 0, 9999));
        state.put("Trust", clampInt(state.optInt("Trust",0) + delta.optInt("Trust",0), 0, 9999));
        state.put("Romance", clampInt(state.optInt("Romance",0) + delta.optInt("Romance",0), 0, 9999));
        state.put("BenaveilInfluence", clampInt(state.optInt("BenaveilInfluence",0) + delta.optInt("BenaveilInfluence",0), -9999, 9999));
        double connectionAfter = clamp(connectionBefore + delta.optDouble("EmotionalConnectionPercent",0), 0, 100);
        state.put("LastEmotionalConnectionPercentDelta", connectionAfter - connectionBefore);
        state.put("EmotionalConnectionPercent", connectionAfter);

        state.put("InteractionCount", state.optInt("InteractionCount",0) + 1);
        if (previousLast == null || logical.isAfter(previousLast)) state.put("LastInteractionAt", logical.toString());
        state.put("LastDailyStateSourceUpdateAt", now.toString());
        updateGirlfriendReadiness(state, now);

        String summary = review.optString("conversationSummary", "").trim();
        if (isLatest && !summary.isEmpty()) state.put("ConversationSummary", summary);
        if (isLatest) {
            state.put("LastEmotionalEffectVector", review.optString("emotionalEffectVector", ""));
            state.put("LastNegativeEmotionVector", review.optString("negativeEmotionVector", ""));
            state.put("LastDeterministicEmotionSummary", calculated.summary);
            state.put("LastTopAffectedEmotions", calculated.topEmotions);
        }

        addUniqueStrings(array(state,"MemoryNotes"), review.optJSONArray("memoryNotes"), 120);
        addShortTerm(state, review.optJSONArray("shortTermMemoryItems"), logical, now, "Chat");
        if (state.optBoolean("IsGirlfriend",false) && review.optBoolean("isSexting",false)) state.put("GirlfriendSextingEnabled", true);

        JSONArray learned = array(state,"ThingsLearnedOrSuggestedByPlayer");
        JSONArray newlyLearned = addUniqueStringsWithResult(learned, review.optJSONArray("thingsLearnedOrSuggestedByPlayer"), 200);
        updateUnresolved(state, review, now);
        JSONArray markersStored = upsertMarkers(state, review.optJSONArray("rememberedMarkers"), now);
        markReferencedMarkers(state, review.optJSONArray("referencedMemoryMarkerIds"), now);
        addUniqueStrings(array(state,"SecretsUnlocked"), review.optJSONArray("secretsUnlocked"), 100);
        addUniqueStrings(array(state,"RelationshipEvents"), review.optJSONArray("relationshipEvents"), 160);
        if (state.optBoolean("IsGirlfriend",false)) addUniqueStrings(array(state,"GirlfriendHistory"), review.optJSONArray("girlfriendHistoryUpdates"), 160);
        addUniqueStrings(array(state,"PetNamesPlayerUsesForStar"), review.optJSONArray("petNamesPlayerUsesForStar"), 20);
        addUniqueStrings(array(state,"PetNamesStarUsesForPlayer"), review.optJSONArray("petNamesStarUsesForPlayer"), 20);
        updateGirlfriendNeeds(state, review, now);
        updateCoupleActivity(state, review, now);
        upsertLearnedFunctions(state, review.optJSONArray("learnedFunctions"), now);
        updateCombos(state, calculated.combos, now);
        updateSuccession(state, delta, calculated, now);
        updateEmotionHistory(state, review, calculated, delta, logical, connectionBefore, connectionAfter);
        updateStory(state, review, now);

        JSONArray selectedQuotes = selectImportantPlayerQuotes(review.optJSONArray("importantPlayerQuotes"), playerText, 3);
        addUniqueStrings(array(state,"ImportantPlayerQuotes"), selectedQuotes, 80);
        addImportantQuoteRecords(state, selectedQuotes, profile.optString("starId", ""), review, delta, calculated.combos, markersStored, logical);

        JSONObject entry = new JSONObject();
        entry.put("CreatedAt", logical.toString());
        entry.put("RequestType", "Chat");
        entry.put("PlayerText", truncate(playerText,8000));
        entry.put("StarResponse", truncate(spokenText,24000));
        entry.put("Summary", truncate(firstNonEmpty(review.optString("changesSummary",""), summary),4000));
        entry.put("ActualDelta", new JSONObject(delta.toString()));
        entry.put("SuggestedDelta", review.optJSONObject("suggestedStateDeltas") == null ? new JSONObject() : review.optJSONObject("suggestedStateDeltas"));
        entry.put("EmotionalConnectionPercentBefore", connectionBefore);
        entry.put("EmotionalConnectionPercentAfter", connectionAfter);
        entry.put("IsGirlfriendReadyAfter", state.optBoolean("IsGirlfriendReady",false));
        entry.put("EmotionalEffectVector", review.optString("emotionalEffectVector",""));
        entry.put("NegativeEmotionVector", review.optString("negativeEmotionVector",""));
        entry.put("DeterministicEmotionSummary", calculated.summary);
        entry.put("WisdomScore", review.optDouble("wisdomScore",0));
        entry.put("ImportantQuotes", copyArray(selectedQuotes));
        entry.put("ThingsLearnedOrSuggestedByPlayer", newlyLearned);
        entry.put("RememberedMarkers", markersStored);
        entry.put("ReferencedMemoryMarkerIds", copyArray(review.optJSONArray("referencedMemoryMarkerIds")));
        entry.put("Tags", new JSONArray(calculated.combos.toString()));
        entry.put("RelationshipEvents", copyArray(review.optJSONArray("relationshipEvents")));
        entry.put("AcceptedPositiveSignals", new JSONArray(calculated.positiveSignals.toString()));
        entry.put("AcceptedNegativeSignals", new JSONArray(calculated.negativeSignals.toString()));
        entry.put("AcceptedCombos", new JSONArray(calculated.combos.toString()));
        entry.put("SecretsUnlocked", copyArray(review.optJSONArray("secretsUnlocked")));

        JSONArray memoryLog = array(state,"MemoryLog");
        memoryLog.put(entry);
        sortObjectsDescending(memoryLog,"CreatedAt");
        trim(memoryLog, 400);
        storeRollups(state, review, now);

        state.put("StandaloneLastReview", new JSONObject()
                .put("CommittedAt",now.toString())
                .put("TurnStartedAt",logical.toString())
                .put("IsSexting",review.optBoolean("isSexting",false))
                .put("InformationNeedsVector",review.optString("informationNeedsVector",""))
                .put("EmotionAnimation",review.optString("emotionAnimation",""))
                .put("RequestToShowBoobs",review.optBoolean("requestToShowBoobs",false))
                .put("RiskFlags",copyArray(review.optJSONArray("riskFlags")))
                .put("LoreUsed",copyArray(review.optJSONArray("loreUsed"))));
        return entry;
    }

    private void updateGirlfriendReadiness(JSONObject state, OffsetDateTime now) throws Exception {
        boolean ready = state.optBoolean("IsGirlfriend",false) || (state.optBoolean("IsAlly",false)
                && state.optDouble("EmotionalConnectionPercent",0) >= 25
                && state.optInt("Heart",0) >= 6 && state.optInt("Trust",0) >= 4 && state.optInt("Romance",0) >= 3);
        boolean before=state.optBoolean("IsGirlfriendReady",false);
        state.put("IsGirlfriendReady",ready);
        if (ready && !before) {
            state.put("GirlfriendReadySince",now.toString());
            prependUnique(array(state,"RelationshipEvents"),"Girlfriend readiness gate reached: ally + emotional connection + heart/trust/romance thresholds.",160);
        } else if (!ready && before && !state.optBoolean("IsGirlfriend",false)) {
            state.put("GirlfriendReadySince",JSONObject.NULL);
            prependUnique(array(state,"RelationshipEvents"),"Girlfriend readiness gate no longer met after connection/relationship change.",160);
        }
        state.put("GirlfriendReadinessSummary", ready
                ? "Ready for girlfriend path: ask directly, and she can decide based on this bond."
                : "Not ready: requires ally status, EmotionalConnectionPercent >= 25, Heart >= 6, Trust >= 4, and Romance >= 3. Build ally trust, safe affection, and personality-matched emotional vectors.");
    }

    private void addShortTerm(JSONObject state, JSONArray items, OffsetDateTime created, OffsetDateTime pruneAt, String source) throws Exception {
        pruneShortTerm(state, pruneAt);
        if(items==null)return;
        JSONArray target=array(state,"ShortTermMemory");
        for(int i=0;i<items.length();i++){
            String text=items.optString(i,"").trim(); if(text.isEmpty())continue;
            boolean exists=false; for(int j=0;j<target.length();j++){JSONObject x=target.optJSONObject(j);if(x!=null&&x.optString("Text").equalsIgnoreCase(text))exists=true;}
            if(exists)continue;
            target.put(new JSONObject().put("CreatedAt",created.toString()).put("ExpiresAt",created.plusMinutes(settings.shortTermMemoryMinutes()).toString()).put("Text",truncate(text,500)).put("Source",source));
        }
        sortObjectsDescending(target,"CreatedAt"); trim(target,64);
    }

    private static void pruneShortTerm(JSONObject state, OffsetDateTime now) throws Exception {
        JSONArray source=array(state,"ShortTermMemory"), keep=new JSONArray();
        for(int i=0;i<source.length();i++){JSONObject x=source.optJSONObject(i);if(x==null)continue;OffsetDateTime ex=parseNullableTime(x.opt("ExpiresAt"));if(ex==null||ex.isAfter(now))keep.put(x);}
        replaceArray(source,keep);
    }

    private static void updateUnresolved(JSONObject state, JSONObject review, OffsetDateTime now) throws Exception {
        upsertVector(array(state,"IncomingUnresolvedVectors"), review.optString("questionsFromLastCatVector",""), "in", now);
        upsertVector(array(state,"OutgoingUnresolvedVectors"), review.optString("questionsToLastCatVector",""), "out", now);
        String resolution=review.optString("questionResolutionVector","");
        if(!resolution.trim().isEmpty()){
            for(String raw:resolution.split("~")){String[]p=raw.split("\\^",-1);if(p.length<1)continue;String id=p[0].trim();if(id.isEmpty())continue;removeVector(array(state,"IncomingUnresolvedVectors"),id);removeVector(array(state,"OutgoingUnresolvedVectors"),id);prependObject(array(state,"UnresolvedResolutionHistory"),new JSONObject().put("Id",id).put("ResolvedAt",now.toString()).put("Raw",raw),100);}
        }
        int total=array(state,"IncomingUnresolvedVectors").length()+array(state,"OutgoingUnresolvedVectors").length();
        while(total>10){JSONArray a=array(state,"IncomingUnresolvedVectors").length()>=array(state,"OutgoingUnresolvedVectors").length()?array(state,"IncomingUnresolvedVectors"):array(state,"OutgoingUnresolvedVectors");if(a.length()==0)break;a.remove(a.length()-1);total--;}
    }

    private static void upsertVector(JSONArray target,String vector,String direction,OffsetDateTime now)throws Exception{
        if(vector==null||vector.trim().isEmpty())return;
        for(String raw:vector.split("~")){String[]p=raw.split("\\^",-1);String id=p.length==0?"":p[0].trim();if(id.isEmpty())continue;JSONObject found=null;for(int i=0;i<target.length();i++){JSONObject x=target.optJSONObject(i);if(x!=null&&x.optString("Id").equalsIgnoreCase(id)){found=x;break;}}if(found==null){found=new JSONObject();target.put(found);}found.put("Id",id).put("Direction",direction).put("Raw",raw).put("UpdatedAt",now.toString());}
        sortObjectsDescending(target,"UpdatedAt");
    }

    private static void removeVector(JSONArray a,String id){for(int i=a.length()-1;i>=0;i--){JSONObject x=a.optJSONObject(i);if(x!=null&&x.optString("Id").equalsIgnoreCase(id))a.remove(i);}}

    private static JSONArray upsertMarkers(JSONObject state,JSONArray incoming,OffsetDateTime now)throws Exception{
        JSONArray stored=new JSONArray();if(incoming==null)return stored;JSONArray target=array(state,"RememberedMarkers");
        for(int i=0;i<incoming.length();i++){JSONObject raw=incoming.optJSONObject(i);if(raw==null)continue;JSONObject m=new JSONObject(raw.toString());String id=firstNonEmpty(m.optString("id",""),slug(firstNonEmpty(m.optString("markerName",""),m.optString("description","marker"))));if(id.isEmpty())continue;m.put("Id",id).put("MarkerName",m.optString("markerName",m.optString("MarkerName",""))).put("Description",m.optString("description",m.optString("Description",""))).put("LocationText",m.optString("locationText",m.optString("LocationText",""))).put("Coordinates",m.optString("coordinates",m.optString("Coordinates",""))).put("Area",m.optString("area",m.optString("Area",""))).put("SourcePlayerQuote",m.optString("sourcePlayerQuote",m.optString("SourcePlayerQuote",""))).put("ReminderText",m.optString("reminderText",m.optString("ReminderText",""))).put("Confidence",m.optDouble("confidence",m.optDouble("Confidence",0))).put("IsActive",m.optBoolean("isActive",m.optBoolean("IsActive",true))).put("UpdatedAt",now.toString());
            JSONObject existing=null;for(int j=0;j<target.length();j++){JSONObject x=target.optJSONObject(j);if(x!=null&&x.optString("Id").equalsIgnoreCase(id)){existing=x;break;}}if(existing==null){target.put(m);stored.put(m);}else{JsonUtil.mergeObject(existing,m);stored.put(existing);}}
        sortObjectsDescending(target,"UpdatedAt");trim(target,100);return stored;
    }

    private static void markReferencedMarkers(JSONObject state,JSONArray ids,OffsetDateTime now)throws Exception{if(ids==null)return;JSONArray markers=array(state,"RememberedMarkers");for(int i=0;i<ids.length();i++){String id=ids.optString(i,"");for(int j=0;j<markers.length();j++){JSONObject m=markers.optJSONObject(j);if(m!=null&&m.optString("Id").equalsIgnoreCase(id))m.put("LastReferencedAt",now.toString());}}}

    private static void upsertLearnedFunctions(JSONObject state,JSONArray incoming,OffsetDateTime now)throws Exception{if(incoming==null)return;JSONArray target=array(state,"LearnedFunctions");for(int i=0;i<incoming.length();i++){JSONObject raw=incoming.optJSONObject(i);if(raw==null)continue;String id=firstNonEmpty(raw.optString("id",""),slug(raw.optString("name","")));if(id.isEmpty())continue;JSONObject x=null;for(int j=0;j<target.length();j++){JSONObject q=target.optJSONObject(j);if(q!=null&&q.optString("Id").equalsIgnoreCase(id)){x=q;break;}}if(x==null){x=new JSONObject();target.put(x);}x.put("Id",id).put("Name",raw.optString("name",x.optString("Name",""))).put("Description",raw.optString("description",x.optString("Description",""))).put("SourcePlayerQuote",raw.optString("sourcePlayerQuote",x.optString("SourcePlayerQuote",""))).put("Confidence",Math.max(x.optDouble("Confidence",0),raw.optDouble("confidence",0))).put("IsActive",raw.optBoolean("isActive",true)).put("LearnedAt",x.optString("LearnedAt",now.toString()));}trim(target,100);}

    private static void updateGirlfriendNeeds(JSONObject state,JSONObject review,OffsetDateTime now)throws Exception{
        if(!state.optBoolean("IsGirlfriend",false))return;
        JSONArray needs=review.optJSONArray("girlfriendNeedsMetVector"),wants=review.optJSONArray("girlfriendWantsMetVector");
        if((needs==null||needs.length()==0)&&(wants==null||wants.length()==0)&&review.optString("girlfriendNeedsMetSummary","").isEmpty())return;
        JSONObject event=new JSONObject().put("CreatedAt",now.toString()).put("Summary",review.optString("girlfriendNeedsMetSummary","")).put("Needs",copyArray(needs)).put("Wants",copyArray(wants));
        prependObject(array(state,"GirlfriendNeedsMetHistory"),event,120);
        mergePerformance(array(state,"GirlfriendNeedsMetPerformanceVector"),needs,now);mergePerformance(array(state,"GirlfriendWantsMetPerformanceVector"),wants,now);
    }

    private static void mergePerformance(JSONArray target,JSONArray items,OffsetDateTime now)throws Exception{if(items==null)return;for(int i=0;i<items.length();i++){JSONObject item=items.optJSONObject(i);if(item==null)continue;String id=item.optString("itemId","");if(id.isEmpty())continue;JSONObject x=null;for(int j=0;j<target.length();j++){JSONObject q=target.optJSONObject(j);if(q!=null&&q.optString("ItemId").equalsIgnoreCase(id)){x=q;break;}}if(x==null){x=new JSONObject().put("ItemId",id).put("Count",0).put("TotalScore",0.0);target.put(x);}x.put("DisplayName",item.optString("displayName",x.optString("DisplayName",id))).put("Count",x.optInt("Count",0)+1).put("TotalScore",x.optDouble("TotalScore",0)+item.optDouble("score",0)).put("LastConfidence",item.optDouble("confidence",0)).put("LastReason",item.optString("reason","")).put("LastUpdatedAt",now.toString());}}

    private static void updateCoupleActivity(JSONObject state,JSONObject review,OffsetDateTime now)throws Exception{if(!state.optBoolean("IsGirlfriend",false))return;String vector=review.optString("girlfriendCoupleActivityVector","").trim();String summary=review.optString("girlfriendCoupleActivitySummary","").trim();if(vector.isEmpty()&&summary.isEmpty())return;prependObject(array(state,"GirlfriendCoupleActivityHistory"),new JSONObject().put("CreatedAt",now.toString()).put("Summary",summary).put("Vector",vector),120);if(!vector.isEmpty()){for(String raw:vector.split(";")){String[]p=raw.split(":",-1);if(p.length<2)continue;String id=p[0].trim();double score=parseDouble(p[1]);if(id.isEmpty()||score<=0)continue;JSONObject x=findBy(array(state,"GirlfriendCoupleActivityScores"),"ActivityId",id);if(x==null){x=new JSONObject().put("ActivityId",id).put("Count",0).put("TotalScore",0.0);array(state,"GirlfriendCoupleActivityScores").put(x);}x.put("Count",x.optInt("Count",0)+1).put("TotalScore",x.optDouble("TotalScore",0)+score).put("LastSeenAt",now.toString());}}}

    private static void updateCombos(JSONObject state,JSONArray combos,OffsetDateTime now)throws Exception{if(combos==null)return;for(int i=0;i<combos.length();i++){String id=combos.optString(i,"");if(id.isEmpty())continue;JSONObject x=findBy(array(state,"RelationshipCombos"),"ComboId",id);if(x==null){x=new JSONObject().put("ComboId",id).put("Count",0);array(state,"RelationshipCombos").put(x);}x.put("Count",x.optInt("Count",0)+1).put("LastSeenAt",now.toString());}}

    private static void updateSuccession(JSONObject state,JSONObject delta,DeterministicRelationshipEngine.Result r,OffsetDateTime now)throws Exception{boolean pos=delta.optInt("Heart",0)>0||delta.optInt("Trust",0)>0||delta.optDouble("EmotionalConnectionPercent",0)>0||r.positiveSignals.length()>0;boolean romantic=delta.optInt("Romance",0)>0||containsAny(r.combos,"romantic","affection","tender","vulnerability");boolean hurt=delta.optInt("Heart",0)<0||delta.optInt("Trust",0)<0||r.negativeSignals.length()>0;state.put("PositiveSuccessionCount",pos?state.optInt("PositiveSuccessionCount",0)+1:0);state.put("RomanticSuccessionCount",romantic?state.optInt("RomanticSuccessionCount",0)+1:0);state.put("HurtSuccessionCount",hurt?state.optInt("HurtSuccessionCount",0)+1:0);if(pos)state.put("LastPositiveInteractionAt",now.toString());if(romantic)state.put("LastRomanticInteractionAt",now.toString());if(hurt)state.put("LastHurtInteractionAt",now.toString());}

    private static void updateEmotionHistory(JSONObject state,JSONObject review,DeterministicRelationshipEngine.Result r,JSONObject delta,OffsetDateTime turn,double before,double after)throws Exception{JSONObject e=new JSONObject().put("CreatedAt",turn.toString()).put("EmotionalEffectVector",review.optString("emotionalEffectVector","")).put("NegativeEmotionVector",review.optString("negativeEmotionVector","")).put("ActualDelta",new JSONObject(delta.toString())).put("ConnectionBefore",before).put("ConnectionAfter",after).put("AcceptedPositiveSignals",new JSONArray(r.positiveSignals.toString())).put("AcceptedNegativeSignals",new JSONArray(r.negativeSignals.toString())).put("AcceptedCombos",new JSONArray(r.combos.toString())).put("Summary",r.summary);prependObject(array(state,"EmotionUpdateHistory"),e,256);}

    private static void updateStory(JSONObject state,JSONObject review,OffsetDateTime now)throws Exception{String id=review.optString("storyId","").trim(),title=review.optString("storyTitle","").trim(),summary=review.optString("storySummary","").trim();if(id.isEmpty()&&title.isEmpty()&&summary.isEmpty())return;if(id.isEmpty())id=slug(firstNonEmpty(title,summary));JSONObject s=findBy(array(state,"StoryHistory"),"StoryId",id);if(s==null){s=new JSONObject().put("StoryId",id);array(state,"StoryHistory").put(s);}s.put("Title",title).put("Summary",summary).put("IsFinished",review.optBoolean("storyIsFinished",true)).put("PartIndex",review.optInt("storyPartIndex",0)).put("UpdatedAt",now.toString());}

    private static JSONArray selectImportantPlayerQuotes(JSONArray proposed, String playerText, int maxItems) {
        JSONArray out = new JSONArray();
        String player = playerText == null ? "" : playerText.trim();
        if (proposed == null || player.isEmpty() || maxItems <= 0) return out;
        for (int i = 0; i < proposed.length() && out.length() < maxItems; i++) {
            String quote = proposed.optString(i, "").replace('\r',' ').replace('\n',' ').trim();
            if (quote.isEmpty() || quote.length() > 700) continue;
            // The background LLM decides significance; Android only verifies that an exact quote
            // was actually spoken in this turn before making it durable.
            if (!player.contains(quote)) continue;
            boolean duplicate = false;
            for (int j = 0; j < out.length(); j++) if (quote.equals(out.optString(j))) { duplicate = true; break; }
            if (!duplicate) out.put(quote);
        }
        return out;
    }

    private static void addImportantQuoteRecords(JSONObject state, JSONArray quotes, String starId, JSONObject review, JSONObject delta, JSONArray combos, JSONArray markers, OffsetDateTime createdAt) throws Exception {
        if (quotes == null || quotes.length() == 0) return;
        JSONArray archive = array(state, "AllImportantPlayerQuotes");
        for (int i = 0; i < quotes.length(); i++) {
            String quote = quotes.optString(i, "").trim();
            if (quote.isEmpty()) continue;
            boolean exists = false;
            for (int j = 0; j < archive.length(); j++) {
                JSONObject old = archive.optJSONObject(j);
                if (old != null && quote.equalsIgnoreCase(old.optString("Quote", ""))) { exists = true; break; }
            }
            if (exists) continue;
            JSONArray markerIds = new JSONArray();
            if (markers != null) for (int j = 0; j < markers.length(); j++) {
                JSONObject marker = markers.optJSONObject(j);
                if (marker != null && !marker.optString("Id", "").isEmpty()) markerIds.put(marker.optString("Id"));
            }
            JSONObject rec = new JSONObject()
                    .put("Id", (starId + "_quote_" + createdAt.toInstant().toEpochMilli() + "_" + i).toLowerCase(Locale.ROOT))
                    .put("CreatedAt", createdAt.toString())
                    .put("StarId", starId)
                    .put("Quote", quote)
                    .put("Reason", "background-review-significant-player-quote")
                    .put("RequestType", "Chat")
                    .put("EmotionalEffectVector", review.optString("emotionalEffectVector", ""))
                    .put("NegativeEmotionVector", review.optString("negativeEmotionVector", ""))
                    .put("WisdomScore", review.optDouble("wisdomScore", 0))
                    .put("ActualDelta", new JSONObject(delta.toString()))
                    .put("Tags", copyArray(combos))
                    .put("MarkerIds", markerIds);
            prependObject(archive, rec, 240);
        }
    }

    private static void storeRollups(JSONObject state,JSONObject review,OffsetDateTime now)throws Exception{int count=state.optInt("InteractionCount",0);String ten=review.optString("tenTurnConversationSummary","").trim();if(count%10==0&&!ten.isEmpty())prependObject(array(state,"TenTurnConversationSummaries"),new JSONObject().put("StartTurn",Math.max(1,count-9)).put("EndTurn",count).put("Summary",ten).put("CreatedAt",now.toString()),20);String hundred=review.optString("hundredTurnConversationSummary","").trim();if(count%100==0&&!hundred.isEmpty())prependObject(array(state,"HundredTurnConversationSummaries"),new JSONObject().put("StartTurn",Math.max(1,count-99)).put("EndTurn",count).put("Summary",hundred).put("CreatedAt",now.toString()),10);}

    private static JSONObject findBy(JSONArray a,String key,String value){for(int i=0;i<a.length();i++){JSONObject x=a.optJSONObject(i);if(x!=null&&x.optString(key).equalsIgnoreCase(value))return x;}return null;}
    private static JSONArray array(JSONObject o,String key)throws Exception{JSONArray a=o.optJSONArray(key);if(a==null){a=new JSONArray();o.put(key,a);}return a;}
    private static JSONArray addUniqueStringsWithResult(JSONArray target,JSONArray incoming,int max)throws Exception{JSONArray out=new JSONArray();if(incoming!=null)for(int i=0;i<incoming.length();i++){String s=incoming.optString(i,"").trim();if(s.isEmpty()||containsIgnoreCase(target,s))continue;target.put(s);out.put(s);}trimFromFront(target,max);return out;}
    private static void addUniqueStrings(JSONArray target,JSONArray incoming,int max)throws Exception{addUniqueStringsWithResult(target,incoming,max);}
    private static void addUniqueString(JSONArray target,String value,int max)throws Exception{if(value==null||value.trim().isEmpty()||containsIgnoreCase(target,value.trim()))return;target.put(value.trim());trimFromFront(target,max);}
    private static void prependUnique(JSONArray target,String value,int max)throws Exception{if(containsIgnoreCase(target,value))return;JSONArray n=new JSONArray().put(value);for(int i=0;i<target.length()&&n.length()<max;i++)n.put(target.get(i));replaceArray(target,n);}
    private static boolean containsIgnoreCase(JSONArray a,String s){for(int i=0;i<a.length();i++)if(a.optString(i,"").equalsIgnoreCase(s))return true;return false;}
    private static void prependObject(JSONArray target,JSONObject value,int max)throws Exception{JSONArray n=new JSONArray().put(value);for(int i=0;i<target.length()&&n.length()<max;i++)n.put(target.get(i));replaceArray(target,n);}
    private static void trim(JSONArray a,int max){while(a.length()>max)a.remove(a.length()-1);}
    private static void trimFromFront(JSONArray a,int max){while(a.length()>max)a.remove(0);}
    private static void replaceArray(JSONArray target,JSONArray source)throws Exception{while(target.length()>0)target.remove(target.length()-1);for(int i=0;i<source.length();i++)target.put(source.get(i));}
    private static JSONArray copyArray(JSONArray a){try{return a==null?new JSONArray():new JSONArray(a.toString());}catch(Exception e){return new JSONArray();}}
    private static void sortObjectsDescending(JSONArray a,String key)throws Exception{List<JSONObject>l=new ArrayList<>();for(int i=0;i<a.length();i++){JSONObject x=a.optJSONObject(i);if(x!=null)l.add(x);}l.sort((x,y)->y.optString(key,"").compareTo(x.optString(key,"")));while(a.length()>0)a.remove(a.length()-1);for(JSONObject x:l)a.put(x);}
    private static boolean containsAny(JSONArray a,String...needles){for(int i=0;i<a.length();i++){String x=a.optString(i,"").toLowerCase(Locale.ROOT);for(String n:needles)if(x.contains(n))return true;}return false;}
    private static OffsetDateTime parseTime(String s,OffsetDateTime fallback){try{return OffsetDateTime.parse(s);}catch(Exception e){return fallback;}}
    private static OffsetDateTime parseNullableTime(Object o){if(o==null||o==JSONObject.NULL)return null;try{return OffsetDateTime.parse(String.valueOf(o));}catch(Exception e){return null;}}
    private static int clampInt(int v,int lo,int hi){return Math.max(lo,Math.min(hi,v));}
    private static double clamp(double v,double lo,double hi){return Math.max(lo,Math.min(hi,v));}
    private static double parseDouble(String s){try{return Double.parseDouble(s);}catch(Exception e){return 0;}}
    private static String truncate(String s,int n){if(s==null)return"";return s.length()<=n?s:s.substring(0,n);}
    private static String firstNonEmpty(String...v){for(String s:v)if(s!=null&&!s.trim().isEmpty())return s.trim();return"";}
    private static String slug(String v){return (v==null?"":v).trim().toLowerCase(Locale.ROOT).replaceAll("[^a-z0-9]+","_").replaceAll("^_+|_+$","");}
}
