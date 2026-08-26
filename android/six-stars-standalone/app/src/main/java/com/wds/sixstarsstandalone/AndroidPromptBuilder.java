package com.wds.sixstarsstandalone;

import org.json.JSONArray;
import org.json.JSONObject;

import java.time.OffsetDateTime;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.*;

/** Android port of the server prompt assembly. The actual prompt wording comes from the same .sbn files. */
final class AndroidPromptBuilder {
    static final String[] POSITIVE_EMOTIONS = {
            "Happy","Amused","Playful","Comforted","Safe","Protected","Trusting","Respected","Admiring","Affectionate","Vulnerable","Hopeful","Wondrous","Intrigued","Impassioned","Determined","Concerned","Confused","Unsure","Frustrated","Angry","Hurt","Sad","Melancholy","Lonely","Guilty","Suspicious","Flattered","Attracted","Tender","Yearning","Jealous"
    };
    static final String[] NEGATIVE_EMOTIONS = {"Irritated","Disrespected","Pressured","CreepedOut","Bored","Hurt","Unsafe"};

    private final ContentRepository content;
    private final StandaloneSettings settings;
    private final SbnTemplateRenderer renderer = new SbnTemplateRenderer();
    private final JSONObject mainInstructions;

    AndroidPromptBuilder(ContentRepository content, StandaloneSettings settings) throws Exception {
        this.content = content;
        this.settings = settings;
        this.mainInstructions = JsonUtil.readObject(content.file("Instructions/main-instructions.json"));
    }

    List<ChatMessage> buildSpeech(JSONObject profile, JSONObject state, String playerText) throws Exception {
        JSONObject data = baseSpeechData(profile, state, playerText);
        String system = renderer.render(content.readText("PromptTemplates/speech_system.sbn"), data);
        String user = renderer.render(content.readText("PromptTemplates/speech_user.sbn"), data);
        return Arrays.asList(new ChatMessage("system", system), new ChatMessage("user", user));
    }

    List<ChatMessage> buildReview(JSONObject profile, JSONObject stateBefore, String playerText, String spokenText, String turnStartedAt) throws Exception {
        JSONObject data = new JSONObject();
        String star = profile.optString("displayName", profile.optString("starId", "Star"));
        data.put("starDisplayName", star);
        data.put("requestType", "Chat");
        data.put("currentTimeContext", currentTimeContext(turnStartedAt));
        data.put("playerTextBlock", playerText == null ? "" : playerText);
        data.put("foregroundSpokenPartsJson", new JSONArray().put(spokenText == null ? "" : spokenText).toString());
        data.put("relationshipContext", relationshipContext(profile, stateBefore));
        data.put("recentConversationHistory", recentConversationText(stateBefore, settings.recentConversationTurns()));
        data.put("shortTermMemoryContext", shortTermMemoryText(stateBefore));
        data.put("durableMemoryContext", durableMemoryText(stateBefore));
        data.put("unresolvedStateContext", unresolvedText(stateBefore));
        data.put("storyFieldsContext", storyHistoryText(stateBefore));
        data.put("conversationSummaryCheckpointContext", buildCheckpointContext(stateBefore, playerText, spokenText));
        data.put("emotionVectorInstructions", emotionVectorInstructions(profile));
        data.put("isGirlfriend", stateBefore.optBoolean("IsGirlfriend", false));

        String system = renderer.render(content.readText("PromptTemplates/review_system.sbn"), data);
        String user = renderer.render(content.readText("PromptTemplates/review_user.sbn"), data);
        return Arrays.asList(new ChatMessage("system", system), new ChatMessage("user", user));
    }

    List<ChatMessage> buildDailyState(JSONObject profile, JSONObject state, String reason) throws Exception {
        JSONObject data = new JSONObject();
        String star = profile.optString("displayName", profile.optString("starId", "Star"));
        OffsetDateTime now = OffsetDateTime.now();
        data.put("starDisplayName", star);
        data.put("globalCanonLore", globalCanonLore());
        data.put("sessionId", settings.sessionId());
        data.put("localDate", now.toLocalDate().toString());
        data.put("slotId", daySlot(now.getHour()));
        data.put("localNow", now.format(DateTimeFormatter.ofPattern("EEEE, MMMM d, uuuu h:mm:ss a")));
        data.put("currentTimeContext", currentTimeContext(now.toString()));
        data.put("starActingCard", starActingCard(profile));
        data.put("relationshipContext", relationshipContext(profile, state));
        data.put("recentConversationTurns", recentConversationText(state, 8));
        data.put("durableMemoryContext", durableMemoryText(state));
        data.put("worldEventStream", "No current world-event feed messages are available.");
        JSONObject today = state.optJSONObject("TodayState");
        data.put("previousDailyStateJson", today == null ? "{}" : today.toString());
        String system = renderer.render(content.readText("PromptTemplates/daily_state_system.sbn"), data);
        String user = renderer.render(content.readText("PromptTemplates/daily_state_user.sbn"), data);
        return Arrays.asList(new ChatMessage("system", system), new ChatMessage("user", user));
    }

