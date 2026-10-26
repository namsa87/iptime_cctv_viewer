using System;
using System.IO;
using C500GDesktop;
namespace C500GDesktop.Tests {
 internal static class PublicConfigurationTests {
  static int count;
  static void Check(bool value,string name){if(!value)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
  static void Missing(Action action){try{action();throw new Exception("Missing configuration was accepted");}catch(CloudAccountException ex){Check(ex.Code=="APP_CONFIG_REQUIRED","Missing/invalid service setting reports a local configuration error");}}
  static int Main(){
   string id=Environment.GetEnvironmentVariable("C500G_APP_ID"),token=Environment.GetEnvironmentVariable("C500G_APP_TOKEN");
   try{
    Environment.SetEnvironmentVariable("C500G_APP_ID",null);Environment.SetEnvironmentVariable("C500G_APP_TOKEN",null);
    Missing(()=>{var unused=CloudServiceSettings.AppId;});Missing(()=>{var unused=CloudServiceSettings.AppToken;});
    Environment.SetEnvironmentVariable("C500G_APP_ID","fixture-id");Environment.SetEnvironmentVariable("C500G_APP_TOKEN","fixture-token");
    var body=new CloudAccountClient().BuildLoginBody("fixture@example.invalid","synthetic-only");
    Check((string)body["appId"]=="fixture-id"&&(string)body["appToken"]=="fixture-token","Request uses only the process-supplied synthetic settings");
    Environment.SetEnvironmentVariable("C500G_APP_TOKEN","changed-fixture-token");
    Check(CloudServiceSettings.AppToken=="changed-fixture-token","Service setting has no embedded or cached credential fallback");
    Environment.SetEnvironmentVariable("C500G_APP_TOKEN","bad\r\nvalue");Missing(()=>{var unused=CloudServiceSettings.AppToken;});
    string missing=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"empty-"+Guid.NewGuid().ToString("N"),"cameras.xml"),warning;
    var profiles=new CameraStore(missing).Load(out warning);
    Check(profiles.Count==0&&warning==null&&!File.Exists(missing),"New configuration has no bundled camera and does not seed a file");
    Console.WriteLine("RESULT "+count+" PASS; no network requests; synthetic process settings only");return 0;
   }catch(Exception ex){Console.WriteLine("FAIL "+ex.GetType().Name+": "+ex.Message);return 1;}
   finally{Environment.SetEnvironmentVariable("C500G_APP_ID",id);Environment.SetEnvironmentVariable("C500G_APP_TOKEN",token);}
  }
 }
}
