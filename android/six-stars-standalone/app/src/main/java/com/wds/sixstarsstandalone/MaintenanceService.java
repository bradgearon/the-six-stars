package com.wds.sixstarsstandalone;

import org.json.JSONObject;

import java.time.OffsetDateTime;
import java.util.List;

/**
 * Low-priority living-state generation after a protected direct-turn memory review has committed.
 * This is event-driven staleness maintenance, not a timer/polling loop: it runs only after a committed
 * interaction and generates daily/hourly state only when the corresponding saved state is stale.
 */
final class MaintenanceService {
    static final class Result { JSONObject todayState; JSONObject girlfriendHourlyState; boolean hasChanges(){return todayState!=null||girlfriendHourlyState!=null;} }

    private final StandaloneSettings settings;
    private final AndroidPromptBuilder prompts;
    private final GoogleAiStudioClient llm;
    private final RuntimeTrace trace;

    MaintenanceService(StandaloneSettings settings, AndroidPromptBuilder prompts, GoogleAiStudioClient llm, RuntimeTrace trace) {
        this.settings=settings; this.prompts=prompts; this.llm=llm; this.trace=trace;
    }

    Result refreshIfNeeded(JSONObject profile, JSONObject stateSnapshot) {
        Result result=new Result();
        try {
            if (settings.dailyStateEnabled() && dailyStale(stateSnapshot)) result.todayState=generateDaily(profile,stateSnapshot);
            if (settings.girlfriendHourlyEnabled() && stateSnapshot.optBoolean("IsGirlfriend",false) && girlfriendHourlyStale(stateSnapshot)) result.girlfriendHourlyState=generateGirlfriendHourly(profile,stateSnapshot);
        } catch (Exception e) {
            trace.log("maintenance.failed", e.getClass().getSimpleName()+": "+e.getMessage());
        }
        return result;
    }

    private boolean dailyStale(JSONObject state) {
        JSONObject today=state.optJSONObject("TodayState");
        if(today==null||today.length()==0)return true;
        String localDate=today.optString("LocalDate",today.optString("localDate",""));
        return !OffsetDateTime.now().toLocalDate().toString().equals(localDate);
    }

    private boolean girlfriendHourlyStale(JSONObject state) {
        JSONObject hourly=state.optJSONObject("GirlfriendHourlyState");
        if(hourly==null||hourly.length()==0)return true;
        String generated=hourly.optString("GeneratedAt",hourly.optString("generatedAt",""));
        try { return OffsetDateTime.parse(generated).isBefore(OffsetDateTime.now().minusHours(1)); }
        catch(Exception e){return true;}
    }

    private JSONObject generateDaily(JSONObject profile, JSONObject state) throws Exception {
        trace.log("maintenance.daily.start",profile.optString("starId"));
        List<ChatMessage> messages=prompts.buildDailyState(profile,state,"standalone state missing or stale");
        JSONObject stored=JsonUtil.parseObject(llm.completeJson(messages));
        OffsetDateTime now=OffsetDateTime.now();
        stored.put("Version",1).put("StarId",profile.optString("starId")).put("StarName",profile.optString("displayName",profile.optString("starId")))
                .put("LocalDate",now.toLocalDate().toString()).put("SlotId",daySlot(now.getHour())).put("GeneratedAt",now.toString())
                .put("GenerationReason","standalone current-day refresh").put("SourceInteractionAt",state.opt("LastInteractionAt"));
        trace.log("maintenance.daily.complete",profile.optString("starId")); return stored;
    }

    private JSONObject generateGirlfriendHourly(JSONObject profile, JSONObject state) throws Exception {
        trace.log("maintenance.girlfriend-hourly.start",profile.optString("starId"));
        List<ChatMessage> messages=prompts.buildGirlfriendHourly(profile,state,"standalone hourly state missing or stale");
        JSONObject stored=JsonUtil.parseObject(llm.completeJson(messages));
        OffsetDateTime now=OffsetDateTime.now();
        stored.put("GeneratedAt",now.toString()).put("SlotId",now.withMinute(0).withSecond(0).withNano(0).toString()).put("GenerationReason","standalone girlfriend hourly refresh");
        trace.log("maintenance.girlfriend-hourly.complete",profile.optString("starId")); return stored;
    }

    private static String daySlot(int hour){if(hour<6)return"overnight";if(hour<12)return"morning";if(hour<17)return"afternoon";if(hour<22)return"evening";return"night";}
}
