using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace C500GDesktop.Tests {
 internal static class ReconnectTests {
  static int checks;static string root;static readonly List<string> report=new List<string>();
  static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;report.Add("PASS "+label);Console.WriteLine(report.Last());}
  static T Get<T>(object o,string n){return (T)o.GetType().GetField(n,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(o);}
  static void Set(object o,string n,object v){o.GetType().GetField(n,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(o,v);}
  static object Call(object o,string n,params object[] args){return o.GetType().GetMethod(n,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(o,args);}
  static void Click(Control c){typeof(Control).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(c,new object[]{EventArgs.Empty});}
  static List<VideoTile> Tiles(ViewerForm v){return Get<List<VideoTile>>(v,"tiles").Where(t=>t.Camera!=null).ToList();}
  static VideoReconnectState Retry(VideoTile tile){return Get<VideoReconnectState>(tile,"retry");}
  static Player Playing(VideoTile tile){return Get<IVideoPlayer>(tile,"player") as Player;}
  static CameraStore Store(string name,int count){var s=new CameraStore(Path.Combine(root,name+".xml"));s.Save(Enumerable.Range(1,count).Select(i=>new CameraProfile{Name="Fixture "+i,Host="192.0.2."+i,DeviceId=i.ToString(),PtzEnabled=true}).ToList());return s;}
  static async Task Close(ViewerForm v){v.Close();for(int i=0;i<200&&!v.IsDisposed;i++)await Task.Delay(10);Check(v.IsDisposed,"Viewer shutdown completes");}
  static void Policy(){
   var state=new VideoReconnectState();var now=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
   state.Failed(now);Check(!state.IsDue(now.AddDays(1))&&!state.RetryAt.HasValue,"Disconnected camera never retries without connection intent");state.Request();
   foreach(int seconds in new[]{2,4,8,16,30,30}){state.BeginAttempt();state.Failed(now);Check(state.RetryAt==now.AddSeconds(seconds)&&!state.IsDue(now.AddSeconds(seconds).AddTicks(-1))&&state.IsDue(now.AddSeconds(seconds)),"Retry backoff boundary: "+seconds+" seconds");now=state.RetryAt.Value;}
   state.BeginAttempt();state.Healthy(now);state.Healthy(now.AddSeconds(29));state.Failed(now.AddSeconds(29));Check(state.RetryAt==now.AddSeconds(59),"Brief playback does not reset retry backoff");
   state.BeginAttempt();state.Healthy(now);state.NotPlaying();state.Healthy(now.AddSeconds(29));state.Healthy(now.AddSeconds(31));state.Failed(now.AddSeconds(31));Check(state.RetryAt==now.AddSeconds(61),"Interrupted playback does not count as stable recovery");
   state.BeginAttempt();state.Healthy(now);state.Healthy(now.AddSeconds(30));state.Failed(now.AddSeconds(31));Check(state.RetryAt==now.AddSeconds(33),"Thirty seconds of continuous healthy playback resets retry delay");
   state.Stop();state.Failed(now);Check(!state.Wanted&&!state.RetryAt.HasValue,"Manual stop clears retry intent and countdown");
  }
  static void TileCases(){
   var clock=new Clock();var camera=new CameraProfile{Name="Fixture",Host="192.0.2.1"};var factory=new Factory();
   using(var tile=new VideoTile(1,camera,()=>clock.Now)){
    IntPtr h=tile.Handle;
    foreach(var terminal in new[]{PlayerState.Error,PlayerState.Ended,PlayerState.Stopped}){
     tile.RequestConnection();tile.ConnectLocal(factory.Create);var old=Playing(tile);old.Playback=terminal;tile.RefreshStatus();
     Check(old.Disposed&&!tile.HasConnection&&Retry(tile).RetryAt==clock.Now.AddSeconds(2),"Terminal video state schedules retry and releases old player: "+terminal);
    }
    tile.RequestConnection();tile.ConnectLocal(factory.Create);var p=Playing(tile);p.Playback=PlayerState.Opening;clock.Now=clock.Now.AddSeconds(26);tile.RefreshStatus();Check(p.Disposed&&Retry(tile).RetryAt.HasValue,"Opening timeout schedules automatic recovery");
    tile.RequestConnection();tile.ConnectLocal(factory.Create);p=Playing(tile);clock.Now=clock.Now.AddMinutes(2);tile.RefreshStatus();p.Playback=PlayerState.Buffering;clock.Now=clock.Now.AddSeconds(1);tile.RefreshStatus();Check(tile.HasConnection,"Brief buffering after long playback does not time out immediately");
    clock.Now=clock.Now.AddSeconds(25);tile.RefreshStatus();Check(!tile.HasConnection&&Retry(tile).RetryAt.HasValue,"Persistent buffering is retried after 25 seconds without healthy playback");
    tile.RequestConnection();tile.ConnectLocal(factory.Create);p=Playing(tile);p.ThrowState=true;tile.RefreshStatus();Check(p.Disposed&&Retry(tile).RetryAt.HasValue,"State-query failure is recovered like a disconnected stream");
    tile.Disconnect();clock.Now=clock.Now.AddHours(1);tile.RefreshStatus();Check(!tile.RetryDue&&!tile.WantsConnection,"Explicit disconnect remains stopped after time advances");
   }
  }
  static async Task ViewerCases(){
   var clock=new Clock();var factory=new Factory();var account=new Account();var store=Store("local",6);
   using(var v=new ViewerForm(account,store,factory.Create,()=>clock.Now)){
    IntPtr h=v.Handle;Get<System.Windows.Forms.Timer>(v,"timer").Stop();await v.StartAccountAsync(false);var tiles=Tiles(v);var initial=factory.Calls;
    var bad=tiles[5];var old=Playing(bad);IntPtr window=Get<Panel>(bad,"surface").Handle;old.Playback=PlayerState.Error;Call(v,"RefreshState");
    await (Task)Call(v,"ReconnectDueAsync");Check(factory.Calls==initial,"Failed camera is not retried before its countdown");
    clock.Now=clock.Now.AddSeconds(2);await (Task)Call(v,"ReconnectDueAsync");
    Check(factory.Calls==initial+1&&bad.HasConnection&&Get<Panel>(bad,"surface").Handle==window&&old.Disposed,"Off-page camera recovers with the same renderer handle");
    Check(tiles.Take(5).All(t=>Playing(t)==factory.All[tiles.IndexOf(t)]&&!Playing(t).Disposed),"Reconnecting one camera preserves all healthy streams");
    factory.Fail.Add(bad.Camera.DeviceId);Playing(bad).Playback=PlayerState.Error;Call(v,"RefreshState");clock.Now=Retry(bad).RetryAt.Value;await (Task)Call(v,"ReconnectDueAsync");
    Check(!bad.HasConnection&&Retry(bad).RetryAt==clock.Now.AddSeconds(8),"A failed retry schedules the next backoff instead of looping immediately");
    int calls=factory.Calls;await (Task)Call(v,"ReconnectDueAsync");Check(factory.Calls==calls,"Repeated timer passes cannot overlap or flood failed attempts");
    factory.Fail.Clear();clock.Now=Retry(bad).RetryAt.Value;await (Task)Call(v,"ReconnectDueAsync");Check(bad.HasConnection,"Camera resumes automatically when connection creation recovers");
    Playing(tiles[0]).Playback=PlayerState.Error;Playing(tiles[1]).Playback=PlayerState.Error;Call(v,"RefreshState");clock.Now=clock.Now.AddSeconds(2);calls=factory.Calls;await (Task)Call(v,"ReconnectDueAsync");Check(factory.Calls==calls+1,"Only one due camera reconnects per timer pass");await (Task)Call(v,"ReconnectDueAsync");Check(factory.Calls==calls+2,"Next due camera recovers on the following pass");
    Check(factory.All.Count(p=>!p.Disposed&&p.Audio)==1&&Playing(tiles[0]).Audio,"Selected-camera audio is restored without enabling other streams");
    Playing(tiles[0]).Playback=PlayerState.Error;Call(v,"RefreshState");clock.Now=Retry(tiles[0]).RetryAt.Value;Set(v,"movingCamera",tiles[0].Camera);account.StopGate=new TaskCompletionSource<bool>();
    calls=factory.Calls;Task pending=(Task)Call(v,"ReconnectDueAsync");Check(account.Stops==1&&!pending.IsCompleted,"Retry waits for active PTZ stop before replacing its session");
    Click(Get<Button>(v,"disconnectButton"));Check(!tiles[0].WantsConnection,"Manual disconnect cancels retry intent immediately while PTZ stop is pending");account.StopGate.SetResult(true);await pending;await Task.Delay(20);Check(factory.Calls==calls&&!tiles[0].HasConnection,"No late reconnect after manual disconnect wins the PTZ-stop race");account.StopGate=null;
    Playing(tiles[1]).Playback=PlayerState.Error;Call(v,"RefreshState");Call(v,"StopAllVideos");clock.Now=clock.Now.AddMinutes(1);await (Task)Call(v,"ReconnectDueAsync");Check(factory.Calls==calls&&tiles.All(t=>!t.HasConnection&&!t.WantsConnection),"Stop-all disables all scheduled automatic reconnects");
    await (Task)Call(v,"ConnectAllAsync");Playing(tiles[0]).Playback=PlayerState.Error;Call(v,"RefreshState");calls=factory.Calls;clock.Now=clock.Now.AddSeconds(2);Get<System.Windows.Forms.Timer>(v,"timer").Start();await Task.Delay(750);Get<System.Windows.Forms.Timer>(v,"timer").Stop();Check(factory.Calls==calls+1&&tiles[0].HasConnection,"Actual WinForms timer drives automatic reconnection without a user click");
    Playing(tiles[1]).Playback=PlayerState.Error;Call(v,"RefreshState");var removed=tiles[1];Get<List<CameraProfile>>(v,"cameras").Remove(removed.Camera);Call(v,"BuildGrid");clock.Now=clock.Now.AddMinutes(1);calls=factory.Calls;await (Task)Call(v,"ReconnectDueAsync");Check(removed.IsDisposed&&factory.Calls==calls,"Removing a camera cancels its queued reconnect");
    await Close(v);Check(factory.All.All(p=>p.Disposed),"Application close disposes all active and replaced players");
   }
   store=Store("pending",5);factory=new Factory();clock=new Clock();
   using(var v=new ViewerForm(new UnavailablePtzController(),store,factory.Create,()=>clock.Now)){
    IntPtr h=v.Handle;Get<System.Windows.Forms.Timer>(v,"timer").Stop();Task batch=v.StartAccountAsync(false);var tile=Tiles(v)[4];tile.Disconnect();await batch;
    Check(!tile.HasConnection&&!tile.WantsConnection&&factory.Calls==4,"Manual disconnect also cancels a camera still queued in startup connect-all");await Close(v);
   }
  }
  static async Task AccountCases(){
   var clock=new Clock();var account=new Account{LoggedIn=true};var store=Store("account",1);
   using(var v=new ViewerForm(account,store,(c,h)=>{throw new Exception("Unexpected RTSP");},()=>clock.Now)){
    IntPtr h=v.Handle;Get<System.Windows.Forms.Timer>(v,"timer").Stop();v.LoadAccountCameras(account.AccountDevices);await (Task)Call(v,"ConnectAllAsync");var tiles=Tiles(v);Playing(tiles[0]).Playback=PlayerState.Stopped;Call(v,"RefreshState");clock.Now=clock.Now.AddSeconds(2);await (Task)Call(v,"ReconnectDueAsync");
    Check(account.Factory.Calls==3&&tiles.All(t=>t.HasConnection)&&Get<VlcEngine>(v,"engine")==null,"Account/P2P stream reconnects without RTSP settings or account relogin");
    Playing(tiles[1]).Playback=PlayerState.Error;Call(v,"RefreshState");int before=account.Factory.Calls;await (Task)Call(v,"ManageAccountAsync");clock.Now=clock.Now.AddMinutes(1);await (Task)Call(v,"ReconnectDueAsync");Check(!account.LoggedIn&&account.Factory.Calls==before&&account.Factory.All.All(p=>p.Disposed),"Logout cancels pending account retries without reconnecting or reauthenticating");await Close(v);
   }
  }
  [STAThread]static int Main(){
   int exit=0;root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Reconnect-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   using(var context=new ApplicationContext())using(var timer=new System.Windows.Forms.Timer()){
    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());timer.Interval=10;timer.Tick+=async delegate{timer.Stop();try{Policy();TileCases();await ViewerCases();await AccountCases();report.Add("RESULT "+checks+" PASS; fake clock and players; no real logins/cameras/network disruption");Console.WriteLine(report.Last());}catch(Exception ex){exit=1;report.Add("FAIL "+ex);Console.WriteLine(report.Last());}finally{context.ExitThread();}};timer.Start();Application.Run(context);
   }
   File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ReconnectTests.results.txt"),report);
   if(Path.GetDirectoryName(Path.GetFullPath(root)).TrimEnd(Path.DirectorySeparatorChar)==AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)&&Path.GetFileName(root).StartsWith("Reconnect-fixture-",StringComparison.Ordinal))Directory.Delete(root,true);return exit;
  }
  sealed class Clock{public DateTime Now=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);}
  sealed class Player:IVideoPlayer{public bool Disposed,Audio,ThrowState;public PlayerState Playback=PlayerState.Playing;public PlayerState State{get{if(ThrowState)throw new IOException("Fake state failure");return Playback;}}public void SetAudio(bool enabled,int level){Audio=enabled;}public void Dispose(){Disposed=true;Audio=false;}}
  sealed class Factory{public int Calls;public readonly HashSet<string> Fail=new HashSet<string>();public readonly List<Player> All=new List<Player>();public IVideoPlayer Create(CameraProfile c,IntPtr h){Calls++;if(Fail.Contains(c.DeviceId))throw new IOException("Fake failure");var p=new Player();All.Add(p);return p;}}
  sealed class Account:IAccountPtzController,IAccountVideoController{
   public bool LoggedIn;public int Stops;public TaskCompletionSource<bool> StopGate;public readonly Factory Factory=new Factory();public bool IsLoggedIn{get{return LoggedIn;}}public bool IsReady{get{return true;}}public string Status{get{return "Fixture";}}
   public IReadOnlyList<CloudDeviceInfo> AccountDevices{get{return new List<CloudDeviceInfo>{new CloudDeviceInfo{DeviceId="1",Name="Fixture A"},new CloudDeviceInfo{DeviceId="2",Name="Fixture B"}};}}public string DeviceListErrorCode{get{return null;}}
   public IVideoPlayer CreateVideoPlayer(CameraProfile c,IntPtr h){return Factory.Create(c,h);}public Task LoginAsync(string u,string p,CancellationToken t){LoggedIn=true;return Task.FromResult(0);}public Task LogoutAsync(CancellationToken t){LoggedIn=false;return Task.FromResult(0);}
   public Task MoveAsync(CameraProfile c,PtzDirection d,CancellationToken t){if(d==PtzDirection.Stop){Stops++;if(StopGate!=null)return StopGate.Task;}return Task.FromResult(0);}public void Dispose(){LoggedIn=false;}
  }
 }
}
