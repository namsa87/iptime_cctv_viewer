using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace C500GDesktop.Tests {
 internal static class AccountVideoTests {
  static int checks;static readonly CancellationToken None=CancellationToken.None;
  static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
  static void Wait(Task value){value.GetAwaiter().GetResult();}
  static CameraProfile Cam(string id){return new CameraProfile{AccountCamera=true,Name="Fixture",DeviceId=id,DevConfig=unchecked((int)0x80000005),PtzEnabled=true};}
  static CloudPtzController Ready(Native fake,bool touch=false){var c=new CloudPtzController(fake,touch);Wait(c.RegisterAsync(7,new string('a',128),None));return c;}
  static T Get<T>(object target,string field){return (T)target.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);}
  static void Call(object target,string method){target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,null);}
  static void Until(Func<bool> condition){var watch=System.Diagnostics.Stopwatch.StartNew();while(!condition()&&watch.ElapsedMilliseconds<4000)Thread.Sleep(10);if(!condition())throw new Exception("Async condition timed out");}
  [STAThread]static int Main(){try{
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   var fake=new Native();using(var c=Ready(fake)){
    IntPtr a=c.BeginVideoAsync(Cam("42"),new IntPtr(11),None).Result;
    Check(fake.Opens==1&&fake.Config==unchecked((int)0x80000005)&&fake.Window==new IntPtr(11),"Video uses account device/config/renderer handle without RTSP fields");
    Check(fake.Started.Count==1&&!fake.Enabled,"Video starts muted before selected-camera audio routing");
    Wait(c.MoveAsync(Cam("42"),PtzDirection.Up,None));Check(fake.Opens==1&&fake.SentHandle==a,"PTZ reuses the actual video session");
    IntPtr b=c.BeginVideoAsync(Cam("43"),new IntPtr(12),None).Result;Wait(c.MoveAsync(Cam("43"),PtzDirection.Left,None));
    Check(fake.OpenHandles.Count==2&&fake.Closes==0&&fake.SentHandle==b,"Switching PTZ camera preserves both live video sessions");
    Wait(c.VideoAudioAsync(b,true,70,None));Check(fake.Enabled&&fake.Level==70&&fake.AudioHandle==b,"Audio applies only to requested video handle");
    Check(c.VideoStatusAsync(a,None).Result.State==3,"Video status maps renderer-backed playback state");
    Wait(c.EndVideoAsync(a));Check(fake.OpenHandles.Count==1&&fake.OpenHandles.Contains(b),"Closing one video preserves another camera");
    Wait(c.LogoutAsync(None));Check(fake.OpenHandles.Count==0&&fake.Unregisters==1,"Logout closes all video sessions before unregistering");
    Wait(c.EndVideoAsync(b));Check(fake.Closes==2,"Late player disposal after logout is idempotent");
   }
   fake=new Native();using(var c=Ready(fake)){
    IntPtr a=c.BeginVideoAsync(Cam("42"),new IntPtr(11),None).Result;
    IntPtr b=c.BeginVideoAsync(Cam("42"),new IntPtr(11),None).Result;Wait(c.EndVideoAsync(a));
    Check(a!=b&&fake.OpenHandles.Count==1&&fake.OpenHandles.Contains(b),"Reconnecting same camera cannot let stale cleanup close the new session");
   }
   fake=new Native{FailStart=true};using(var c=Ready(fake)){
    try{c.BeginVideoAsync(Cam("42"),new IntPtr(11),None).GetAwaiter().GetResult();throw new Exception("Expected failure");}catch(NativePtzException){}
    Check(fake.OpenHandles.Count==0&&fake.Closes==1,"Renderer start failure releases the newly allocated session");
   }
   fake=new Native();using(var c=Ready(fake,true)){
    IntPtr a=c.BeginVideoAsync(Cam("42"),new IntPtr(11),None).Result;Wait(c.MoveAsync(Cam("42"),PtzDirection.Up,None));fake.FailSend=true;
    try{Wait(c.EndVideoAsync(a));throw new Exception("Expected release failure");}catch(InvalidOperationException){}
    Check(fake.OpenHandles.Count==0&&fake.Closes==1,"Failed PTZ release still closes video and removes active-session bookkeeping");
   }
   fake=new Native();using(var c=Ready(fake))using(var cancelled=new CancellationTokenSource()){
    cancelled.Cancel();try{c.BeginVideoAsync(Cam("42"),new IntPtr(11),cancelled.Token).GetAwaiter().GetResult();throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
    Check(fake.Opens==0,"Cancelled video request never opens a native connection");
   }
   fake=new Native();using(var c=Ready(fake)){
    var p=new P2pVideoPlayer(c,Cam("42"),new IntPtr(11),Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"video-fixture-status"));p.SetAudio(true,62);Until(()=>p.State==PlayerState.Playing);
    Check(fake.Enabled&&fake.Level==62,"Player applies requested audio and observes native playback");
    p.Dispose();p.Dispose();Until(()=>fake.Closes==1);Check(fake.Closes==1,"Asynchronous player dispose closes once");
   }
   fake=new Native{VideoStateValue=5};using(var c=Ready(fake)){
    var p=new P2pVideoPlayer(c,Cam("42"),new IntPtr(11),Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"video-fixture-status"));Until(()=>fake.Closes==1);
    Check(p.State==PlayerState.Stopped&&fake.OpenHandles.Count==0,"Stopped native video is terminal and releases its session");
    p.Dispose();Wait(Get<Task>(p,"lifetime"));Check(fake.Closes==1,"Disposing an already stopped player does not close twice");
   }
   var stoppedAccount=new Account();using(var tile=new VideoTile(1,Cam("42"))){
    tile.ConnectAccount(stoppedAccount);tile.RefreshStatus();
    stoppedAccount.Players[0].Playback=PlayerState.Stopped;tile.RefreshStatus();
    Check(stoppedAccount.Players[0].Disposed&&Get<IVideoPlayer>(tile,"player")==null,"Stopped tile disposes and releases its player");
    Check(Get<Button>(tile,"connect").Visible&&Get<Button>(tile,"connect").Text=="재연결"&&Get<Label>(tile,"empty").Text.Contains("영상 연결 종료")&&Get<Label>(tile,"empty").Text.Contains("자동 재연결"),"Stopped tile presents reconnect instead of stale LIVE state");
   }
   fake=new Native{FailAudio=true};using(var c=Ready(fake)){
    var p=new P2pVideoPlayer(c,Cam("42"),new IntPtr(11),Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"video-fixture-status"));p.SetAudio(true,70);Until(()=>p.State==PlayerState.Playing);
    Check(fake.Closes==0,"Unavailable audio output does not stop live video");p.Dispose();Until(()=>fake.Closes==1);
   }
   string file=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AccountVideoTests.profile.xml");var store=new CameraStore(file);
   store.Save(new List<CameraProfile>{new CameraProfile{Name="Saved fixture",Host="127.0.0.1",DeviceId="42",ProtectedPassword="DO_NOT_DECRYPT"}});byte[] original=File.ReadAllBytes(file);
   var account=new Account();using(var form=new ViewerForm(account,store)){
    IntPtr hwnd=form.Handle;
    form.LoadAccountCameras(new[]{new CloudDeviceInfo{DeviceId="42",Name="Account A",DevConfig=7},new CloudDeviceInfo{DeviceId="42"},new CloudDeviceInfo{DeviceId="43",Name="Account B",PtzProtocol=AccountPtzProtocol.GWell},new CloudDeviceInfo{DeviceId="invalid"}});
    var list=Get<List<CameraProfile>>(form,"cameras");Check(list.Count==2&&list.All(x=>x.AccountCamera&&x.Host==""&&x.ProtectedPassword==""),"Account list deduplicates and creates password-free profiles");
    Check(list[0].Validate()==null&&list[0].DevConfig==7,"Account profile validates without host and preserves devConfig");
    Check(list[0].AccountProtocol==AccountPtzProtocol.Reoqoo&&list[1].AccountProtocol==AccountPtzProtocol.GWell,"Account profiles preserve product-specific PTZ routing");
    try{list[0].BuildRtspUri();throw new Exception("Unexpected RTSP URI");}catch(InvalidOperationException){}
    Check(!Get<Button>(form,"editButton").Enabled&&!Get<Button>(form,"addButton").Enabled,"Account list cannot overwrite saved local connection settings");
    Call(form,"ConnectSelected");Check(account.Players.Count==1&&Get<VlcEngine>(form,"engine")==null,"Account connect creates P2P player without initializing VLC");
    Check(account.Players[0].Enabled,"Selected account camera receives audio");
    var tiles=Get<List<VideoTile>>(form,"tiles");tiles[1].ConnectAccount(account);Call(form,"ApplyAudioSelection");Check(!account.Players[1].Enabled,"Unselected account camera stays muted");
    form.RestoreLocalCameras();Check(account.Players.All(x=>x.Disposed),"Returning to saved cameras disposes all account video players");
    list=Get<List<CameraProfile>>(form,"cameras");Check(list.Count==1&&!list[0].AccountCamera&&list[0].Name=="Saved fixture","Logout view restores saved local camera list");
    Check(original.SequenceEqual(File.ReadAllBytes(file)),"Account listing/playback never edits or decrypts the saved RTSP profile");
    form.LoadAccountCameras(new[]{new CloudDeviceInfo{DeviceId="42",Name="Account A"}});account.FailLogout=true;
    Wait((Task)typeof(ViewerForm).GetMethod("ManageAccountAsync",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null));
    list=Get<List<CameraProfile>>(form,"cameras");
    Check(!account.IsLoggedIn&&!Get<bool>(form,"accountMode")&&list.Count==1&&!list[0].AccountCamera&&list[0].Name=="Saved fixture","Logout native failure still restores local list after account was cleared");
    Check(Get<Button>(form,"addButton").Enabled&&original.SequenceEqual(File.ReadAllBytes(file)),"Failed logout restores local edit state without writing saved profiles");
    form.LoadAccountCameras(new[]{new CloudDeviceInfo{DeviceId="42",Name="Account A"}});
    using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))form.RestoreLocalCameras();
    Check(!Get<bool>(form,"settingsLoaded"),"Restore read failure revokes the initial successful settings-load state");
    var replacement=new List<CameraProfile>{new CameraProfile{Name="Replacement must not save",Host="127.0.0.2"}};
    bool persisted=(bool)typeof(ViewerForm).GetMethod("Persist",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{replacement});
    Check(!persisted&&original.SequenceEqual(File.ReadAllBytes(file)),"Persist remains blocked after failed restore even when file becomes writable again");
    form.RestoreLocalCameras();list=Get<List<CameraProfile>>(form,"cameras");
    Check(Get<bool>(form,"settingsLoaded")&&list.Count==1&&list[0].Name=="Saved fixture","A later successful restore reloads the original settings and clears the save block");
    form.Close();
   }
   Console.WriteLine("RESULT "+checks+" PASS; fake bridges and local UI fixtures only; no SDK/network/camera");return 0;
  }catch(Exception e){Console.WriteLine("FAIL "+e);return 1;}}
  sealed class Native:ICloudNativeBridge,ICloudVideoBridge {
   public bool Available{get{return true;}}public string Error{get{return null;}}
   public int Opens,Closes,Unregisters,Config,Level;public int VideoStateValue=3;public bool Enabled,FailStart,FailSend,FailAudio;public IntPtr Window,SentHandle,AudioHandle;
   public readonly HashSet<IntPtr> OpenHandles=new HashSet<IntPtr>(),Started=new HashSet<IntPtr>();
   public void Register(long id,byte[] token){}public void Unregister(){if(OpenHandles.Count!=0)throw new Exception("Unregister before close");Unregisters++;}
   public IntPtr Open(ulong id){var h=new IntPtr(++Opens);OpenHandles.Add(h);return h;}
   public void State(IntPtr h,out int state,out int error,out int received){state=5;error=0;received=0;}
   public int Send(IntPtr h,byte[] data){SentHandle=h;return FailSend?-8:0;}
   public void Close(IntPtr h){if(!OpenHandles.Remove(h))throw new Exception("Double close");Interlocked.Increment(ref Closes);}
   public void Configure(IntPtr h,int cfg){Config=cfg;}
   public void VideoStart(IntPtr h,IntPtr hwnd){if(FailStart)throw new NativePtzException(-60);Started.Add(h);Window=hwnd;}
   public void VideoStop(IntPtr h){}
   public void VideoState(IntPtr h,out int state,out int frames,out int error){state=VideoStateValue;frames=-1;error=0;}
   public void VideoAudio(IntPtr h,bool enabled,int level){if(FailAudio&&enabled)throw new NativePtzException(-410);Enabled=enabled;Level=level;AudioHandle=h;}
  }
  sealed class Player:IVideoPlayer {public bool Enabled,Disposed;public PlayerState Playback=PlayerState.Playing;public PlayerState State{get{return Playback;}}public void SetAudio(bool enabled,int v){Enabled=enabled;}public void Dispose(){Disposed=true;}}
  sealed class Account:IAccountPtzController,IAccountVideoController {
   public readonly List<Player> Players=new List<Player>();public bool LoggedIn=true,FailLogout;public bool IsReady{get{return true;}}public bool IsLoggedIn{get{return LoggedIn;}}public string Status{get{return "Fixture";}}
   public IReadOnlyList<CloudDeviceInfo> AccountDevices{get{return new List<CloudDeviceInfo>();}}public string DeviceListErrorCode{get{return null;}}
   public IVideoPlayer CreateVideoPlayer(CameraProfile c,IntPtr h){var p=new Player();Players.Add(p);return p;}
   public Task LoginAsync(string u,string p,CancellationToken t){return Task.FromResult(0);}public Task LogoutAsync(CancellationToken t){LoggedIn=false;if(FailLogout){var failure=new TaskCompletionSource<bool>();failure.SetException(new InvalidOperationException("Fake native logout failure"));return failure.Task;}return Task.FromResult(0);}public Task MoveAsync(CameraProfile c,PtzDirection d,CancellationToken t){return Task.FromResult(0);}public void Dispose(){}
  }
 }
}
