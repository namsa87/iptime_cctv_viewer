using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace C500GDesktop.Tests {
 internal static class AutoLoginTests {
  static int checks;static readonly List<string> report=new List<string>();
  const string User="fixture@example.invalid",Password="Only-Fake-Password-!테스트";
  static string root;
  static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;report.Add("PASS "+label);Console.WriteLine(report.Last());}
  static T Get<T>(object obj,string field){return (T)obj.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(obj);}
  static Task Call(object obj,string method){return (Task)obj.GetType().GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(obj,null);}
  static void Fill(AccountLoginForm f,bool save){IntPtr handle=f.Handle;Get<TextBox>(f,"username").Text=User;Get<TextBox>(f,"password").Text=Password;Get<CheckBox>(f,"remember").Checked=save;}
  static void StoreCases(){
   string path=Path.Combine(root,"store.dat"),warning;var store=new AutoLoginStore(path);
   Check(!store.HasSavedCredentials&&store.Load(out warning)==null&&warning==null,"Missing store requires manual login");
   Check(store.Save(User,Password,out warning)&&warning==null,"Current-user DPAPI saves synthetic credentials");
   byte[] encrypted=File.ReadAllBytes(path);
   Check(!Encoding.UTF8.GetString(encrypted).Contains(User)&&!Encoding.UTF8.GetString(encrypted).Contains(Password),"Username and password are absent from stored plaintext");
   using(var saved=store.Load(out warning))Check(saved!=null&&saved.Username==User&&saved.Password==Password&&warning==null,"Saved account round-trips with current-user DPAPI");
   Check(store.Save(User,"replacement",out warning),"Atomic replacement updates saved account");
   using(var saved=store.Load(out warning))Check(saved.Password=="replacement","Updated password is returned after restart");
   byte[] original=File.ReadAllBytes(path);
   using(var locked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
    Check(!store.Save(User,Password,out warning)&&warning!=null,"Locked storage reports save failure");
    Check(!store.ClearSaved(out warning)&&warning!=null,"Locked storage reports failure to disable automatic login");
    Check(store.Load(out warning)==null&&warning!=null,"Unreadable credentials fall back to manual login");
   }
   Check(original.SequenceEqual(File.ReadAllBytes(path))&&Directory.GetFiles(root,"*.tmp").Length==0,"Failed atomic save preserves previous account and removes temporary file");
   File.WriteAllBytes(path,new byte[]{1,2,3});
   Check(store.Load(out warning)==null&&warning!=null,"Corrupt encrypted store falls back without exposing contents");
   File.WriteAllBytes(path,new byte[16385]);Check(store.Load(out warning)==null&&warning!=null,"Oversized credentials are rejected before decoding");
   File.WriteAllBytes(path,ProtectedData.Protect(Encoding.UTF8.GetBytes("different fixture"),null,DataProtectionScope.CurrentUser));
   Check(store.Load(out warning)==null&&warning!=null,"Incompatible encrypted blob cannot be used as credentials");
   Check(store.ClearSaved(out warning)&&!store.HasSavedCredentials&&store.ClearSaved(out warning),"Disabling automatic login is idempotent");
  }
  static async Task DialogCases(){
   string path=Path.Combine(root,"dialog.dat");var store=new AutoLoginStore(path);
   using(var f=new AccountLoginForm((u,p,t)=>Task.FromResult(0),store,false)){
    IntPtr handle=f.Handle;Check(!Get<CheckBox>(f,"remember").Checked,"First login requires explicit automatic-login opt-in");
    using(var bitmap=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(bitmap,new Rectangle(Point.Empty,f.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AutoLogin140.png"));}
    Fill(f,false);await Call(f,"SubmitAsync");Check(f.DialogResult==DialogResult.OK&&!store.HasSavedCredentials,"Unchecked login never stores credentials");
   }
   using(var f=new AccountLoginForm((u,p,t)=>{throw new CloudAccountException("DENIED","계정 정보를 확인해 주세요.");},store,false)){
    Fill(f,true);await Call(f,"SubmitAsync");Check(!store.HasSavedCredentials&&f.DialogResult!=DialogResult.OK,"Failed authentication never saves typed credentials");
   }
   using(var f=new AccountLoginForm((u,p,t)=>Task.FromResult(0),store,false)){
    Fill(f,true);await Call(f,"SubmitAsync");Check(store.HasSavedCredentials&&Get<TextBox>(f,"password").Text=="","Successful opt-in saves credentials and clears password control");
   }
   int attempts=0;
   using(var f=new AccountLoginForm((u,p,t)=>{attempts++;Check(u==User&&p==Password,"Automatic login submits restored synthetic account");return Task.FromResult(0);},store,true)){
    await Call(f,"RestoreAndSubmitAsync");await Call(f,"RestoreAndSubmitAsync");Check(attempts==1&&f.DialogResult==DialogResult.OK,"Startup automatic login attempts once and completes login dialog");
   }
   attempts=0;byte[] before=File.ReadAllBytes(path);
   using(var f=new AccountLoginForm((u,p,t)=>{attempts++;throw new CloudAccountException("NETWORK","네트워크를 확인해 주세요.");},store,true)){
    await Call(f,"RestoreAndSubmitAsync");await Call(f,"RestoreAndSubmitAsync");
    Check(attempts==1&&Get<Button>(f,"login").Enabled&&Get<TextBox>(f,"password").Text==""&&f.DialogResult!=DialogResult.OK,"Automatic-login failure restores manual entry without retry loop");
    Check(before.SequenceEqual(File.ReadAllBytes(path)),"Transient authentication failure preserves encrypted credentials");
   }
   using(var f=new AccountLoginForm(async (u,p,t)=>{await Task.Delay(10000,t);},store,true)){
    Task pending=Call(f,"RestoreAndSubmitAsync");await Task.Delay(20);Get<CancellationTokenSource>(f,"cancellation").Cancel();await pending;
    Check(f.DialogResult!=DialogResult.OK&&Get<TextBox>(f,"password").Text==""&&before.SequenceEqual(File.ReadAllBytes(path)),"Cancelled automatic request clears password without overwriting saved account");
   }
   using(var locked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))using(var f=new AccountLoginForm((u,p,t)=>Task.FromResult(0),store,false)){
    Fill(f,true);await Call(f,"SubmitAsync");Check(f.DialogResult==DialogResult.OK&&f.PersistenceWarning!=null,"Successful login remains usable when encrypted persistence fails");
   }
   using(var f=new AccountLoginForm((u,p,t)=>Task.FromResult(0),store,false)){
    Fill(f,false);await Call(f,"SubmitAsync");Check(!store.HasSavedCredentials,"Successful unchecked login removes earlier automatic credentials");
   }
   File.WriteAllBytes(path,new byte[]{7});attempts=0;
   using(var f=new AccountLoginForm((u,p,t)=>{attempts++;return Task.FromResult(0);},store,true)){
    await Call(f,"RestoreAndSubmitAsync");Check(attempts==0&&Get<Button>(f,"login").Enabled&&Get<Label>(f,"status").Text.Contains("다시 입력"),"Corrupt store displays manual recovery without authenticating");
   }
  }
  static async Task ViewerCases(){
   string profile=Path.Combine(root,"cameras.xml"),warning;var store=new CameraStore(profile);
   store.Save(new List<CameraProfile>{new CameraProfile{Name="Local fixture",Host="127.0.0.1",DeviceId="42"}});
   byte[] original=File.ReadAllBytes(profile);var saved=new AutoLoginStore(profile+".account-login.dat");var account=new Account();
   using(var viewer=new ViewerForm(account,store,(c,h)=>new Player())){
    IntPtr handle=viewer.Handle;await viewer.StartAccountAsync(false);Check(account.Logins==0,"Normal startup without saved account does not contact the account service");
    viewer.Close();await Task.Delay(50);
   }
   Check(saved.Save(User,Password,out warning),"Viewer restart fixture stores opt-in account");account=new Account();
   using(var viewer=new ViewerForm(account,store,(c,h)=>new Player())){
    IntPtr handle=viewer.Handle;await viewer.StartAccountAsync(false);await viewer.StartAccountAsync(false);
    Check(account.Logins==1&&account.IsLoggedIn,"Viewer startup restores account exactly once");
    Check(account.Players.Count==1&&Get<VlcEngine>(viewer,"engine")==null,"Automatic login connects first account video without RTSP or VLC");
    Check(Get<List<CameraProfile>>(viewer,"cameras").Single().AccountCamera&&original.SequenceEqual(File.ReadAllBytes(profile)),"Automatic account list preserves RTSP profiles");
    viewer.Close();await Task.Delay(100);Check(saved.HasSavedCredentials,"Closing app retains opt-in for next startup");
   }
   account=new Account();using(var viewer=new ViewerForm(account,store,(c,h)=>new Player())){
    IntPtr handle=viewer.Handle;await viewer.StartAccountAsync(false);await Call(viewer,"ManageAccountAsync");
    Check(!saved.HasSavedCredentials&&!account.IsLoggedIn&&!Get<bool>(viewer,"accountMode"),"Explicit logout disables automatic login and restores local profiles");
    Check(account.Players.All(p=>p.Disposed),"Logout stops automatically connected video players");viewer.Close();await Task.Delay(50);
   }
  }
  [STAThread]static int Main(){
   int exit=0;root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AutoLogin-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   using(var context=new ApplicationContext())using(var start=new System.Windows.Forms.Timer()){
    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());start.Interval=10;start.Tick+=async delegate{
     start.Stop();try{StoreCases();await DialogCases();await ViewerCases();report.Add("RESULT "+checks+" PASS; synthetic DPAPI fixtures; real_logins=0; camera_connections=0");Console.WriteLine(report.Last());}
     catch(Exception ex){exit=1;report.Add("FAIL "+ex);Console.WriteLine(report.Last());}finally{context.ExitThread();}
    };start.Start();Application.Run(context);
   }
   File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AutoLoginTests.results.txt"),report);
   // Only this freshly created, resolved fixture directory is removed.
   if(Path.GetDirectoryName(Path.GetFullPath(root)).TrimEnd(Path.DirectorySeparatorChar)==AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)&&Path.GetFileName(root).StartsWith("AutoLogin-fixture-",StringComparison.Ordinal))Directory.Delete(root,true);
   return exit;
  }
  sealed class Player:IVideoPlayer {public bool Disposed;public PlayerState State{get{return PlayerState.Playing;}}public void SetAudio(bool enabled,int volume){}public void Dispose(){Disposed=true;}}
  sealed class Account:IAccountPtzController,IAccountVideoController {
   public int Logins;public bool IsLoggedIn{get;private set;}public bool IsReady{get{return IsLoggedIn;}}public string Status{get{return "Fixture";}}
   public readonly List<Player> Players=new List<Player>();
   public IReadOnlyList<CloudDeviceInfo> AccountDevices{get{return new List<CloudDeviceInfo>{new CloudDeviceInfo{DeviceId="42",Name="Fixture camera",PtzProtocol=AccountPtzProtocol.Reoqoo}};}}
   public string DeviceListErrorCode{get{return null;}}
   public IVideoPlayer CreateVideoPlayer(CameraProfile camera,IntPtr handle){var player=new Player();Players.Add(player);return player;}
   public Task LoginAsync(string user,string password,CancellationToken token){if(user!=User||password!=Password)throw new InvalidOperationException("Synthetic credentials mismatch");Logins++;IsLoggedIn=true;return Task.FromResult(0);}
   public Task LogoutAsync(CancellationToken token){IsLoggedIn=false;return Task.FromResult(0);}
   public Task MoveAsync(CameraProfile camera,PtzDirection direction,CancellationToken token){return Task.FromResult(0);}
   public void Dispose(){IsLoggedIn=false;}
  }
 }
}
