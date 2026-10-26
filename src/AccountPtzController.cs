using System;
using System.Globalization;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace C500GDesktop {
 public interface IAccountPtzController:IPtzController {
  bool IsLoggedIn{get;}
  Task LoginAsync(string username,string password,CancellationToken token);
  Task LogoutAsync(CancellationToken token);
 }
 public sealed class AccountPtzController:IAccountPtzController,IHoldPtzController,IAccountVideoController {
  private readonly RtspPtzController rtsp=new RtspPtzController();
  private CloudPtzController cloud=new CloudPtzController();
  private CloudAccountSession session;private bool disposed;
  public bool IsLoggedIn{get{return session!=null;}}
  public IReadOnlyList<CloudDeviceInfo> AccountDevices{get{return session==null?new List<CloudDeviceInfo>().AsReadOnly():session.Devices;}}
  public string DeviceListErrorCode{get{return session==null?null:session.DeviceListErrorCode;}}
  public IVideoPlayer CreateVideoPlayer(CameraProfile camera,IntPtr window){if(!IsLoggedIn)throw new InvalidOperationException("먼저 계정으로 로그인해 주세요.");return new P2pVideoPlayer(cloud,camera,window);}

  public bool RepeatWhileHeld{get{return !IsLoggedIn||cloud.RepeatWhileHeld;}}
  public int RepeatIntervalMilliseconds{get{return IsLoggedIn?cloud.RepeatIntervalMilliseconds:80;}}
  public bool IsReady{get{return !disposed&&(IsLoggedIn?cloud.IsReady:rtsp.IsReady);}}
  public string Status{get{return IsLoggedIn?cloud.Status:"계정 로그인 전 · 기존 RTSP PTZ";}}
  public async Task LoginAsync(string username,string password,CancellationToken token){
   if(disposed)throw new ObjectDisposedException("AccountPtzController");
   if(!cloud.NativeAvailable){cloud.Dispose();cloud=new CloudPtzController();}
   if(!cloud.NativeAvailable)throw new CloudAccountException("SDK_UNAVAILABLE","계정 PTZ 모듈을 불러오지 못했습니다. 앱의 제어 모듈 설치 경로를 확인해 주세요.");
   CloudAccountSession candidate=await new CloudAccountClient().LoginAsync(username,password,token).ConfigureAwait(false);
   bool accepted=false;
   try{
    long accessId;if(!Int64.TryParse(candidate.AccessId,NumberStyles.Integer,CultureInfo.InvariantCulture,out accessId))throw new CloudAccountException("ACCESS_ID_INVALID","로그인 응답에서 PTZ 계정 정보를 확인하지 못했습니다.");
    if(!cloud.SupportsExtendedTokens)RequireLegacyToken(candidate);
    await cloud.RegisterAsync(accessId,candidate.AccessToken,token).ConfigureAwait(false);
    if(session!=null)session.Dispose();session=candidate;accepted=true;
   }finally{if(!accepted)candidate.Dispose();}
  }
  internal static void RequireLegacyToken(CloudAccountSession candidate){
   // The pinned Windows SDK copies into a fixed 128-character token field.
   // Keep all bytes for HTTP use; never truncate or pass extensions to this DLL.
   if(candidate==null)throw new ArgumentNullException("candidate");
   if(candidate.AccessToken==null||candidate.AccessToken.Length!=128)
    throw new CloudAccountException("SDK_TOKEN_UNSUPPORTED","계정 인증은 완료됐지만 Windows PTZ 모듈이 이 토큰 형식을 지원하지 않습니다. 아래 진단 정보로 호환 모듈을 확인해야 합니다.",candidate.TokenFormatInfo);
  }
  public Task PrepareCameraAsync(CameraProfile camera,CancellationToken token){if(!IsLoggedIn)throw new InvalidOperationException("먼저 계정으로 로그인해 주세요.");return cloud.PrepareAsync(camera,token);}
  public Task MoveAsync(CameraProfile camera,PtzDirection direction,CancellationToken token){
   if(disposed)throw new ObjectDisposedException("AccountPtzController");
   return IsLoggedIn?cloud.MoveAsync(camera,direction,token):rtsp.MoveAsync(camera,direction,token);
  }
  public async Task LogoutAsync(CancellationToken token){
   try{await cloud.LogoutAsync(token).ConfigureAwait(false);}
   finally{if(!cloud.IsReady&&session!=null){session.Dispose();session=null;}}
  }
  public void Dispose(){if(disposed)return;disposed=true;cloud.Dispose();rtsp.Dispose();if(session!=null){session.Dispose();session=null;}}
 }
}
