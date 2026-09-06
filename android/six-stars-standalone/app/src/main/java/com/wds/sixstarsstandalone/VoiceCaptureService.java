package com.wds.sixstarsstandalone;

import android.Manifest;
import android.content.Context;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.AudioTrack;
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
    private volatile boolean monitorEnabled;
    private volatile AudioTrack monitorTrack;

    VoiceCaptureService(Context context){this.context=context.getApplicationContext();}

    synchronized void setMonitorEnabled(boolean enabled){
        monitorEnabled=enabled;
        if(!enabled)releaseMonitorLocked();
        else if(recording)startMonitorLocked();
    }

    boolean isMonitorEnabled(){return monitorEnabled;}

    synchronized void start() throws Exception {
        if(recording)return;
        int min=AudioRecord.getMinBufferSize(SAMPLE_RATE,AudioFormat.CHANNEL_IN_MONO,AudioFormat.ENCODING_PCM_16BIT);
        if(min<=0)throw new IllegalStateException("AudioRecord buffer unavailable: "+min);
        recorder=new AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION,SAMPLE_RATE,AudioFormat.CHANNEL_IN_MONO,AudioFormat.ENCODING_PCM_16BIT,Math.max(min,8192)*2);
        if(recorder.getState()!=AudioRecord.STATE_INITIALIZED)throw new IllegalStateException("AudioRecord failed to initialize.");
        pcmFile=new File(context.getCacheDir(),"sixstars-talk-"+System.currentTimeMillis()+".pcm");
        recording=true; recorder.startRecording(); if(monitorEnabled)startMonitorLocked();
        thread=new Thread(()->captureLoop(Math.max(min,8192)),"SixStarsVoiceCapture"); thread.start();
    }

    synchronized File stop() throws Exception {
        if(!recording)throw new IllegalStateException("Not recording.");
        recording=false;
        try{recorder.stop();}catch(Exception ignored){}
        if(thread!=null)thread.join(2500);
        recorder.release(); recorder=null; releaseMonitorLocked();
        File wav=new File(context.getCacheDir(),"sixstars-talk-"+System.currentTimeMillis()+".wav");
        pcmToWav(pcmFile,wav); if(pcmFile!=null)pcmFile.delete(); return wav;
    }

    boolean isRecording(){return recording;}

    private void captureLoop(int bufferSize){byte[]buf=new byte[bufferSize];try(OutputStream out=new FileOutputStream(pcmFile)){while(recording){int n=recorder.read(buf,0,buf.length);if(n>0){out.write(buf,0,n);writeMonitor(buf,n);}}}catch(Exception ignored){}}

    private synchronized void startMonitorLocked(){
        if(!monitorEnabled||monitorTrack!=null)return;
        try{
            int min=AudioTrack.getMinBufferSize(SAMPLE_RATE,AudioFormat.CHANNEL_OUT_MONO,AudioFormat.ENCODING_PCM_16BIT);
            if(min<=0)min=2048;
            AudioTrack track=new AudioTrack.Builder()
                    .setAudioAttributes(new AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
                    .setAudioFormat(new AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT).setSampleRate(SAMPLE_RATE).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).build())
                    .setBufferSizeInBytes(Math.max(min,2048))
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                    .build();
            if(track.getState()!=AudioTrack.STATE_INITIALIZED){track.release();return;}
            track.play(); monitorTrack=track;
        }catch(Throwable ignored){monitorTrack=null;}
    }

    private void writeMonitor(byte[]buf,int n){
        if(!monitorEnabled||n<=0)return; AudioTrack track=monitorTrack; if(track==null)return;
        try{track.write(buf,0,n,AudioTrack.WRITE_NON_BLOCKING);}catch(Throwable ignored){}
    }

    private synchronized void releaseMonitorLocked(){
        AudioTrack track=monitorTrack; monitorTrack=null; if(track==null)return;
        try{track.pause();}catch(Throwable ignored){} try{track.flush();}catch(Throwable ignored){} try{track.stop();}catch(Throwable ignored){} try{track.release();}catch(Throwable ignored){}
    }

    private static void pcmToWav(File pcm,File wav)throws Exception{
        long data=pcm.length(); try(OutputStream out=new FileOutputStream(wav);InputStream in=new FileInputStream(pcm)){
            ByteBuffer h=ByteBuffer.allocate(44).order(ByteOrder.LITTLE_ENDIAN);
            h.put(new byte[]{'R','I','F','F'});h.putInt((int)(36+data));h.put(new byte[]{'W','A','V','E'});h.put(new byte[]{'f','m','t',' '});h.putInt(16);h.putShort((short)1);h.putShort((short)1);h.putInt(SAMPLE_RATE);h.putInt(SAMPLE_RATE*2);h.putShort((short)2);h.putShort((short)16);h.put(new byte[]{'d','a','t','a'});h.putInt((int)data);out.write(h.array());byte[]b=new byte[8192];int n;while((n=in.read(b))>=0)out.write(b,0,n);}
    }
}
