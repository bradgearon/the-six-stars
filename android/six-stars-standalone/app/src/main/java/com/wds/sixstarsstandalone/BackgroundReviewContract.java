package com.wds.sixstarsstandalone;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

/** Primitive-only background review schema plus deterministic compact-record decoding. */
final class BackgroundReviewContract {
    private BackgroundReviewContract() {}

    static JSONObject schema() throws JSONException {
        String[] required = new String[]{
                "starId","emotionalEffectVector","negativeEmotionVector","storyTitle","storyId","storySummary","storyIsFinished","storyPartIndex",
                "detectedCombos","suggestedStateDeltas","conversationSummary","changesSummary","tenTurnConversationSummary","hundredTurnConversationSummary",
                "thingsLearnedOrSuggestedByPlayer","shortTermMemoryItems","isSexting","questionsFromLastCatVector","questionsToLastCatVector","questionResolutionVector","emotionAnimation","informationNeedsVector","importantPlayerQuotes","memoryNotes","rememberedMarkers","referencedMemoryMarkerIds","secretsUnlocked","learnedFunctions","relationshipEvents",
                "girlfriendHistoryUpdates","petNamesPlayerUsesForStar","petNamesStarUsesForPlayer","girlfriendNeedsMetSummary","girlfriendNeedsMetVector","girlfriendWantsMetVector","girlfriendCoupleActivitySummary","girlfriendCoupleActivityVector",
                "requestToShowBoobs","loreUsed","riskFlags","wisdomScore"
        };
        JSONObject props = new JSONObject();
        put(props,"starId",stringSchema());
        put(props,"emotionalEffectVector",stringSchema());
        put(props,"negativeEmotionVector",stringSchema());
        put(props,"storyTitle",stringSchema());
        put(props,"storyId",stringSchema());
        put(props,"storySummary",stringSchema());
        put(props,"storyIsFinished",typeSchema("boolean"));
        put(props,"storyPartIndex",typeSchema("number"));
        put(props,"detectedCombos",stringArraySchema());
        put(props,"suggestedStateDeltas",stringSchema());
        put(props,"conversationSummary",stringSchema());
        put(props,"changesSummary",stringSchema());
        put(props,"tenTurnConversationSummary",stringSchema());
        put(props,"hundredTurnConversationSummary",stringSchema());
        put(props,"thingsLearnedOrSuggestedByPlayer",stringArraySchema());
        put(props,"shortTermMemoryItems",stringArraySchema());
        put(props,"isSexting",typeSchema("boolean"));
        put(props,"questionsFromLastCatVector",stringSchema());
        put(props,"questionsToLastCatVector",stringSchema());
        put(props,"questionResolutionVector",stringSchema());
        put(props,"emotionAnimation",stringSchema());
        put(props,"informationNeedsVector",stringSchema());
        put(props,"importantPlayerQuotes",stringArraySchema());
        put(props,"memoryNotes",stringArraySchema());
        put(props,"rememberedMarkers",stringArraySchema());
        put(props,"referencedMemoryMarkerIds",stringArraySchema());
        put(props,"secretsUnlocked",stringArraySchema());
        put(props,"learnedFunctions",stringArraySchema());
        put(props,"relationshipEvents",stringArraySchema());
        put(props,"girlfriendHistoryUpdates",stringArraySchema());
        put(props,"petNamesPlayerUsesForStar",stringArraySchema());
        put(props,"petNamesStarUsesForPlayer",stringArraySchema());
        put(props,"girlfriendNeedsMetSummary",stringSchema());
        put(props,"girlfriendNeedsMetVector",stringArraySchema());
        put(props,"girlfriendWantsMetVector",stringArraySchema());
        put(props,"girlfriendCoupleActivitySummary",stringSchema());
        put(props,"girlfriendCoupleActivityVector",stringSchema());
        put(props,"requestToShowBoobs",typeSchema("boolean"));
        put(props,"loreUsed",stringArraySchema());
        put(props,"riskFlags",stringArraySchema());
        put(props,"wisdomScore",typeSchema("number"));

        JSONArray requiredArray = new JSONArray();
        for (String name : required) requiredArray.put(name);
        return new JSONObject()
                .put("type","object")
                .put("additionalProperties",false)
                .put("required",requiredArray)
                .put("properties",props);
    }

