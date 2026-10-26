using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace C500GDesktop {
 internal sealed class P2pVideoPlayer:IVideoPlayer {
  private readonly string diagnosticFolder;private readonly CloudPtzController controller;private readonly CancellationTokenSource cancellation=new CancellationTokenSource();
  private readonly object audioGate=new object();private bool audioEnabled;private int volume=70,audioRevision;
  private volatile int state=(int)PlayerState.Opening;private int disposed;private readonly Task lifetime;
  public PlayerState State{get{return (PlayerState)state;}}
  internal P2pVideoPlayer(CloudPtzController controller,CameraProfile camera,IntPtr window,string diagnosticDirectory=null){this.controller=controller;diagnosticFolder=diagnosticDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"C500GDesktop");lifetime=Task.Run(()=>RunAsync(camera,window));}
  public void SetAudio(bool enabled,int level){lock(audioGate){level=Math.Max(0,Math.Min(100,level));if(audioEnabled!=enabled||volume!=level){audioEnabled=enabled;volume=level;audioRevision++;}}}
  private async Task RunAsync(CameraProfile camera,IntPtr window){
   IntPtr handle=IntPtr.Zero;int applied=-1,lastState=-1;
   try{
    handle=await controller.BeginVideoAsync(camera,window,cancellation.Token).ConfigureAwait(false);
    while(true){
     cancellation.Token.ThrowIfCancellationRequested();int revision,level;bool enabled;
     lock(audioGate){revision=audioRevision;level=volume;enabled=audioEnabled;}
     if(applied!=revision){
      try{await controller.VideoAudioAsync(handle,enabled,level,cancellation.Token).ConfigureAwait(false);}
      catch(NativePtzException ex){if(ex.Code<=-400&&ex.Code>=-499)Record("audio_error="+ex.Code);else throw;}
      applied=revision;
     }
     CloudVideoStatus status=await controller.VideoStatusAsync(handle,cancellation.Token).ConfigureAwait(false);state=status.Error==0?status.State:(int)PlayerState.Error;
     if(lastState!=state){Record("state="+state+" frames="+status.Frames+" error="+status.Error);lastState=state;}
     if(state==(int)PlayerState.Error||state==(int)PlayerState.Ended||state==(int)PlayerState.Stopped)break;
     await Task.Delay(300,cancellation.Token).ConfigureAwait(false);
    }
   }catch(OperationCanceledException){state=(int)PlayerState.Stopped;}
   catch(NativePtzException ex){state=(int)PlayerState.Error;Record("native_error="+ex.Code);}
   catch{state=(int)PlayerState.Error;Record("worker_error");}
   if(handle!=IntPtr.Zero){try{await controller.EndVideoAsync(handle).ConfigureAwait(false);}catch{Record("cleanup_error");}}
  }
  private void Record(string text){try{string folder=diagnosticFolder;Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,text.StartsWith("audio_error=",StringComparison.Ordinal)?"audio-status-1.3.3.txt":"video-status-1.3.3.txt"),DateTime.UtcNow.ToString("o")+" "+text);}catch(IOException){}catch(UnauthorizedAccessException){}}
  public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;cancellation.Cancel();lifetime.ContinueWith(t=>cancellation.Dispose(),TaskScheduler.Default);}
 }
}
