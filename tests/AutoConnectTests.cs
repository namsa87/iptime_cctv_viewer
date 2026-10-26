using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace C500GDesktop.Tests {
 internal static class AutoConnectTests {
  static int checks;static string root;static readonly List<string> report=new List<string>();
  const string User="fixture@example.invalid",Password="synthetic-only";
  static void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;report.Add("PASS "+text);Console.WriteLine(report.Last());}
  static T Get<T>(object o,string n){return (T)o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);}
  static void Set(object o,string n,object v){o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(o,v);}
  static object Call(object o,string n){return o.GetType().GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(o,null);}
  static List<VideoTile> RealTiles(ViewerForm f){return Get<List<VideoTile>>(f,"tiles").Where(t=>t.Camera!=null).ToList();}
  static void Page(ViewerForm f,int page,int count){Set(f,"page",page);Set(f,"layoutCount",count);Call(f,"BuildGrid");}
  static CameraStore Store(string name,int count){
   var store=new CameraStore(Path.Combine(root,name+".xml"));
   store.Save(Enumerable.Range(1,count).Select(i=>new CameraProfile{Name="Fixture "+i,Host="192.0.2."+i,DeviceId=i.ToString(),PtzEnabled=true}).ToList());return store;
  }
  static async Task Close(ViewerForm f){f.Close();for(int i=0;i<100&&!f.IsDisposed;i++)await Task.Delay(10);Check(f.IsDisposed,"Viewer shutdown completes");}
  static async Task LocalCases(){
   var store=Store("local",7);byte[] original=File.ReadAllBytes(store.FilePath);var factory=new Factory();
   using(var viewer=new ViewerForm(new UnavailablePtzController(),store,factory.Create)){
    IntPtr h=viewer.Handle;await viewer.StartAccountAsync(false);await viewer.StartAccountAsync(false);
    Check(factory.Calls.Count==7&&factory.Players.Count==7&&RealTiles(viewer).All(t=>t.HasConnection),"Startup connects every configured RTSP camera including those beyond page one, exactly once");
    Check(factory.Calls.Select(c=>c.Item2).Distinct().Count()==7&&factory.Calls.All(c=>c.Item2!=IntPtr.Zero),"Every stream receives its own valid renderer handle");
    Check(original.SequenceEqual(File.ReadAllBytes(store.FilePath)),"Startup leaves saved camera configuration untouched");
    var tiles=RealTiles(viewer);var handles=tiles.Select(t=>Get<Panel>(t,"surface").Handle).ToArray();
    Page(viewer,1,4);
    Check(factory.Calls.Count==7&&factory.Players.All(p=>!p.Disposed),"Page change keeps all visible and hidden stream sessions alive");
    Check(tiles.Select(t=>Get<Panel>(t,"surface").Handle).SequenceEqual(handles),"Page change preserves HWNDs used by native video renderers");
    Check(Get<TableLayoutPanel>(viewer,"grid").Controls.Count==4&&Get<CameraProfile>(viewer,"selected").DeviceId=="5","Page selection follows the visible camera and retains empty slots");
    Check(factory.Players.Count(p=>p.Audio)==1&&factory.Players[4].Audio,"Only selected visible camera supplies audio after page change");
    Page(viewer,0,9);
    Check(factory.Calls.Count==7&&tiles.Select(t=>Get<Panel>(t,"surface").Handle).SequenceEqual(handles),"Layout change retains players and rendering handles");
    await (Task)Call(viewer,"ConnectAllAsync");Check(factory.Calls.Count==7,"Repeated connect-all does not restart active or connecting streams");
    Call(viewer,"StopAllVideos");Page(viewer,0,4);
    Check(factory.Players.All(p=>p.Disposed)&&RealTiles(viewer).All(t=>!t.HasConnection),"Stop-all disconnects hidden streams and page change does not reconnect them");
    await (Task)Call(viewer,"ConnectAllAsync");Check(factory.Calls.Count==14,"Explicit connect-all reconnects every stopped camera");
    await Close(viewer);Check(factory.Players.All(p=>p.Disposed),"Shutdown releases every visible and hidden player");
   }
   store=Store("failure",6);factory=new Factory{FailDevice="2"};
   using(var viewer=new ViewerForm(new UnavailablePtzController(),store,factory.Create)){
    IntPtr h=viewer.Handle;await viewer.StartAccountAsync(false);
    Check(factory.Calls.Count==6&&factory.Players.Count==5,"One stream start failure does not block later cameras");
    Check(Get<Label>(viewer,"banner").Text.Contains("실패 1대"),"Batch completion retains failed-camera count");
    factory.FailDevice=null;await (Task)Call(viewer,"ConnectAllAsync");
    Check(factory.Calls.Count==7&&RealTiles(viewer).All(t=>t.HasConnection),"Retry-all starts only the failed stream");
    factory.Players[0].Playback=PlayerState.Error;await (Task)Call(viewer,"ConnectAllAsync");
    Check(factory.Calls.Count==8&&factory.Players[0].Disposed,"Retry-all replaces failed playback without disturbing healthy cameras");
    await Close(viewer);
   }
   store=Store("cancel",13);factory=new Factory();
   using(var viewer=new ViewerForm(new UnavailablePtzController(),store,factory.Create)){
    IntPtr h=viewer.Handle;Task pending=viewer.StartAccountAsync(false);await Task.Delay(150);Call(viewer,"StopAllVideos");int count=factory.Calls.Count;await pending;await Task.Delay(150);
    Check(count>0&&count<13&&factory.Calls.Count==count&&factory.Players.All(p=>p.Disposed),"Stop-all cancels pending startup work with no late connections");
    pending=(Task)Call(viewer,"ConnectAllAsync");await Task.Delay(120);await Close(viewer);count=factory.Calls.Count;await pending;await Task.Delay(100);
    Check(factory.Calls.Count==count&&factory.Players.All(p=>p.Disposed),"Closing during a batch cancels queued connections and releases players");
   }
  }
  static async Task AccountCases(){
   var store=Store("account",2);string warning;var saved=new AutoLoginStore(store.FilePath+".account-login.dat");Check(saved.Save(User,Password,out warning),"Synthetic automatic-login fixture saved");
   var account=new Account();var local=new Factory();
   using(var viewer=new ViewerForm(account,store,local.Create)){
    IntPtr h=viewer.Handle;await viewer.StartAccountAsync(false);
    Check(account.Logins==1&&local.Calls.Count==0,"Saved account startup does not connect stale local/RTSP profiles first");
    Check(account.Factory.Calls.Count==13&&RealTiles(viewer).Count==13&&RealTiles(viewer).All(t=>t.HasConnection),"Automatic account login connects all 13 cameras, including those beyond the 12-view layout");
    var tiles=RealTiles(viewer);var handles=tiles.Select(t=>Get<Panel>(t,"surface").Handle).ToArray();Page(viewer,3,4);
    Check(account.Factory.Players.All(p=>!p.Disposed)&&tiles.Select(t=>Get<Panel>(t,"surface").Handle).SequenceEqual(handles),"Fourth page preserves all P2P player sessions and render handles");
    Check(account.Factory.Players.Count(p=>p.Audio)==1&&account.Factory.Players[12].Audio,"P2P audio switches to the visible selected camera");
    Page(viewer,0,12);await (Task)Call(viewer,"ConnectAllAsync");
    Check(account.Factory.Calls.Count==13&&account.Factory.Players.All(p=>!p.Disposed),"12-view layout and repeated connect-all retain the thirteenth stream");
    Get<ComboBox>(viewer,"layoutBox").SelectedIndex=2;viewer.ShowInTaskbar=false;viewer.Opacity=0;viewer.Show();await Task.Delay(100);foreach(var tile in tiles)tile.RefreshStatus();viewer.SavePreview(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AutoConnect141.png"));
    await (Task)Call(viewer,"ManageAccountAsync");
    Check(!saved.HasSavedCredentials&&!account.IsLoggedIn&&account.Factory.Players.All(p=>p.Disposed),"Logout clears automatic login and closes every account stream");
    Check(local.Calls.Count==0&&RealTiles(viewer).All(t=>!t.HasConnection),"Logout restores local profiles without silently starting them");
    await Close(viewer);
   }
  }
  [STAThread]static int Main(){
   int exit=0;root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AutoConnect-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   using(var context=new ApplicationContext())using(var timer=new System.Windows.Forms.Timer()){
    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());timer.Interval=10;timer.Tick+=async delegate{
     timer.Stop();try{await LocalCases();await AccountCases();report.Add("RESULT "+checks+" PASS; fake players; real_logins=0; camera_connections=0");Console.WriteLine(report.Last());}
     catch(Exception ex){exit=1;report.Add("FAIL "+ex);Console.WriteLine(report.Last());}finally{context.ExitThread();}
    };timer.Start();Application.Run(context);
   }
   File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AutoConnectTests.results.txt"),report);
   if(Path.GetDirectoryName(Path.GetFullPath(root)).TrimEnd(Path.DirectorySeparatorChar)==AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)&&Path.GetFileName(root).StartsWith("AutoConnect-fixture-",StringComparison.Ordinal))Directory.Delete(root,true);
   return exit;
  }
  sealed class Player:IVideoPlayer {
   public bool Audio,Disposed;public PlayerState Playback=PlayerState.Playing;
   public PlayerState State{get{return Playback;}}public void SetAudio(bool value,int level){Audio=value;}public void Dispose(){Disposed=true;Audio=false;}
  }
  sealed class Factory {
   public string FailDevice;public readonly List<Tuple<string,IntPtr>> Calls=new List<Tuple<string,IntPtr>>();public readonly List<Player> Players=new List<Player>();
   public IVideoPlayer Create(CameraProfile camera,IntPtr window){Calls.Add(Tuple.Create(camera.DeviceId,window));if(camera.DeviceId==FailDevice)throw new IOException("Synthetic start failure");var player=new Player();Players.Add(player);return player;}
  }
  sealed class Account:IAccountPtzController,IAccountVideoController {
   public int Logins;public readonly Factory Factory=new Factory();public bool IsLoggedIn{get;private set;}public bool IsReady{get{return IsLoggedIn;}}public string Status{get{return "Fixture";}}
   public IReadOnlyList<CloudDeviceInfo> AccountDevices{get{return Enumerable.Range(1,13).Select(i=>new CloudDeviceInfo{DeviceId=i.ToString(),Name="Fixture camera "+i,PtzProtocol=AccountPtzProtocol.Reoqoo}).ToList();}}
   public string DeviceListErrorCode{get{return null;}}
   public IVideoPlayer CreateVideoPlayer(CameraProfile camera,IntPtr window){return Factory.Create(camera,window);}
   public Task LoginAsync(string u,string p,CancellationToken t){if(u!=User||p!=Password)throw new Exception("Unexpected fixture account");Logins++;IsLoggedIn=true;return Task.FromResult(0);}
   public Task LogoutAsync(CancellationToken t){IsLoggedIn=false;return Task.FromResult(0);}public Task MoveAsync(CameraProfile c,PtzDirection d,CancellationToken t){return Task.FromResult(0);}public void Dispose(){IsLoggedIn=false;}
  }
 }
}
