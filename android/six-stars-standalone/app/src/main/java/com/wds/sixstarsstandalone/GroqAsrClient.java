package com.wds.sixstarsstandalone;

import okhttp3.*;
import org.json.JSONObject;

import java.io.File;
import java.util.concurrent.TimeUnit;

/** Android equivalent of the hosted Groq path in LocalWhisperHttpSpeechStreamer. */
final class GroqAsrClient {
    private final StandaloneSettings settings;
    private final OkHttpClient client;

    GroqAsrClient(StandaloneSettings settings) {
        this.settings=settings;
        client=new OkHttpClient.Builder().connectTimeout(20,TimeUnit.SECONDS).readTimeout(Math.max(20,settings.asrTimeoutSeconds()),TimeUnit.SECONDS).build();
    }

    String transcribe(File wav) throws Exception {
        if(!settings.asrEnabled()) throw new IllegalStateException("Hosted ASR is disabled.");
        if(settings.asrKey().isEmpty()) throw new IllegalStateException("Groq API key is empty. Configure " + settings.file.getAbsolutePath());
        RequestBody form=new MultipartBody.Builder().setType(MultipartBody.FORM)
                .addFormDataPart("model",settings.asrModel())
                .addFormDataPart("language",settings.asrLanguage())
                .addFormDataPart("response_format","json")
                .addFormDataPart("file",wav.getName(),RequestBody.create(wav,MediaType.get("audio/wav")))
                .build();
        Request req=new Request.Builder().url(settings.asrEndpoint()).header("Authorization","Bearer "+settings.asrKey()).post(form).build();
        try(Response res=client.newCall(req).execute()){
            String body=res.body()==null?"":res.body().string();
            if(!res.isSuccessful())throw new IllegalStateException("Groq ASR failed "+res.code()+": "+body);
            String text=new JSONObject(body).optString("text","").trim();
            if(text.isEmpty())throw new IllegalStateException("Groq ASR returned no transcript.");
            return text;
        }
    }
}
