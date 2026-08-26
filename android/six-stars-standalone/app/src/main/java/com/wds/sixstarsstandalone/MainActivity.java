package com.wds.sixstarsstandalone;

import android.Manifest;
import android.app.Activity;
import android.content.pm.PackageManager;
import android.os.Bundle;
import android.view.View;
import android.widget.*;

import org.json.JSONObject;

import java.io.File;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Minimal first shell for the server-free relationship builder. Settings/import UI is intentionally next. */
public final class MainActivity extends Activity {
    private static final int RECORD_AUDIO_REQUEST=41;
    private final ExecutorService initExecutor=Executors.newSingleThreadExecutor();
    private StandaloneRuntime runtime;
    private VoiceCaptureService voiceCapture;
    private String selectedStarId="Elusiaa";
    private TextView status,starName,relationship,transcript;
    private ImageView portrait;
    private EditText input;
    private Button sendButton,talkButton;
    private LinearLayout starButtons;
    private volatile boolean assistantLineOpen;

    private final StandaloneRuntime.Callbacks callbacks=new StandaloneRuntime.Callbacks(){
        @Override public void onStatus(String text){ui(()->status.setText(text));}
        @Override public void onUserText(String starId,String text){ui(()->appendTranscript("You: "+text+"\n\n"));}
        @Override public void onAssistantStart(String starId){ui(()->{assistantLineOpen=true;appendTranscript(displayName(starId)+": ");});}
        @Override public void onAssistantDelta(String starId,String delta){ui(()->appendTranscript(delta));}
        @Override public void onAssistantComplete(String starId,String text){ui(()->{assistantLineOpen=false;appendTranscript("\n\n");scrollToBottom();});}
        @Override public void onStateChanged(String starId,JSONObject snapshot){if(starId.equalsIgnoreCase(selectedStarId))ui(()->showState(snapshot));}
        @Override public void onError(String operation,Throwable error){ui(()->{status.setText(operation+" failed: "+friendly(error));Toast.makeText(MainActivity.this,operation+" failed",Toast.LENGTH_LONG).show();});}
    };

    @Override protected void onCreate(Bundle savedInstanceState){
        super.onCreate(savedInstanceState);setContentView(R.layout.activity_main);
        status=findViewById(R.id.status);starName=findViewById(R.id.starName);relationship=findViewById(R.id.relationship);transcript=findViewById(R.id.transcript);portrait=findViewById(R.id.starPortrait);input=findViewById(R.id.input);sendButton=findViewById(R.id.sendButton);talkButton=findViewById(R.id.talkButton);starButtons=findViewById(R.id.starButtons);
        sendButton.setEnabled(false);talkButton.setEnabled(false);buildStarButtons();
        sendButton.setOnClickListener(v->sendTyped()); talkButton.setOnClickListener(v->toggleTalk());
        initExecutor.submit(this::initializeRuntime);
    }

    private void initializeRuntime(){
        try{
            StandaloneRuntime r=new StandaloneRuntime(this); VoiceCaptureService capture=new VoiceCaptureService(this);runtime=r;voiceCapture=capture;
            ui(()->{sendButton.setEnabled(true);talkButton.setEnabled(true);selectStar(selectedStarId);String config="The Last Cat: the Six Stars • standalone • "+r.settings().configurationSummary()+"\nstate="+r.stateStore().file().getAbsolutePath()+"\ncontent="+r.content().root().getAbsolutePath();if(r.settings().llmKey().isEmpty()||r.settings().asrKey().isEmpty()||r.settings().ttsKey().isEmpty())config+="\n"+r.settings().configurationHelp();status.setText(config);});
        }catch(Throwable e){ui(()->status.setText("Standalone runtime initialization failed: "+friendly(e)));}
    }

