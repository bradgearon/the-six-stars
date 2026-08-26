package com.wds.sixstarsstandalone;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.time.OffsetDateTime;
import java.util.Locale;

/**
 * Save format intentionally mirrors SixStarsSaveData/StarState property names so a later import
 * layer can merge this file without inventing a second relationship schema.
 */
final class StarStateStore {
    private final File file;
    private final StandaloneSettings settings;
    private JSONObject save;

    StarStateStore(Context context, StandaloneSettings settings) throws Exception {
        this.settings = settings;
        File dir = new File(context.getFilesDir(), "state");
        if (!dir.exists()) dir.mkdirs();
        file = new File(dir, "six-stars-standalone-state.json");
        save = file.exists() ? JsonUtil.readObject(file) : createDefaultSave();
        normalize();
        persist();
    }

    synchronized JSONObject star(String starId) throws Exception {
        JSONArray stars = save.getJSONArray("Stars");
        for (int i = 0; i < stars.length(); i++) {
            JSONObject s = stars.getJSONObject(i);
            if (s.optString("StarId").equalsIgnoreCase(starId)) return s;
        }
        JSONObject s = createDefaultStar(starId);
        stars.put(s); persist(); return s;
    }

    synchronized void persist() throws Exception {
        save.put("SavedAt", OffsetDateTime.now().toString());
        JsonUtil.writeObject(file, save);
    }

    synchronized JSONObject snapshot() throws Exception { return JsonUtil.deepCopy(save); }
    File file() { return file; }

    private JSONObject createDefaultSave() throws Exception {
        JSONObject root = new JSONObject();
        root.put("Version", 2);
        root.put("SessionId", settings.sessionId());
        root.put("SavedAt", OffsetDateTime.now().toString());
        JSONArray stars = new JSONArray();
        for (String id : StarProfileRepository.IDS) stars.put(createDefaultStar(id));
        root.put("Stars", stars);
        root.put("StandaloneMetadata", new JSONObject()
                .put("Schema", "SixStarsSaveData-compatible")
                .put("SocialAccessMode", "all-stars")
                .put("ImportPolicyHint", "merge relationship/memory fields; do not overwrite game discovery/progression flags unless explicitly selected"));
        return root;
    }

    private JSONObject createDefaultStar(String id) throws Exception {
        JSONObject s = new JSONObject();
        s.put("StarId", id);
        s.put("Heart", 0); s.put("Trust", 0); s.put("Romance", 0); s.put("BenaveilInfluence", 0);
        // The standalone relationship-builder intentionally makes every Star socially available. A future
        // game import can merge relationship/memory while leaving Unity's discovery flags authoritative.
        s.put("IsMet", true); s.put("IsAlly", true);
        s.put("EmotionalConnectionSkillLevel", "interested_private_potential_partner");
        s.put("EmotionalConnectionPercent", 0.0); s.put("LastEmotionalConnectionPercentDelta", 0.0);
        s.put("IsGirlfriendReady", false); s.put("GirlfriendReadySince", JSONObject.NULL);
        s.put("GirlfriendReadinessSummary", "Not ready: requires ally status, EmotionalConnectionPercent >= 25, Heart >= 6, Trust >= 4, and Romance >= 3.");
        s.put("LastEmotionalEffectVector", ""); s.put("LastNegativeEmotionVector", ""); s.put("LastDeterministicEmotionSummary", "");
        s.put("TodayState", new JSONObject()); s.put("GirlfriendHourlyState", new JSONObject());
        s.put("LastDailyStateSourceUpdateAt", OffsetDateTime.now().toString());
        s.put("InteractionCount", 0); s.put("LastInteractionAt", JSONObject.NULL); s.put("CurrentConversationId", ""); s.put("ConversationSummary", "");
        for (String key : new String[]{"OtherStars","ActiveEffectors","GameDialogueHistory","LastTopAffectedEmotions","ImportantPlayerQuotes","AllImportantPlayerQuotes","MemoryNotes","ShortTermMemory","ThingsLearnedOrSuggestedByPlayer","IncomingUnresolvedVectors","OutgoingUnresolvedVectors","UnresolvedResolutionHistory","RememberedMarkers","SecretsUnlocked","SecretStates","LearnedFunctions","RelationshipCombos","RelationshipEvents","GirlfriendHistory","PetNamesPlayerUsesForStar","PetNamesStarUsesForPlayer","GirlfriendDesiredStateHistory","GirlfriendNeedsMetHistory","GirlfriendDesiredNeedVectorHistory","GirlfriendDesiredWantVectorHistory","GirlfriendMetNeedVectorHistory","GirlfriendMetWantVectorHistory","GirlfriendNeedsMetPerformanceVector","GirlfriendWantsMetPerformanceVector","GirlfriendCoupleActivityScores","GirlfriendCoupleActivityHistory","GirlfriendCoupleActivityBestExamples","MemoryLog","TenTurnConversationSummaries","HundredTurnConversationSummaries","OldConversationArchives","EmotionUpdateHistory","EmotionVectorHistory","RelationshipScoreAccumulators","PositiveEmotionTotals","NegativeEmotionTotals","StoryHistory"}) s.put(key, new JSONArray());
        s.put("CurrentDialogueState", new JSONObject());
        s.put("IsGirlfriend", false); s.put("GirlfriendSince", JSONObject.NULL); s.put("GirlfriendCuteModeEnabled", true); s.put("GirlfriendSextingEnabled", false);
        s.put("PositiveSuccessionCount", 0); s.put("RomanticSuccessionCount", 0); s.put("HurtSuccessionCount", 0);
        s.put("LastPositiveInteractionAt", JSONObject.NULL); s.put("LastRomanticInteractionAt", JSONObject.NULL); s.put("LastHurtInteractionAt", JSONObject.NULL);
        return s;
    }

    private void normalize() throws Exception {
        if (!save.has("Version")) save.put("Version", 2);
        if (!save.has("SessionId")) save.put("SessionId", settings.sessionId());
        JSONArray stars = save.optJSONArray("Stars");
        if (stars == null) { stars = new JSONArray(); save.put("Stars", stars); }
        for (String id : StarProfileRepository.IDS) {
            boolean found = false;
            for (int i = 0; i < stars.length(); i++) if (stars.getJSONObject(i).optString("StarId").equalsIgnoreCase(id)) found = true;
            if (!found) stars.put(createDefaultStar(id));
        }
    }
}
