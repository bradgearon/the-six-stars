package com.wds.sixstarsstandalone;

import org.json.JSONObject;

/** Common TTS boundary so the standalone app can use either local Qwen or hosted Alibaba. */
interface StarTtsClient {
    TtsAudioResult synthesize(JSONObject profile, String text) throws Exception;
    String modeName();
}
