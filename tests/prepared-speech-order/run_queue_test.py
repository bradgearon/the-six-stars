from pathlib import Path
import subprocess,sys,tempfile
source=Path(sys.argv[1]).read_text()
def method(signature):
 start=source.index(signature,source.index('private final class WavAudioQueue'))
 brace=source.index('{',start);depth=1;i=brace+1
 while depth:
  depth += (source[i]=='{')-(source[i]=='}');i+=1
 return source[start:i]
methods='\n'.join(method(s) for s in ['void enqueue(PhoneVoiceClip clip)', 'void beginPreparedPrefix()', 'void cancelPreparedPrefix()', 'void enqueuePreparedPrefix(', 'private PhoneVoiceClip takeNextClip()'])
java='''import java.util.*;
import java.util.concurrent.*;
public class PreparedQueueTest {
 static class PhoneVoiceClip { byte[] wav={1}; int id; PhoneVoiceClip(int id){this.id=id;} }
 static class Queue {
  final LinkedBlockingDeque<PhoneVoiceClip> queue=new LinkedBlockingDeque<>();
  final Object preparedLock=new Object();
  volatile boolean running=true,preparedPrefixPending;
  volatile PhoneVoiceClip activeClip;
 METHODS
 }
 static void check(boolean ok,String message){if(!ok)throw new AssertionError(message);}
 public static void main(String[] args)throws Exception{
  ExecutorService pool=Executors.newFixedThreadPool(2);
  try{
   Queue q=new Queue();q.beginPreparedPrefix();q.enqueue(new PhoneVoiceClip(100));
   CountDownLatch partial=new CountDownLatch(1),release=new CountDownLatch(1),consumerEntered=new CountDownLatch(1);
   ArrayList<PhoneVoiceClip> prefix=new ArrayList<>(){
    @Override public PhoneVoiceClip get(int index){
     if(index==2){partial.countDown();try{if(!release.await(3,TimeUnit.SECONDS))throw new AssertionError("Producer timeout");}catch(InterruptedException e){throw new RuntimeException(e);}}
     return super.get(index);
    }
   };
   for(int i=0;i<4;i++)prefix.add(new PhoneVoiceClip(i));
   Future<?> producer=pool.submit(()->q.enqueuePreparedPrefix(prefix));
   check(partial.await(3,TimeUnit.SECONDS),"Producer never reached partial batch");
   Future<PhoneVoiceClip> first=pool.submit(()->{consumerEntered.countDown();return q.takeNextClip();});
   check(consumerEntered.await(3,TimeUnit.SECONDS),"Consumer not started");
   try{first.get(100,TimeUnit.MILLISECONDS);throw new AssertionError("Consumer observed partially inserted batch");}
   catch(TimeoutException expected){}finally{release.countDown();}
   producer.get(3,TimeUnit.SECONDS);
   check(first.get(3,TimeUnit.SECONDS).id==0,"First fragment not zero");
   for(int i=1;i<4;i++)check(q.takeNextClip().id==i,"Prefix reordered");
   check(q.takeNextClip().id==100,"Foreground reply did not follow prefix");
   check(q.queue.isEmpty(),"Duplicate fragments");
   // Pending prefix also blocks local cached clips until cancellation releases them.
   q.beginPreparedPrefix();q.enqueue(new PhoneVoiceClip(200));
   Future<PhoneVoiceClip> pending=pool.submit(()->q.takeNextClip());
   try{pending.get(100,TimeUnit.MILLISECONDS);throw new AssertionError("Pending prefix bypassed");}catch(TimeoutException expected){}
   q.cancelPreparedPrefix();check(pending.get(3,TimeUnit.SECONDS).id==200,"Cancellation lost queued clip");
   // Empty/cancelled preparation must not strand playback.
   q.beginPreparedPrefix();q.enqueue(new PhoneVoiceClip(201));q.enqueuePreparedPrefix(new ArrayList<>());
   check(q.takeNextClip().id==201,"Empty prefix blocked playback");
   // Repeated real producer/consumer contention: each fragment exactly once and in order.
   for(int round=0;round<500;round++){
    Queue stress=new Queue();stress.beginPreparedPrefix();
    ArrayList<PhoneVoiceClip> batch=new ArrayList<>();for(int i=0;i<64;i++)batch.add(new PhoneVoiceClip(i));
    Future<?> consumer=pool.submit(()->{try{for(int i=0;i<64;i++)check(stress.takeNextClip().id==i,"Stress order");}catch(InterruptedException e){throw new RuntimeException(e);}});
    stress.enqueuePreparedPrefix(batch);consumer.get(3,TimeUnit.SECONDS);
    check(stress.queue.isEmpty(),"Stress duplicates");
   }
   q.running=false;synchronized(q.preparedLock){q.preparedLock.notifyAll();}
   check(q.takeNextClip()==null,"Stopped queue did not exit");
   System.out.println("PASS: atomic prefix publication, prefix/reply order, local gating, cancellation, empty batch, shutdown, and 500 concurrent 64-fragment batches");
  }finally{pool.shutdownNow();}
 }
}
'''.replace('METHODS',methods)
with tempfile.TemporaryDirectory() as tmp:
 p=Path(tmp)/'PreparedQueueTest.java';p.write_text(java)
 subprocess.run(['java','-jar',sys.argv[2],'-17','-proc:none','-warn:none','-d',tmp,str(p)],check=True)
 subprocess.run(['java','-cp',tmp,'PreparedQueueTest'],check=True)