    List<ChatMessage> buildGirlfriendHourly(JSONObject profile, JSONObject state, String reason) throws Exception {
        JSONObject data = new JSONObject();
        JSONObject star = new JSONObject();
        star.put("displayName", profile.optString("displayName", profile.optString("starId", "Star")));
        star.put("element", profile.optString("element", ""));
        star.put("domain", profile.optString("domain", ""));
        star.put("coreQuote", profile.optString("coreQuote", ""));
        star.put("personality", profile.optString("personalityShort", profile.optString("deepPersonality", "")));
        star.put("backstory", profile.optString("backstorySummary", ""));
        star.put("completeBackground", profile.optString("completeBackgroundStory", ""));
        data.put("star", star);
        data.put("globalCanonLore", globalCanonLore());
        data.put("currentTimeContext", currentTimeContext(OffsetDateTime.now().toString()));
        data.put("privateRomanceProfile", privateRomanceProfile(profile));
        data.put("relationshipContext", relationshipContext(profile, state));
        data.put("girlfriendHistory", state.optJSONArray("GirlfriendHistory") == null ? new JSONArray() : state.optJSONArray("GirlfriendHistory"));
        data.put("coupleActivityContext", formatArray("Recent couple activity", state.optJSONArray("GirlfriendCoupleActivityHistory"), 6));
        data.put("petNamesPlayerUsesForHer", state.optJSONArray("PetNamesPlayerUsesForStar") == null ? new JSONArray() : state.optJSONArray("PetNamesPlayerUsesForStar"));
        data.put("petNamesSheUsesForPlayer", state.optJSONArray("PetNamesStarUsesForPlayer") == null ? new JSONArray() : state.optJSONArray("PetNamesStarUsesForPlayer"));
        data.put("compactNeedWantHistory", formatArray("Need/want history", state.optJSONArray("GirlfriendDesiredStateHistory"), 8));
        data.put("recentTurns", recentTurnObjects(state, 8));
        data.put("importantPlayerQuotes", state.optJSONArray("ImportantPlayerQuotes") == null ? new JSONArray() : state.optJSONArray("ImportantPlayerQuotes"));
        data.put("memoryNotes", state.optJSONArray("MemoryNotes") == null ? new JSONArray() : state.optJSONArray("MemoryNotes"));
        data.put("thingsLearnedOrSuggestedByPlayer", state.optJSONArray("ThingsLearnedOrSuggestedByPlayer") == null ? new JSONArray() : state.optJSONArray("ThingsLearnedOrSuggestedByPlayer"));
        data.put("relationshipEvents", state.optJSONArray("RelationshipEvents") == null ? new JSONArray() : state.optJSONArray("RelationshipEvents"));
        data.put("interstarUpdates", new JSONArray());
        JSONObject previousRaw = state.optJSONObject("GirlfriendHourlyState");
        JSONObject previous = new JSONObject();
        previous.put("available", previousRaw != null && previousRaw.length() > 0);
        previous.put("generatedAt", previousRaw == null ? "" : previousRaw.optString("GeneratedAt", previousRaw.optString("generatedAt", "")));
        previous.put("summary", previousRaw == null ? "" : previousRaw.optString("Summary", previousRaw.optString("summary", "")));
        previous.put("thingsSheWantedToSay", previousRaw == null ? new JSONArray() : firstArray(previousRaw, "ThingsSheWantsToSay", "thingsSheWantsToSay"));
        previous.put("cuteThingsSheWantedToSay", previousRaw == null ? new JSONArray() : firstArray(previousRaw, "CuteThingsSheWantsToSay", "cuteThingsSheWantsToSay"));
        data.put("previousState", previous);
        data.put("needCatalog", defaultNeedCatalog());
        data.put("wantCatalog", defaultWantCatalog());
        data.put("reason", reason == null ? "standalone hourly refresh" : reason);
        String system = renderer.render(content.readText("PromptTemplates/girlfriend_hourly_system.sbn"), data);
        String user = renderer.render(content.readText("PromptTemplates/girlfriend_hourly_user.sbn"), data);
        return Arrays.asList(new ChatMessage("system", system), new ChatMessage("user", user));
    }

