using System;

using System.Security.Cryptography;

using System.Text;

using System.Threading;

using System.Threading.Tasks;

using System.Xml.Serialization;

namespace C500GDesktop {

 public enum PtzDirection { Up, Down, Left, Right, Stop }
 public enum AccountPtzProtocol { Reoqoo=0, GWell=1 }

 public interface IPtzController : IDisposable {

  bool IsReady { get; } string Status { get; }

  Task MoveAsync(CameraProfile camera, PtzDirection direction, CancellationToken token);

 }

 public sealed class CameraProfile {

  public string Id { get; set; } public string Name { get; set; }

  public string Host { get; set; } public string Username { get; set; }

  public string ProtectedPassword { get; set; } public string DeviceId { get; set; }

  public int Port { get; set; } public string StreamPath { get; set; }

  public bool PtzEnabled { get; set; }

  [XmlIgnore] public bool AccountCamera { get; set; }

  [XmlIgnore] public int DevConfig { get; set; }
  [XmlIgnore] public AccountPtzProtocol AccountProtocol { get; set; }

  public CameraProfile() {

   Id=Guid.NewGuid().ToString("N"); Name="새 카메라"; Host=""; Username="admin";

   ProtectedPassword=""; DeviceId=""; Port=554; StreamPath="/onvif1"; PtzEnabled=false;

  }

  public string GetPassword() {

   if(String.IsNullOrEmpty(ProtectedPassword)) return "";

   byte[] clear=ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword),null,DataProtectionScope.CurrentUser);

   try { return Encoding.UTF8.GetString(clear); } finally { Array.Clear(clear,0,clear.Length); }

  }

  public void SetPassword(string password) {

   byte[] clear=Encoding.UTF8.GetBytes(password??"");

   try { ProtectedPassword=Convert.ToBase64String(ProtectedData.Protect(clear,null,DataProtectionScope.CurrentUser)); }

   finally { Array.Clear(clear,0,clear.Length); }

  }

  public CameraProfile Copy() { return (CameraProfile)MemberwiseClone(); }

  public string Validate() {

   if(String.IsNullOrWhiteSpace(Name)) return "카메라 이름을 입력해 주세요.";

   if(AccountCamera){ulong device;return UInt64.TryParse(DeviceId,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out device)&&device!=0?null:"계정 카메라의 기기 ID가 올바르지 않습니다.";}

   if(String.IsNullOrWhiteSpace(Host)) return "카메라 IP 또는 호스트 이름을 입력해 주세요.";

   string host=Host.Trim().Trim('[',']');

   if(Uri.CheckHostName(host)==UriHostNameType.Unknown || host.IndexOfAny(new[]{'/','\\','@','?','#'})>=0)

    return "주소에는 IP 또는 호스트 이름만 입력해 주세요. rtsp:// 또는 경로는 제외합니다.";

   if(Port<1 || Port>65535) return "포트는 1~65535 범위여야 합니다.";

   if(String.IsNullOrEmpty(StreamPath)||!StreamPath.StartsWith("/",StringComparison.Ordinal)||StreamPath.IndexOfAny(new[]{'\r','\n','#','?'})>=0)

    return "스트림 경로가 올바르지 않습니다.";

   return null;

  }

  public string BuildRtspUri() {

   if(AccountCamera)throw new InvalidOperationException("계정 카메라는 P2P 영상으로 연결합니다.");

   string validation=Validate(); if(validation!=null) throw new ArgumentException(validation);

   string host=Host.Trim().Trim('[',']'); if(Uri.CheckHostName(host)==UriHostNameType.IPv6) host="["+host+"]";

   return "rtsp://"+Uri.EscapeDataString(Username??"")+":"+Uri.EscapeDataString(GetPassword())+"@"+host+":"+Port.ToString(System.Globalization.CultureInfo.InvariantCulture)+StreamPath;

  }

  [XmlIgnore] public string DisplayAddress { get { return AccountCamera?"ipTIME 계정 연결":(Host??"")+":"+Port.ToString(); } }

  public override string ToString(){return Name;}

 }

 public sealed class UnavailablePtzController:IPtzController {

  public bool IsReady{get{return false;}} public string Status{get{return "PTZ 분석 중 · 현재 버전에서는 제어할 수 없습니다";}}

  public Task MoveAsync(CameraProfile c,PtzDirection d,CancellationToken t){

   if(d==PtzDirection.Stop)return Task.FromResult(0); throw new InvalidOperationException(Status);

  }

  public void Dispose(){}

 }

}