using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace C500GDesktop {
 public sealed partial class CloudPtzController {
  private readonly Dictionary<ulong,IntPtr> videoSessions=new Dictionary<ulong,IntPtr>();
  private bool ContainsVideoHandle(IntPtr handle){return videoSessions.Values.Contains(handle);}
  private ICloudVideoBridge VideoBridge {get{var value=bridge as ICloudVideoBridge;if(value==null)throw new InvalidOperationException("계정 영상 모듈이 준비되지 않았습니다.");return value;}}
  internal async Task<IntPtr> BeginVideoAsync(CameraProfile camera,IntPtr window,CancellationToken token){
   ThrowIfDisposed();ulong device=ParseDeviceId(camera);await gate.WaitAsync(token).ConfigureAwait(false);
   IntPtr handle=IntPtr.Zero;
   try{
    ThrowIfDisposed();if(!IsReady)throw new InvalidOperationException("먼저 계정으로 로그인해 주세요.");
    IntPtr previous;if(videoSessions.TryGetValue(device,out previous))await Task.Run(()=>CloseVideoCore(previous)).ConfigureAwait(false);
    if(session!=IntPtr.Zero&&sessionDevice==device){handle=session;}else{handle=await Task.Run(()=>bridge.Open(device),token).ConfigureAwait(false);}
    videoSessions.Add(device,handle);IntPtr current=handle;
    await Task.Run(()=>{VideoBridge.Configure(current,camera.DevConfig);VideoBridge.VideoStart(current,window);VideoBridge.VideoAudio(current,false,0);},token).ConfigureAwait(false);
    token.ThrowIfCancellationRequested();return handle;
   }catch{
    if(handle!=IntPtr.Zero){try{CloseVideoCore(handle);}catch{}}
    throw;
   }finally{gate.Release();}
  }
  internal async Task<CloudVideoStatus> VideoStatusAsync(IntPtr handle,CancellationToken token){
   await gate.WaitAsync(token).ConfigureAwait(false);
   try{ThrowIfDisposed();if(!ContainsVideoHandle(handle))return new CloudVideoStatus{State=6,Frames=0,Error=0};
    return await Task.Run(()=>{int state,frames,error;VideoBridge.VideoState(handle,out state,out frames,out error);return new CloudVideoStatus{State=state,Frames=frames,Error=error};},token).ConfigureAwait(false);
   }finally{gate.Release();}
  }
  internal async Task VideoAudioAsync(IntPtr handle,bool enabled,int volume,CancellationToken token){
   await gate.WaitAsync(token).ConfigureAwait(false);
   try{ThrowIfDisposed();if(ContainsVideoHandle(handle))await Task.Run(()=>VideoBridge.VideoAudio(handle,enabled,volume),token).ConfigureAwait(false);}
   finally{gate.Release();}
  }
  internal async Task EndVideoAsync(IntPtr handle){
   await gate.WaitAsync().ConfigureAwait(false);
   try{if(ContainsVideoHandle(handle))await Task.Run(()=>CloseVideoCore(handle)).ConfigureAwait(false);}
   finally{gate.Release();}
  }
  private void CloseVideoCore(IntPtr handle){
   ulong device=0;bool found=false;foreach(var pair in videoSessions)if(pair.Value==handle){device=pair.Key;found=true;break;}
   if(!found)return;
   Exception failure=null;
   if(session==handle){try{if(pressed)ReleaseCore();}catch(Exception ex){failure=ex;}finally{session=IntPtr.Zero;sessionDevice=0;lastDirection=null;pressed=false;}}
   videoSessions.Remove(device);try{bridge.Close(handle);}catch(Exception ex){if(failure==null)failure=ex;}
   if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
  }
  private void CloseVideosCore(){Exception failure=null;foreach(IntPtr handle in videoSessions.Values.ToArray()){try{CloseVideoCore(handle);}catch(Exception ex){if(failure==null)failure=ex;}}if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();}
 }
}