    private JSONObject baseSpeechData(JSONObject profile, JSONObject state, String playerText) throws Exception {
        JSONObject data = new JSONObject();
        String star = profile.optString("displayName", profile.optString("starId", "Star"));
        data.put("starDisplayName", star);
        data.put("foregroundModeInstructions", mainInstructions.optString("systemPrompt", ""));
        data.put("plainStreamingSpeech", true);
        data.put("fastSpeechControlsEnabled", false);
        data.put("isGirlfriend", state.optBoolean("IsGirlfriend", false));
        data.put("globalCanonLore", globalCanonLore());
        data.put("privateRomanceProfile", privateRomanceProfile(profile));
        data.put("starActingVoiceCard", starActingCard(profile));
        data.put("fastConversationProfile", fastConversationProfile(profile, state));
        data.put("storyModeSystemContext", "RequestType is Chat. Long stories requested in chat should still be completed in this reply.");
        data.put("requestType", "Chat");
        data.put("romanceMode", settings.romanceMode());
        data.put("area", "Standalone relationship space");
        data.put("quest", "Spend time together and build a durable relationship that can later be imported into the game.");
        JSONObject tasks = mainInstructions.optJSONObject("taskInstructions");
        data.put("taskSpecificGuidance", tasks == null ? "Answer the player's latest input directly." : tasks.optString("Chat", "Answer the player's latest input directly."));
        JSONObject fast = mainInstructions.optJSONObject("fastSpeech");
        data.put("foregroundContractHeading", "FAST STREAMING FOREGROUND SPEECH");
        data.put("foregroundContract", fast == null ? "Return only the spoken reply and answer the Last Cat directly." : fast.optString("responseInstructions", "Return only the spoken reply and answer the Last Cat directly."));
        data.put("todayStarStateContext", todayStateText(state));
        data.put("dialogueAndMarkerContext", markerText(state));
        data.put("storyHistoryContext", storyHistoryText(state));
        data.put("importantMemoryContext", importantMemoryText(state));
        data.put("relationshipContext", relationshipContext(profile, state));
        data.put("unresolvedContinuityContext", unresolvedText(state));
        data.put("latestScreenOneLine", "");
        data.put("conversationHundredSummaries", state.optJSONArray("HundredTurnConversationSummaries") == null ? new JSONArray() : normalizeSummaryBlocks(state.optJSONArray("HundredTurnConversationSummaries")));
        data.put("conversationTenSummaries", state.optJSONArray("TenTurnConversationSummaries") == null ? new JSONArray() : normalizeSummaryBlocks(state.optJSONArray("TenTurnConversationSummaries")));
        data.put("conversationRecentTurns", recentTurnObjectsForSpeech(state, settings.recentConversationTurns()));
        int turn = state.optInt("InteractionCount", 0) + 1;
        data.put("currentTurnNumber", turn);
        data.put("currentTurnTimestamp", OffsetDateTime.now().toString());
        data.put("currentPlayerText", playerText == null ? "" : playerText);
        return data;
    }