    private void buildStarButtons(){
        for(String id:StarProfileRepository.IDS){Button b=new Button(this);b.setText(id);b.setAllCaps(false);b.setOnClickListener(v->selectStar(id));starButtons.addView(b,new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT,LinearLayout.LayoutParams.WRAP_CONTENT));}
    }

    private void selectStar(String id){
        selectedStarId=id;starName.setText(displayName(id));portrait.setImageResource(portraitFor(id));
        if(runtime==null){relationship.setText("Loading local state...");return;}
        try{showState(runtime.stateSnapshot(id));}catch(Exception e){relationship.setText("State error: "+friendly(e));}
    }

    private void sendTyped(){
        StandaloneRuntime r=runtime;if(r==null)return;String text=input.getText().toString().trim();if(text.isEmpty())return;input.setText("");r.submitText(selectedStarId,text,callbacks);
    }

    private void toggleTalk(){
        if(runtime==null||voiceCapture==null)return;
        if(voiceCapture.isRecording()){stopTalk();return;}
        if(checkSelfPermission(Manifest.permission.RECORD_AUDIO)!=PackageManager.PERMISSION_GRANTED){requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO},RECORD_AUDIO_REQUEST);return;}
        startTalk();
    }

    private void startTalk(){try{voiceCapture.start();talkButton.setText("Stop");status.setText("Recording locally... tap Stop when finished.");}catch(Exception e){callbacks.onError("Microphone",e);}}
    private void stopTalk(){try{File wav=voiceCapture.stop();talkButton.setText("Talk");runtime.transcribeAndSubmit(selectedStarId,wav,callbacks);}catch(Exception e){talkButton.setText("Talk");callbacks.onError("Microphone",e);}}

    @Override public void onRequestPermissionsResult(int requestCode,String[] permissions,int[] grantResults){super.onRequestPermissionsResult(requestCode,permissions,grantResults);if(requestCode==RECORD_AUDIO_REQUEST){if(grantResults.length>0&&grantResults[0]==PackageManager.PERMISSION_GRANTED)startTalk();else Toast.makeText(this,"Microphone permission is required for Talk.",Toast.LENGTH_LONG).show();}}

    private void showState(JSONObject state){
        relationship.setText(String.format(Locale.US,"Heart %d  •  Trust %d  •  Romance %d\nConnection %.1f%%  •  %s%s",
                state.optInt("Heart",0),state.optInt("Trust",0),state.optInt("Romance",0),state.optDouble("EmotionalConnectionPercent",0),
                state.optBoolean("IsGirlfriend",false)?"Girlfriend":state.optBoolean("IsGirlfriendReady",false)?"Girlfriend-ready":"Building relationship",
                state.optBoolean("GirlfriendSextingEnabled",false)?"  •  Sexting continuity active":""));
    }

    private String displayName(String id){try{return runtime==null?id:runtime.profiles().displayName(id);}catch(Exception e){return id;}}
    private int portraitFor(String id){switch(id.toLowerCase(Locale.ROOT)){case"kaelia":return R.drawable.portrait_kaelia;case"maera":return R.drawable.portrait_maera;case"khavia":return R.drawable.portrait_khavia;case"auerlis":return R.drawable.portrait_auerlis;case"nyxari":return R.drawable.portrait_nyxari;default:return R.drawable.portrait_elusiaa;}}
    private void appendTranscript(String text){transcript.append(text);scrollToBottom();}
    private void scrollToBottom(){final ScrollView s=findViewById(R.id.transcriptScroll);s.post(()->s.fullScroll(View.FOCUS_DOWN));}
    private void ui(Runnable r){runOnUiThread(r);}
    private static String friendly(Throwable e){if(e==null)return"unknown error";Throwable x=e;while(x.getCause()!=null&&x.getCause()!=x)x=x.getCause();String m=x.getMessage();return x.getClass().getSimpleName()+(m==null?"":": "+m);}

    @Override protected void onDestroy(){super.onDestroy();try{if(voiceCapture!=null&&voiceCapture.isRecording())voiceCapture.stop();}catch(Exception ignored){}if(runtime!=null)runtime.close();initExecutor.shutdownNow();}
}
