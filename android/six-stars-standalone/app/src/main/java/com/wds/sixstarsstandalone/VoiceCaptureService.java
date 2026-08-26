package com.wds.sixstarsstandalone;

import android.Manifest;
import android.content.Context;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;

import java.io.*;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/** Minimal on-device microphone capture producing the same ordinary WAV input Groq receives on desktop. */
final class VoiceCaptureService {
    private static final int SAMPLE_RATE = 16000;
    private final Context context;
    private volatile boolean recording;
    private AudioRecord recorder;
    private Thread thread;
    private File pcmFile;

    VoiceCaptureService(Context context){this.context=context.getApplicationContext();}

    synchronized void start() throws Exception {
        if(recording)return;
        int min=AudioRecord.getMinBufferSize(SAMPLE_RATE,AudioFormat.CHANNEL_IN_MONO,AudioFormat.ENCODING_PCM_16BIT);
        if(min<=0)throw new IllegalStateException("AudioRecord buffer unavailable: "+min);
        recorder=new AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION,SAMPLE_RATE,AudioFormat.CHANNEL_IN_MONO,AudioFormat.ENCODING_PCM_16BIT,Math.max(min,8192)*2);
        if(recorder.getState()!=AudioRecord.STATE_INITIALIZED)throw new IllegalStateException("AudioRecord failed to initialize.");
        pcmFile=new File(context.getCacheDir(),"sixstars-talk-"+System.currentTimeMillis()+".pcm");
        recording=true; recorder.startRecording();
        thread=new Thread(()->captureLoop(Math.max(min,8192)),"SixStarsVoiceCapture"); thread.start();
    }

    synchronized File stop() throws Exception {
        if(!recording)throw new IllegalStateException("Not recording.");
        recording=false;
        try{recorder.stop();}catch(Exception ignored){}
        if(thread!=null)thread.join(2500);
        recorder.release(); recorder=null;
        File wav=new File(context.getCacheDir(),"sixstars-talk-"+System.currentTimeMillis()+".wav");
        pcmToWav(pcmFile,wav); if(pcmFile!=null)pcmFile.delete(); return wav;
    }

    boolean isRecording(){return recording;}

    private void captureLoop(int bufferSize){byte[]buf=new byte[bufferSize];try(OutputStream out=new FileOutputStream(pcmFile)){while(recording){int n=recorder.read(buf,0,buf.length);if(n>0)out.write(buf,0,n);}}catch(Exception ignored){}}

    private static void pcmToWav(File pcm,File wav)throws Exception{
        long data=pcm.length(); try(OutputStream out=new FileOutputStream(wav);InputStream in=new FileInputStream(pcm)){
            ByteBuffer h=ByteBuffer.allocate(44).order(ByteOrder.LITTLE_ENDIAN);
            h.put(new byte[]{'R','I','F','F'});h.putInt((int)(36+data));h.put(new byte[]{'W','A','V','E'});h.put(new byte[]{'f','m','t',' '});h.putInt(16);h.putShort((short)1);h.putShort((short)1);h.putInt(SAMPLE_RATE);h.putInt(SAMPLE_RATE*2);h.putShort((short)2);h.putShort((short)16);h.put(new byte[]{'d','a','t','a'});h.putInt((int)data);out.write(h.array());byte[]b=new byte[8192];int n;while((n=in.read(b))>=0)out.write(b,0,n);}
    }
}