    String relationshipContext(JSONObject profile, JSONObject state) {
        StringBuilder b = new StringBuilder();
        b.append("RELATIONSHIP / EMOTION STATE\n");
        b.append("Heart ").append(state.optInt("Heart",0)).append(", Trust ").append(state.optInt("Trust",0)).append(", Romance ").append(state.optInt("Romance",0))
                .append(", EmotionalConnection ").append(String.format(Locale.US,"%.1f",state.optDouble("EmotionalConnectionPercent",0))).append("%\n");
        b.append("Met=").append(state.optBoolean("IsMet",false)).append("; Ally=").append(state.optBoolean("IsAlly",false)).append("; GirlfriendReady=").append(state.optBoolean("IsGirlfriendReady",false)).append("; IsGirlfriend=").append(state.optBoolean("IsGirlfriend",false)).append(".\n");
        String summary = state.optString("ConversationSummary",""); if (!summary.isEmpty()) b.append("ConversationSummary: ").append(summary).append('\n');
        String last = state.optString("LastDeterministicEmotionSummary",""); if (!last.isEmpty()) b.append("Last deterministic update: ").append(last).append('\n');
        if (state.optBoolean("IsGirlfriend", false)) {
            b.append("GIRLFRIEND MODE INSTRUCTIONS — ACTIVE\nAct like a committed girlfriend in this state: responsive, emotionally present, affectionate, and actively flirty when it fits her personality and current context.\n");
            if (state.optBoolean("GirlfriendSextingEnabled", false)) {
                JSONArray sx = profile.optJSONArray("sextingModeInstructions");
                if (sx != null && sx.length() > 0) { b.append("GIRLFRIEND SEXTING MODE LATCH — ACTIVE\n"); appendArray(b, sx, 8); }
            }
            JSONObject hourly = state.optJSONObject("GirlfriendHourlyState"); if (hourly != null && hourly.length() > 0) b.append("CURRENT GIRLFRIEND HOURLY STATE\n").append(hourly.toString()).append('\n');
        }
        return b.toString().trim();
    }

    private String globalCanonLore() {
        return "GLOBAL LAST CAT CANON THE STARS SHOULD KNOW\n" +
                "- No cat has been seen for one hundred years. The universe is drying up because cats are gone; cats are rare astral conduits tied to water, stardust, and restoration.\n" +
                "- The player is the sentient Last Cat. He was delivered twenty years ago to an intergalactic orphanage with people and other races from across the universe.\n" +
                "- The six Stars are six human women in or near the caves and cave settlements. They have childhoods, parents, village ties, wounds, and their own local histories.\n" +
                "- The caves are dark, deep, fast-moving, and genuinely lethal. Violent and treacherous enemies, poisoned water, collapsing routes, ambushes, and ancient powers can injure or kill both cats and Stars.\n" +
                "- The Last Cat needs the Stars' powers to survive the cave network, and the Stars need his astral growth, judgment, and courage. Neither side is invulnerable.\n" +
                "- The Last Cat is the only known cat and the world's present hope of restoring the lost cats and surviving what waits below. Keep hope alive without softening the danger.\n" +
                "- A greater evil may be involved in why the cats are gone, but the Stars do not know the full truth. Treat this as mystery, rumor, intuition, or prophecy, not solved exposition.\n" +
                "- Other Stars are discovery-gated in the game. This standalone relationship app makes them socially available without claiming game progression occurred.";
    }

    private String privateRomanceProfile(JSONObject p) {
        StringBuilder b = new StringBuilder("PRIVATE ROMANCE / ATTRACTION PROFILE — INTERNAL ONLY\nThis is inner-character knowledge, not something to dump directly. Let it guide subtext, teasing, affection, pursuit, and connection growth. It never implies commitment unless IsGirlfriend=true.\n");
        appendNamedArray(b, "Flirt style", p.optJSONArray("flirtStyle"), 10);
        appendNamedArray(b, "Friends and contacts", p.optJSONArray("friendsAndContacts"), 16);
        return b.toString().trim();
    }

    private String starActingCard(JSONObject p) {
        StringBuilder b = new StringBuilder();
        b.append("Star: ").append(p.optString("displayName",p.optString("starId",""))).append('\n');
        b.append("Element: ").append(p.optString("element","")).append('\n');
        b.append("Domain: ").append(p.optString("domain","")).append('\n');
        b.append("Core quote: ").append(p.optString("coreQuote","")).append('\n');
        b.append("Personality: ").append(p.optString("personalityShort",p.optString("deepPersonality",""))).append('\n');
        b.append("Backstory: ").append(p.optString("backstorySummary","")).append('\n');
        b.append("Complete background: ").append(p.optString("completeBackgroundStory","")).append('\n');
        appendNamedArray(b,"Speaking style",p.optJSONArray("speakingStyle"),12);
        appendNamedArray(b,"Behaviors",p.optJSONArray("behaviors"),12);
        appendNamedArray(b,"Known people",p.optJSONArray("peopleKnown"),16);
        return b.toString().trim();
    }

    private String fastConversationProfile(JSONObject p, JSONObject state) {
        StringBuilder b = new StringBuilder("FAST CONVERSATION PROFILE\n");
        appendNamedArray(b,"Spoken response rules",p.optJSONArray("spokenResponseRules"),12);
        appendNamedArray(b,"Voice direction",p.optJSONArray("voiceDirection"),8);
        b.append("CharacterAttributes=").append(p.optString("characterAttributes","")).append('\n');
        if (state.optBoolean("IsGirlfriend",false)) appendNamedArray(b,"Cute mode instructions",p.optJSONArray("cuteModeInstructions"),8);
        return b.toString().trim();
    }