    /** Convert compact string records back to the canonical object shapes expected by state code. */
    static JSONObject normalize(JSONObject review) throws JSONException {
        if (review == null) return new JSONObject();
        Object delta = review.opt("suggestedStateDeltas");
        if (delta instanceof String) review.put("suggestedStateDeltas", parseDelta((String)delta));
        normalizeArray(review,"rememberedMarkers", BackgroundReviewContract::parseMarker);
        normalizeArray(review,"learnedFunctions", BackgroundReviewContract::parseLearnedFunction);
        normalizeArray(review,"girlfriendNeedsMetVector", BackgroundReviewContract::parseGirlfriendVector);
        normalizeArray(review,"girlfriendWantsMetVector", BackgroundReviewContract::parseGirlfriendVector);
        return review;
    }

    private interface Parser { JSONObject parse(String value) throws JSONException; }

    private static void normalizeArray(JSONObject root,String key,Parser parser)throws JSONException{
        JSONArray source=root.optJSONArray(key); if(source==null)return;
        JSONArray converted=new JSONArray();
        for(int i=0;i<source.length();i++){
            Object raw=source.opt(i);
            if(raw instanceof JSONObject){converted.put(raw);continue;}
            if(!(raw instanceof String))continue;
            JSONObject parsed=parser.parse((String)raw); if(parsed!=null)converted.put(parsed);
        }
        root.put(key,converted);
    }

    private static JSONObject parseDelta(String value)throws JSONException{
        String[] p=split(value,4);
        return new JSONObject()
                .put("heart",clampInt(number(p,0),-8,8))
                .put("trust",clampInt(number(p,1),-8,8))
                .put("romance",clampInt(number(p,2),-6,6))
                .put("emotionalConnectionPercent",clamp(number(p,3),-5,5));
    }

    private static JSONObject parseMarker(String value)throws JSONException{
        String[] p=split(value,11); if(empty(p[0])&&empty(p[1]))return null;
        return new JSONObject().put("id",clean(p[0])).put("markerName",clean(p[1])).put("description",clean(p[2]))
                .put("locationText",clean(p[3])).put("coordinates",clean(p[4])).put("area",clean(p[5]))
                .put("sourcePlayerQuote",clean(p[6])).put("reminderText",clean(p[7])).put("confidence",clamp(number(p,8),0,1))
                .put("isActive",bool(p[9],true)).put("tags",tags(p[10]));
    }

    private static JSONObject parseLearnedFunction(String value)throws JSONException{
        String[] p=split(value,7); if(empty(p[0])&&empty(p[1]))return null;
        return new JSONObject().put("id",clean(p[0])).put("name",clean(p[1])).put("description",clean(p[2]))
                .put("sourcePlayerQuote",clean(p[3])).put("confidence",clamp(number(p,4),0,1)).put("isActive",bool(p[5],true)).put("tags",tags(p[6]));
    }

    private static JSONObject parseGirlfriendVector(String value)throws JSONException{
        String[] p=split(value,5); if(empty(p[0]))return null;
        return new JSONObject().put("itemId",clean(p[0])).put("displayName",clean(p[1])).put("confidence",clamp(number(p,2),0,1))
                .put("score",clamp(number(p,3),0,100)).put("reason",clean(p[4]));
    }

    private static JSONObject stringSchema()throws JSONException{return typeSchema("string");}
    private static JSONObject typeSchema(String type)throws JSONException{return new JSONObject().put("type",type);}
    private static JSONObject stringArraySchema()throws JSONException{return new JSONObject().put("type","array").put("items",stringSchema());}
    private static void put(JSONObject root,String key,JSONObject value)throws JSONException{root.put(key,value);}
    private static String[] split(String value,int n){String[] raw=(value==null?"":value).split("\\^",-1),out=new String[n];for(int i=0;i<n;i++)out[i]=i<raw.length?raw[i].trim():"";return out;}
    private static boolean empty(String value){return value==null||value.trim().isEmpty();}
    private static String clean(String value){return (value==null?"":value).replace('^',' ').replace('\r',' ').replace('\n',' ').trim();}
    private static double number(String[]p,int i){if(i<0||i>=p.length)return 0;try{return Double.parseDouble(p[i]);}catch(Exception e){return 0;}}
    private static int clampInt(double v,int min,int max){return (int)Math.max(min,Math.min(max,Math.round(v)));}
    private static double clamp(double v,double min,double max){return Math.max(min,Math.min(max,v));}
    private static boolean bool(String v,boolean fallback){if("true".equalsIgnoreCase(v)||"1".equals(v))return true;if("false".equalsIgnoreCase(v)||"0".equals(v))return false;return fallback;}
    private static JSONArray tags(String value){JSONArray a=new JSONArray();if(value==null)return a;for(String x:value.split(",")){String c=clean(x);if(!c.isEmpty())a.put(c);}return a;}
}
