package com.wds.sixstarsstandalone;

import android.content.Context;
import android.util.Base64;

import okhttp3.*;
import org.json.JSONObject;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.Locale;
import java.util.concurrent.TimeUnit;

/** Android equivalent of AlibabaQwenTtsStreamingVoiceGenerator, including on-device voice-id cache. */
final class AlibabaQwenTtsClient {
    static final class AudioResult { final byte[] bytes; final String mime; AudioResult(byte[] b,String m){bytes=b;mime=m;} }
    private final Context context;
    private final StandaloneSettings settings;
    private final ContentRepository content;
    private final OkHttpClient client;
    private final File cacheFile;

    AlibabaQwenTtsClient(Context context, StandaloneSettings settings, ContentRepository content) {
        this.context=context.getApplicationContext(); this.settings=settings; this.content=content;
        client=new OkHttpClient.Builder().connectTimeout(30,TimeUnit.SECONDS).readTimeout(Math.max(30,settings.ttsTimeoutSeconds()),TimeUnit.SECONDS).build();
        cacheFile=new File(this.context.getFilesDir(),"state/hosted-tts-voices.json");
    }

    AudioResult synthesize(JSONObject profile,String text) throws Exception {
        if(!settings.ttsEnabled())throw new IllegalStateException("Hosted TTS is disabled.");
        if(settings.ttsKey().isEmpty())throw new IllegalStateException("Alibaba/DashScope API key is empty. Configure "+settings.file.getAbsolutePath());
        String starId=profile.optString("starId",profile.optString("displayName","star"));
        String voice=resolveVoice(profile,starId);
        JSONObject input=new JSONObject().put("text",text).put("voice",voice).put("language_type",settings.defaultLanguage());
        JSONObject payload=new JSONObject().put("model",settings.ttsModel()).put("input",input);
        String body=post(settings.ttsEndpoint(),payload);
        JSONObject audio=new JSONObject(body).optJSONObject("output"); if(audio!=null)audio=audio.optJSONObject("audio");
        if(audio==null)throw new IllegalStateException("Alibaba Qwen TTS response missing output.audio: "+body);
        String data=audio.optString("data","");
        if(!data.isEmpty())return new AudioResult(Base64.decode(data,Base64.DEFAULT),"audio/wav");
        String url=audio.optString("url",""); if(!url.isEmpty())return download(url);
        throw new IllegalStateException("Alibaba Qwen TTS response did not contain output.audio.url or data: "+body);
    }

    private synchronized String resolveVoice(JSONObject profile,String starId) throws Exception {
        JSONObject configured=settings.configuredVoiceIds();
        String voice=configured.optString(starId,""); if(!voice.isEmpty())return voice;
        JSONObject cache=cacheFile.exists()?JsonUtil.readObject(cacheFile):new JSONObject();
        String key=settings.ttsModel()+"|"+starId.toLowerCase(Locale.ROOT); voice=cache.optString(key,""); if(!voice.isEmpty())return voice;
        if(!settings.autoCloneVoices())throw new IllegalStateException("No voice id configured for "+starId+" and AutoCloneVoices=false.");
        String clone=profile.optString("voiceCloneAudioFile",""); if(clone.isEmpty())throw new IllegalStateException("Profile has no voiceCloneAudioFile for "+starId);
        File cloneFile=content.file("VoiceClones/"+clone); if(!cloneFile.exists())throw new FileNotFoundException(cloneFile.getAbsolutePath());
        voice=enroll(starId,cloneFile); cache.put(key,voice); JsonUtil.writeObject(cacheFile,cache); return voice;
    }

    private String enroll(String starId,File file) throws Exception {
        byte[] bytes=readAll(file); String dataUri="data:audio/wav;base64,"+Base64.encodeToString(bytes,Base64.NO_WRAP);
        String preferred=starId.toLowerCase(Locale.ROOT).replaceAll("[^a-z0-9_-]",""); if(preferred.length()>16)preferred=preferred.substring(0,16); if(preferred.length()<3)preferred="star_"+preferred;
        JSONObject input=new JSONObject().put("action","create").put("target_model",settings.ttsModel()).put("preferred_name",preferred).put("audio",new JSONObject().put("data",dataUri));
        JSONObject payload=new JSONObject().put("model",settings.enrollmentModel()).put("input",input);
        String body=post(settings.enrollmentEndpoint(),payload); JSONObject output=new JSONObject(body).optJSONObject("output"); String voice=output==null?"":output.optString("voice",""); if(voice.isEmpty())throw new IllegalStateException("Voice enrollment response missing output.voice: "+body); return voice;
    }

    private String post(String url,JSONObject json)throws Exception{
        Request req=new Request.Builder().url(url).header("Authorization","Bearer "+settings.ttsKey()).post(RequestBody.create(json.toString(),MediaType.get("application/json; charset=utf-8"))).build();
        try(Response res=client.newCall(req).execute()){String body=res.body()==null?"":res.body().string(); if(!res.isSuccessful())throw new IllegalStateException("Alibaba request failed "+res.code()+": "+body); return body;}
    }

    private AudioResult download(String url)throws Exception{Request req=new Request.Builder().url(url).get().build();try(Response res=client.newCall(req).execute()){if(!res.isSuccessful())throw new IllegalStateException("TTS audio download failed "+res.code());byte[] b=res.body()==null?new byte[0]:res.body().bytes();String mime=res.header("Content-Type","audio/wav");return new AudioResult(b,mime);}}
    private static byte[] readAll(File f)throws Exception{try(InputStream in=new FileInputStream(f);ByteArrayOutputStream out=new ByteArrayOutputStream()){byte[]b=new byte[8192];int n;while((n=in.read(b))>=0)out.write(b,0,n);return out.toByteArray();}}
}