    private String emotionVectorInstructions(JSONObject profile) {
        StringBuilder b = new StringBuilder();
        b.append("emotionalEffectVector is exactly ").append(POSITIVE_EMOTIONS.length).append(" semicolon-separated confidence:score pairs in this order: ").append(String.join(", ",POSITIVE_EMOTIONS)).append(". confidence=0-1, score=0-100.\n");
        b.append("negativeEmotionVector is exactly ").append(NEGATIVE_EMOTIONS.length).append(" pairs in this order: ").append(String.join(", ",NEGATIVE_EMOTIONS)).append(".\n");
        b.append("Star-specific deterministic influence map from the profile (the host, not the model, awards relationship points):\n");
        JSONArray influence = profile.optJSONArray("emotionInfluence");
        if (influence != null) for (int i=0;i<influence.length();i++) {
            JSONObject x=influence.optJSONObject(i); if(x==null)continue;
            b.append("- ").append(x.optString("emotion")).append(" polarity=").append(x.optString("polarity")).append(" affectsStar=").append(x.optDouble("affectsStar",0)).append('\n');
        }
        return b.toString();
    }

    private String currentTimeContext(String time) {
        OffsetDateTime now;
        try { now = OffsetDateTime.parse(time); } catch (Exception e) { now = OffsetDateTime.now(); }
        return "CURRENT TIME\nCurrent time: " + now + "\nLocal date: " + now.toLocalDate() + "\nTime zone: " + ZoneId.systemDefault();
    }

    private String recentConversationText(JSONObject state, int max) { JSONArray a=recentTurnObjects(state,max); StringBuilder b=new StringBuilder(); for(int i=0;i<a.length();i++){JSONObject t=a.optJSONObject(i); if(t!=null)b.append("- Turn ").append(t.optInt("index",i+1)).append(": player=\"").append(t.optString("player")).append("\"; Star=\"").append(t.optString("star")).append("\"\n");} return b.length()==0?"No recent usable turns yet.":b.toString().trim(); }

    private JSONArray recentTurnObjects(JSONObject state, int max) {
        JSONArray log=state.optJSONArray("MemoryLog"); JSONArray out=new JSONArray(); if(log==null)return out;
        int count=Math.min(max,log.length()); for(int i=0;i<count;i++){JSONObject e=log.optJSONObject(i); if(e==null)continue; JSONObject t=new JSONObject(); t.put("index",i+1); t.put("player",e.optString("PlayerText","")); t.put("star",e.optString("StarResponse","")); out.put(t);} return out;
    }

    private JSONArray recentTurnObjectsForSpeech(JSONObject state, int max) {
        JSONArray log=state.optJSONArray("MemoryLog"); JSONArray out=new JSONArray(); if(log==null)return out;
        List<JSONObject> selected=new ArrayList<>(); for(int i=0;i<Math.min(max,log.length());i++){JSONObject e=log.optJSONObject(i); if(e!=null)selected.add(e);} Collections.reverse(selected);
        int base=Math.max(1,state.optInt("InteractionCount",selected.size())-selected.size()+1);
        for(int i=0;i<selected.size();i++){JSONObject e=selected.get(i); JSONObject t=new JSONObject(); t.put("turnNumber",base+i); t.put("timestamp",e.optString("CreatedAt","")); t.put("player",e.optString("PlayerText","")); t.put("you",e.optString("StarResponse","")); out.put(t);} return out;
    }

    private JSONArray normalizeSummaryBlocks(JSONArray source) { JSONArray out=new JSONArray(); if(source==null)return out; for(int i=0;i<source.length();i++){JSONObject s=source.optJSONObject(i); if(s==null)continue; JSONObject x=new JSONObject(); x.put("startTurn",s.optInt("StartTurn",s.optInt("startTurn",0))); x.put("endTurn",s.optInt("EndTurn",s.optInt("endTurn",0))); x.put("summary",s.optString("Summary",s.optString("summary",""))); out.put(x);} return out; }

