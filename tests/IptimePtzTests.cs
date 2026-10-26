using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using C500GDesktop;
namespace C500GDesktop.Tests {
 internal static class IptimePtzTests {
  static int count;static void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
  static void Wait(Task t){t.GetAwaiter().GetResult();}
  static CameraProfile Cam(string id="42"){return new CameraProfile{DeviceId=id,AccountCamera=true,PtzEnabled=true};}
  static byte[][] Vectors(){
   string file=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"native-signing","xiaotun-ptz-vectors.json");
   return Regex.Matches(File.ReadAllText(file),"\"hex\":\"([0-9A-F]+)\"").Cast<Match>().Select(m=>Enumerable.Range(0,m.Groups[1].Value.Length/2).Select(i=>Convert.ToByte(m.Groups[1].Value.Substring(2*i,2),16)).ToArray()).ToArray();
  }
  static int Main(){try{
   var v=Vectors();Check(v.Length==8,"Eight independent original Java binary reference vectors loaded");
   var dirs=new[]{PtzDirection.Left,PtzDirection.Right,PtzDirection.Up,PtzDirection.Down};
   for(int i=0;i<4;i++)for(int r=0;r<2;r++)Check(CloudPtzController.BuildIptimePayload(dirs[i],r==1).SequenceEqual(v[i*2+r]),"Original 24-byte vector "+dirs[i]+" release="+r);
   foreach(string pid in new[]{"343597383680548","343597383680547","343599531164229","343599531164230",""})Check(CloudAccountClient.ResolveProductPtz(pid)==AccountPtzProtocol.Reoqoo,"APK product solution selects REOQOO: "+pid);
   Check(CloudAccountClient.ResolveProductPtz("343599531164151")==AccountPtzProtocol.GWell,"APK C300E product keeps distinct GWell route");
   var metadata=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,object>>(@"{""devId"":42,""saas"":{""productId"":""343599531164151""}}");
   var parsedDevice=CloudAccountClient.ParseDeviceInfo(metadata);
   Check(parsedDevice.ProductId=="343599531164151"&&parsedDevice.PtzProtocol==AccountPtzProtocol.GWell,"Device-list saas.productId reaches protocol selection");
   var fake=new Fake();using(var c=new CloudPtzController(fake)){
    Wait(c.RegisterAsync(7,new string('a',128),CancellationToken.None));Wait(c.PrepareAsync(Cam(),CancellationToken.None));
    Check(fake.Payloads.Count==0&&fake.ModeQueries==0&&!c.RepeatWhileHeld,"Account preparation uses C500G protocol without movement or GWell model query");
    for(int i=0;i<4;i++){
     fake.Payloads.Clear();Wait(c.MoveAsync(Cam(),dirs[i],CancellationToken.None));Wait(c.MoveAsync(Cam(),dirs[i],CancellationToken.None));
     Check(fake.Payloads.Count==1&&fake.Payloads[0].SequenceEqual(v[i*2])&&!c.RepeatWhileHeld,"Held "+dirs[i]+" sends one start, without timed stop or repeat");
     Wait(c.MoveAsync(Cam(),PtzDirection.Stop,CancellationToken.None));
     Check(fake.Payloads.Count==2&&fake.Payloads[1].SequenceEqual(v[i*2+1]),"Release "+dirs[i]+" uses its own paired stop");
    }
    fake.Payloads.Clear();Wait(c.MoveAsync(Cam(),PtzDirection.Up,CancellationToken.None));Wait(c.MoveAsync(Cam(),PtzDirection.Right,CancellationToken.None));
    Check(fake.Payloads.Select(b=>b[1]).SequenceEqual(new byte[]{9,10,7}),"Direction change releases previous direction before new start");
    Wait(c.MoveAsync(Cam("43"),PtzDirection.Down,CancellationToken.None));
    Check(fake.Events.IndexOf("send:42:8")>=0&&fake.Events.IndexOf("send:42:8")<fake.Events.IndexOf("close:42")&&fake.Events.IndexOf("close:42")<fake.Events.IndexOf("open:43")&&fake.Events.Last()=="send:43:11","Camera switch releases old direction and starts the new camera");
    fake.Payloads.Clear();Wait(c.LogoutAsync(CancellationToken.None));
    Check(fake.Payloads.Count==1&&fake.Payloads[0].SequenceEqual(v[7]),"Logout releases active down motion with option12");
   }
   var gwFake=new Fake();using(var c=new CloudPtzController(gwFake)){
    Wait(c.RegisterAsync(7,new string('a',128),CancellationToken.None));var gwCam=Cam();gwCam.AccountProtocol=AccountPtzProtocol.GWell;
    Wait(c.MoveAsync(gwCam,PtzDirection.Left,CancellationToken.None));
    Check(gwFake.ModeQueries==1&&gwFake.Payloads[0][0]==(byte)'{'&&c.RepeatWhileHeld,"Explicit GWell account product retains JSON capability route");
   }
   var cancelFake=new Fake();using(var c=new CloudPtzController(cancelFake)){
    Wait(c.RegisterAsync(7,new string('a',128),CancellationToken.None));using(var cancel=new CancellationTokenSource()){
     cancelFake.AfterSend=()=>cancel.Cancel();bool cancelled=false;try{Wait(c.MoveAsync(Cam(),PtzDirection.Left,cancel.Token));}catch(OperationCanceledException){cancelled=true;}
     Check(cancelled&&cancelFake.Payloads.Select(b=>b[1]).SequenceEqual(new byte[]{5,6}),"Cancellation after start still sends paired release");
    }
   }
   Console.WriteLine("RESULT "+count+" PASS; reference and fake bridge tests only; no camera");return 0;
  }catch(Exception e){Console.WriteLine("FAIL "+e);return 1;}}
  sealed class Fake:ICloudNativeBridge,ICloudPtzModeBridge {
   public bool Available{get{return true;}}public string Error{get{return null;}}public int ModeQueries;
   public List<byte[]> Payloads=new List<byte[]>();public List<string> Events=new List<string>();public Action AfterSend;
   public void Register(long id,byte[] token){}public void Unregister(){Events.Add("unregister");}
   public IntPtr Open(ulong id){Events.Add("open:"+id);return new IntPtr((long)id);}
   public void State(IntPtr s,out int state,out int error,out int received){state=5;error=received=0;}
   public int GetPtzMode(IntPtr s){ModeQueries++;return 0;}
   public int Send(IntPtr s,byte[] p){Payloads.Add(p.ToArray());Events.Add("send:"+s.ToInt64()+":"+p[1]);if(AfterSend!=null)AfterSend();return 0;}
   public void Close(IntPtr s){Events.Add("close:"+s.ToInt64());}
  }
 }
}
