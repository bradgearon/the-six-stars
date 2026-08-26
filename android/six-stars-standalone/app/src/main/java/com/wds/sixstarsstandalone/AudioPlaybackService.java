package com.wds.sixstarsstandalone;

import android.content.Context;
import android.media.MediaPlayer;

import java.io.File;
import java.io.FileOutputStream;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

final class AudioPlaybackService {
    private final Context context;
    AudioPlaybackService(Context context){this.context=context.getApplicationContext();}

    void playBlocking(AlibabaQwenTtsClient.AudioResult audio)throws Exception{
        String ext=audio.mime!=null&&audio.mime.toLowerCase().contains("mpeg")?".mp3":audio.mime!=null&&audio.mime.toLowerCase().contains("ogg")?".ogg":".wav";
        File f=File.createTempFile("sixstars-tts-",ext,context.getCacheDir());try(FileOutputStream out=new FileOutputStream(f)){out.write(audio.bytes);}CountDownLatch done=new CountDownLatch(1);MediaPlayer p=new MediaPlayer();try{p.setDataSource(f.getAbsolutePath());p.setOnCompletionListener(x->done.countDown());p.setOnErrorListener((x,w,e)->{done.countDown();return true;});p.prepare();p.start();done.await(180,TimeUnit.SECONDS);}finally{try{p.release();}catch(Exception ignored){}f.delete();}
    }
}
