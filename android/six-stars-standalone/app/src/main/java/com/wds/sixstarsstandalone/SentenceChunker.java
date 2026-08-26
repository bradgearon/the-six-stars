package com.wds.sixstarsstandalone;

import java.util.ArrayList;
import java.util.List;

/** Emits speakable sentences while foreground Google streaming continues. */
final class SentenceChunker {
    private final StringBuilder pending=new StringBuilder();
    synchronized List<String> accept(String delta){pending.append(delta==null?"":delta);return drain(false);}
    synchronized List<String> finish(){return drain(true);}
    private List<String> drain(boolean force){List<String> out=new ArrayList<>();int start=0;for(int i=0;i<pending.length();i++){char c=pending.charAt(i);boolean end=(c=='.'||c=='?'||c=='!'||c=='\n')&&(i+1==pending.length()||Character.isWhitespace(pending.charAt(i+1)));if(end){String s=pending.substring(start,i+1).trim();if(!s.isEmpty())out.add(s);start=i+1;}}if(start>0)pending.delete(0,start);if(force){String s=pending.toString().trim();if(!s.isEmpty())out.add(s);pending.setLength(0);}return out;}
}