    private String shortTermMemoryText(JSONObject state) { return formatArray("SHORT-TERM MEMORY", state.optJSONArray("ShortTermMemory"), 12); }
    private String durableMemoryText(JSONObject state) { StringBuilder b=new StringBuilder(); b.append(formatArray("Important player quotes",state.optJSONArray("ImportantPlayerQuotes"),16)); b.append('\n').append(formatArray("Memory notes",state.optJSONArray("MemoryNotes"),16)); b.append('\n').append(formatArray("Things learned or suggested by the Last Cat",state.optJSONArray("ThingsLearnedOrSuggestedByPlayer"),24)); b.append('\n').append(formatArray("Remembered markers",state.optJSONArray("RememberedMarkers"),16)); return b.toString().trim(); }
    private String importantMemoryText(JSONObject state) { return durableMemoryText(state)+"\n"+shortTermMemoryText(state); }
    private String unresolvedText(JSONObject state) { return formatArray("Questions FROM the Last Cat",state.optJSONArray("IncomingUnresolvedVectors"),10)+"\n"+formatArray("Questions TO the Last Cat",state.optJSONArray("OutgoingUnresolvedVectors"),10); }
    private String markerText(JSONObject state) { return formatArray("REMEMBERED MARKERS",state.optJSONArray("RememberedMarkers"),16); }
    private String storyHistoryText(JSONObject state) { return formatArray("STORY HISTORY",state.optJSONArray("StoryHistory"),12); }
    private String todayStateText(JSONObject state) { JSONObject o=state.optJSONObject("TodayState"); return o==null||o.length()==0?"No generated current-day state yet.":o.toString(); }

    private String buildCheckpointContext(JSONObject state, String player, String reply) { int n=state.optInt("InteractionCount",0)+1; if(n%10!=0)return ""; return "CONVERSATION SUMMARY CHECKPOINT\nThis is turn "+n+". Produce tenTurnConversationSummary for the latest ten turns."+(n%100==0?" Also produce hundredTurnConversationSummary from the latest ten ten-turn rollups.":""); }

    private JSONArray firstArray(JSONObject o,String a,String b){JSONArray x=o.optJSONArray(a); if(x==null)x=o.optJSONArray(b); return x==null?new JSONArray():x;}
    private String daySlot(int hour){ if(hour<6)return "overnight"; if(hour<12)return "morning"; if(hour<17)return "afternoon"; if(hour<22)return "evening"; return "night"; }

    private String formatArray(String title, JSONArray a, int max) { StringBuilder b=new StringBuilder(title).append(':'); if(a==null||a.length()==0)return b.append(" none").toString(); b.append('\n'); for(int i=0;i<Math.min(max,a.length());i++)b.append("- ").append(String.valueOf(a.opt(i))).append('\n'); return b.toString().trim(); }
    private void appendNamedArray(StringBuilder b,String title,JSONArray a,int max){if(a==null||a.length()==0)return;b.append(title).append(":\n");appendArray(b,a,max);} private void appendArray(StringBuilder b,JSONArray a,int max){for(int i=0;i<Math.min(max,a.length());i++)b.append("- ").append(String.valueOf(a.opt(i))).append('\n');}

    private JSONArray defaultNeedCatalog() { return new JSONArray(Arrays.asList("emotionalconsistency | Emotional consistency","tenderness | Tenderness","reassurance | Reassurance","calmpresence | Calm presence","honestattention | Honest attention","gentlepraise | Gentle praise","comfortafterfear | Comfort after fear","qualitytime | Quality time","physicalcloseness | Physical closeness","feelsafe | Feel safe","understood | Feel understood","playfulness | Playfulness","sharedpurpose | Shared purpose","space | Healthy space","beingchosen | Being chosen","beingheard | Being heard","trust | Trust","rest | Rest","affection | Affection","adventure | Shared adventure")); }
    private JSONArray defaultWantCatalog() { return new JSONArray(Arrays.asList("actoutaffection | Act out affection","pullhimclose | Pull him close","kisshim | Kiss him","holdhishand | Hold his hand","touchhischeek | Touch his cheek","initiatecloseness | Initiate closeness","usepetname | Use a pet name","cuddle | Cuddle","teasehim | Tease him","comfort him | Comfort him","protecthim | Protect him","playagame | Start a game","exploretogether | Explore together","talkdeeply | Talk deeply","showoffmagic | Show off domain magic","makessomething | Make something together","sing | Sing","practice | Practice together","askforreassurance | Ask for reassurance","sharevulnerability | Share vulnerability")); }
}
