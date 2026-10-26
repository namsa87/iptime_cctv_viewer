using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using C500GDesktop;
namespace C500GDesktop.Tests {
 internal sealed class CloudHoldFake:IPtzController,IHoldPtzController {
  internal readonly List<PtzDirection> Calls=new List<PtzDirection>();internal bool Disposed;internal int Delay;internal bool Repeat;internal readonly List<long> Times=new List<long>();internal readonly System.Diagnostics.Stopwatch Clock=System.Diagnostics.Stopwatch.StartNew();
  public bool RepeatWhileHeld{get{return Repeat;}}public int RepeatIntervalMilliseconds{get{return 200;}}public bool IsReady{get{return true;}}public string Status{get{return "Offline hold test";}}
  public async Task MoveAsync(CameraProfile c,PtzDirection d,CancellationToken token){Calls.Add(d);Times.Add(Clock.ElapsedMilliseconds);await Task.Delay(Delay,token);}
  public void Dispose(){Disposed=true;}
 }
 internal static class AccountHoldTests {
  static int failures;static readonly List<string> report=new List<string>();
  static object Get(object obj,string name){return obj.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(obj);}
  static void Raise(Control c,string method,EventArgs arg){typeof(Control).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(c,new object[]{arg});}
  static void Check(bool ok,string text){report.Add((ok?"PASS ":"FAIL ")+text);Console.WriteLine(report.Last());if(!ok)failures++;}
  [STAThread]static int Main(){
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   string root=AppDomain.CurrentDomain.BaseDirectory;var store=new CameraStore(Path.Combine(root,"AccountHoldTests.profile.xml"));store.Save(new List<CameraProfile>{new CameraProfile{Name="Offline test",Host="127.0.0.1",PtzEnabled=true}});
   var fake=new CloudHoldFake();
   using(var form=new ViewerForm(fake,store))using(var context=new ApplicationContext())using(var begin=new System.Windows.Forms.Timer()){
    typeof(ViewerForm).GetField("initializedEngine",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(form,true);
    IntPtr handle=form.Handle;SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());form.FormClosed+=delegate{context.ExitThread();};begin.Interval=20;
    begin.Tick+=async delegate{begin.Stop();try{
     Button down=((List<Button>)Get(form,"directionButtons")).First(b=>(PtzDirection)b.Tag==PtzDirection.Down);
     Raise(down,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,5,5,0));await Task.Delay(5200);
     Check(fake.Calls.SequenceEqual(new[]{PtzDirection.Down}),"Account PTZ hold sends one start and no timed STOP for 5.2 seconds");
     Raise(down,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,5,5,0));await (Task)Get(form,"stoppingTask");await Task.Delay(250);
     Check(fake.Calls.SequenceEqual(new[]{PtzDirection.Down,PtzDirection.Stop}),"Release sends exactly one STOP and no more start commands");
     fake.Calls.Clear();fake.Delay=400;
     Raise(down,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,5,5,0));await Task.Delay(50);Raise(down,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,5,5,0));await (Task)Get(form,"stoppingTask");
     Check(fake.Calls.SequenceEqual(new[]{PtzDirection.Down,PtzDirection.Stop}),"Releasing during the start request cancels it and sends STOP");
     fake.Delay=0;fake.Calls.Clear();Raise(down,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,5,5,0));await Task.Delay(80);
     typeof(Form).GetMethod("OnDeactivate",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,new object[]{EventArgs.Empty});await (Task)Get(form,"stoppingTask");
     Check(fake.Calls.SequenceEqual(new[]{PtzDirection.Down,PtzDirection.Stop}),"Leaving the window stops account PTZ");
     fake.Repeat=true;fake.Calls.Clear();fake.Times.Clear();fake.Clock.Restart();
     Raise(down,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,5,5,0));await Task.Delay(5200);
     Check(fake.Calls.Count>=23&&fake.Calls.Count<=28&&fake.Calls.All(d=>d==PtzDirection.Down),"Legacy hold repeats for over5 seconds at200ms with no automatic STOP");
     Check(fake.Times.Zip(fake.Times.Skip(1),(a,b)=>b-a).All(delta=>delta>=150),"Legacy repeats do not form an80ms command flood");
     Raise(down,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,5,5,0));await (Task)Get(form,"stoppingTask");int stoppedCount=fake.Calls.Count;await Task.Delay(450);
     Check(fake.Calls.Count==stoppedCount&&fake.Calls.Last()==PtzDirection.Stop,"Legacy release cancels pending repeats without a late movement call");
    }catch(Exception ex){Check(false,"Harness: "+ex.GetType().Name);}finally{form.Close();}};begin.Start();Application.Run(context);
   }
   Check(fake.Disposed,"Closing disposes the controller");report.Add("RESULT "+(failures==0?"PASS":"FAIL")+"; real_camera_connections=0");File.WriteAllLines(Path.Combine(root,"AccountHoldTests.results.txt"),report);return failures==0?0:1;
  }
 }
}
