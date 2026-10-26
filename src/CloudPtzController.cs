using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace C500GDesktop
{
 public interface IHoldPtzController { bool RepeatWhileHeld { get; } int RepeatIntervalMilliseconds { get; } }
 internal interface ICloudTokenCapabilities { bool SupportsExtendedTokens {get;} }
 internal interface ICloudNativeBridge {
  bool Available {get;} string Error {get;}
  void Register(long accessId,byte[] token);void Unregister();
  IntPtr Open(ulong deviceId);void State(IntPtr session,out int state,out int error,out int received);
  int Send(IntPtr session,byte[] payload);void Close(IntPtr session);
 }
 public sealed partial class CloudPtzController:IPtzController,IHoldPtzController
 {
  private readonly ICloudNativeBridge bridge;
  private readonly bool touchCommandsSupported;private bool selectedTouchCommands,selectedIptimeCommands;
  private readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
  private volatile bool registered,disposed;
  private string status="계정 로그인이 필요합니다.";
  private IntPtr session;private ulong sessionDevice;
  private PtzDirection? lastDirection;private bool pressed;
  public CloudPtzController():this(CloudNativeBridge.CreateConfigured()){}
  public CloudPtzController(string bridgePath,string sdkPath):this(new CloudNativeBridge(bridgePath,sdkPath)){}
  // Match the original app: without confirmed ptzInfo bit0x800, use legacy type2.
  internal CloudPtzController(ICloudNativeBridge native):this(native,false){}
  internal CloudPtzController(ICloudNativeBridge native,bool supportsTouchCommands){bridge=native;touchCommandsSupported=supportsTouchCommands;selectedTouchCommands=supportsTouchCommands;if(!native.Available)status=native.Error;}
  public bool RepeatWhileHeld{get{return !selectedTouchCommands;}}
  public int RepeatIntervalMilliseconds{get{return 200;}}
  public bool NativeAvailable{get{return bridge.Available;}}
  public string StartupError{get{return bridge.Error;}}
  public bool SupportsExtendedTokens{get{var capabilities=bridge as ICloudTokenCapabilities;return capabilities!=null&&capabilities.SupportsExtendedTokens;}}
  // Register is a void vendor call. Ready means credentials were submitted, not that a camera connected.
  public bool IsReady{get{return !disposed&&registered&&bridge.Available;}}
  public string Status{get{return status;}}
  public async Task RegisterAsync(long accessId,string accessToken,CancellationToken token)
  {
   ThrowIfDisposed();if(!NativeAvailable)throw new InvalidOperationException(StartupError);
   if(String.IsNullOrEmpty(accessToken))throw new ArgumentException("계정 접속 토큰이 비어 있습니다.");
   byte[] bytes=Encoding.UTF8.GetBytes(accessToken);
   bool validToken=SupportsExtendedTokens?CloudAccountClient.IsSupportedTokenStructure(accessToken):bytes.Length==128;
   if(validToken)foreach(byte b in bytes)if(!((b>=(byte)'0'&&b<=(byte)'9')||(b>=(byte)'a'&&b<=(byte)'f')||(b>=(byte)'A'&&b<=(byte)'F'))){validToken=false;break;}
   if(!validToken){Array.Clear(bytes,0,bytes.Length);throw new NotSupportedException(SupportsExtendedTokens?"계정 토큰 구조를 확인하지 못했습니다.":"현재 Windows SDK에는 정확히 128자리의 16진수 계정 토큰이 필요합니다. 토큰을 변형하거나 잘라 전송하지 않았습니다.");}
   try{
    await gate.WaitAsync(token).ConfigureAwait(false);
    try{
     ThrowIfDisposed();token.ThrowIfCancellationRequested();
     if(registered)await Task.Run((Action)LogoutCore).ConfigureAwait(false);
     await Task.Run(()=>bridge.Register(accessId,bytes),token).ConfigureAwait(false);
     registered=true;status="계정 SDK 등록됨 · 카메라 P2P 연결은 아직 확인되지 않았습니다.";
     if(token.IsCancellationRequested){await Task.Run((Action)LogoutCore).ConfigureAwait(false);token.ThrowIfCancellationRequested();}
    }finally{gate.Release();}
   }finally{Array.Clear(bytes,0,bytes.Length);}
  }
  public async Task LogoutAsync(CancellationToken token)
  {
   await gate.WaitAsync(token).ConfigureAwait(false);
   try{await Task.Run((Action)LogoutCore).ConfigureAwait(false);}
   finally{gate.Release();}
  }
  public async Task PrepareAsync(CameraProfile camera,CancellationToken token)
  {
   ThrowIfDisposed();if(camera==null)throw new ArgumentNullException("camera");
   await gate.WaitAsync(token).ConfigureAwait(false);
   try{
    ThrowIfDisposed();token.ThrowIfCancellationRequested();
    if(!IsReady)throw new InvalidOperationException("먼저 ipTIME CCTV 계정으로 로그인해 주세요.");
    ulong deviceId=ParseDeviceId(camera);
    await EnsureSessionAsync(deviceId,camera.AccountCamera&&camera.AccountProtocol==AccountPtzProtocol.Reoqoo,token).ConfigureAwait(false);
    status="카메라 P2P 연결 확인됨 · 방향 버튼을 사용할 수 있습니다.";
   }finally{gate.Release();}
  }
  private static ulong ParseDeviceId(CameraProfile camera)
  {
   ulong value;
   if(!UInt64.TryParse(camera.DeviceId,NumberStyles.None,CultureInfo.InvariantCulture,out value)||value==0)
    throw new ArgumentException("계정에 등록된 카메라의 숫자 장치 ID가 필요합니다.");
   return value;
  }
  public async Task MoveAsync(CameraProfile camera,PtzDirection direction,CancellationToken token)
  {
   ThrowIfDisposed();if(camera==null)throw new ArgumentNullException("camera");
   if(direction!=PtzDirection.Stop&&!camera.PtzEnabled)throw new InvalidOperationException("이 카메라에서 PTZ 사용을 켜 주세요.");
   await gate.WaitAsync(token).ConfigureAwait(false);
   try{
    ThrowIfDisposed();
    if(direction==PtzDirection.Stop){
     if(session!=IntPtr.Zero&&lastDirection.HasValue)await Task.Run((Action)ReleaseCore).ConfigureAwait(false);
     return;
    }
    if(!IsReady)throw new InvalidOperationException("먼저 ipTIME CCTV 계정으로 로그인해 주세요.");
    ulong deviceId=ParseDeviceId(camera);
    await EnsureSessionAsync(deviceId,camera.AccountCamera&&camera.AccountProtocol==AccountPtzProtocol.Reoqoo,token).ConfigureAwait(false);
    if(selectedTouchCommands&&pressed&&lastDirection==direction)return;
    if(pressed&&lastDirection!=direction)await Task.Run((Action)ReleaseCore).ConfigureAwait(false);
    lastDirection=direction;pressed=true;
    Exception failure=null;
    try{
     byte[] payload=selectedIptimeCommands?BuildIptimePayload(direction,false):BuildPayload(direction,0,selectedTouchCommands);
     int code=await Task.Run(()=>bridge.Send(session,payload),token).ConfigureAwait(false);
     if(code<0)throw new InvalidOperationException("P2P 이동 전송 실패 (SDK "+code.ToString(CultureInfo.InvariantCulture)+")");
     token.ThrowIfCancellationRequested();status=selectedTouchCommands?"P2P 이동 요청 전송 · 실제 기기 동작 확인이 필요합니다.":"P2P 이동 명령 전송 중 · 버튼을 놓으면 반복 전송을 멈춥니다.";
    }catch(Exception ex){failure=ex;}
    if(failure!=null){
     try{await Task.Run((Action)ReleaseCore).ConfigureAwait(false);}catch{status="P2P 정지 요청 실패 · 정지 버튼으로 다시 시도해 주세요.";}
     System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
   }finally{gate.Release();}
  }
  private async Task EnsureSessionAsync(ulong deviceId,bool iptimeAccountCamera,CancellationToken token)
  {
   if(session!=IntPtr.Zero&&sessionDevice!=deviceId)await Task.Run((Action)CloseCore).ConfigureAwait(false);
   string diagnostic="카메라 P2P 연결 초기화에 실패했습니다.";
   Exception failure=null;
   try{
    if(session==IntPtr.Zero){
     status="카메라 P2P 연결 중…";
     IntPtr existing;
     if(videoSessions.TryGetValue(deviceId,out existing)){session=existing;sessionDevice=deviceId;}
     else {session=await Task.Run(()=>bridge.Open(deviceId),token).ConfigureAwait(false);sessionDevice=deviceId;
      ICloudVideoBridge video=bridge as ICloudVideoBridge;if(video!=null)await Task.Run(()=>video.Configure(session,0),token).ConfigureAwait(false);
     }
    }
    Stopwatch watch=Stopwatch.StartNew();
    diagnostic="카메라 P2P 상태 조회에 실패했습니다.";
    while(true){
     token.ThrowIfCancellationRequested();int state=0,error=0,received=0;
     await Task.Run(()=>bridge.State(session,out state,out error,out received),token).ConfigureAwait(false);
     if(error!=0){
      diagnostic="카메라 P2P 연결 오류 (SDK "+error.ToString(CultureInfo.InvariantCulture)+")";
      throw new InvalidOperationException(diagnostic);
     }
     if(state==5){
      if(!pressed){
       // Account profiles come from the REOQOO account route, whose APK plugin is PT/Xiaotun.
       // That route uses paired 24-byte commands and does not depend on GWell devInfo bits.
       selectedIptimeCommands=iptimeAccountCamera;
       var modes=bridge as ICloudPtzModeBridge;int mode=iptimeAccountCamera||modes==null?0:await Task.Run(()=>modes.GetPtzMode(session),token).ConfigureAwait(false);
       selectedTouchCommands=iptimeAccountCamera||touchCommandsSupported||mode==2;
      }
      return;
     } // Bridge connected; choose the observed camera capability before a gesture.
     if(state==7){diagnostic="카메라 P2P 연결이 종료되었습니다. (상태 7)";throw new InvalidOperationException(diagnostic);}
     if(watch.ElapsedMilliseconds>=15000){
      diagnostic="카메라 P2P 연결 시간 초과 (15초) · 계정·장치 ID와 SDK 호환성을 확인해 주세요.";
      throw new TimeoutException(diagnostic);
     }
     await Task.Delay(80,token).ConfigureAwait(false);
    }
   }catch(OperationCanceledException ex){failure=ex;diagnostic="카메라 P2P 연결이 취소되었습니다.";}
   catch(Exception ex){failure=ex;}
   if(failure!=null){
    // Keep only internally generated diagnostics: vendor payloads and arbitrary exception text never enter Status.
    bool cleanupFailed=false;try{await Task.Run((Action)CloseCore).ConfigureAwait(false);}catch{cleanupFailed=true;}
    status=diagnostic+(cleanupFailed?" · 연결 정리를 확인하지 못했습니다.":"");
    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
   }
  }
  internal static byte[] BuildIptimePayload(PtzDirection direction,bool release)
  {
   int option=direction==PtzDirection.Left?5:direction==PtzDirection.Right?7:direction==PtzDirection.Up?9:direction==PtzDirection.Down?11:-1;
   if(option<0)throw new ArgumentException("Release requires the active movement direction.");
   byte[] payload=new byte[24];payload[1]=(byte)(option+(release?1:0));return payload;
  }
  internal static byte[] BuildPayload(PtzDirection direction,int touchType){return BuildPayload(direction,touchType,true);}
  internal static byte[] BuildPayload(PtzDirection direction,int touchType,bool touchCommandsSupported)
  {
   if(touchType!=0&&touchType!=1)throw new ArgumentOutOfRangeException("touchType");
   int dir=direction==PtzDirection.Left?0:direction==PtzDirection.Right?1:direction==PtzDirection.Up?2:direction==PtzDirection.Down?3:-1;
   if(dir<0)throw new ArgumentException("Stop requires the last movement direction.");
   // The APK defaults to type2 without confirmed new-PTZ capability. Legacy release
   // cancels the repeat timer and must not transmit an extra movement payload.
   if(!touchCommandsSupported&&touchType!=0)throw new InvalidOperationException("Legacy release cancels repetition without a payload.");
   int commandType=touchCommandsSupported?22:2;
   // Preserve the exact byte order emitted by the original APK Gson serializer.
   return Encoding.UTF8.GetBytes("{\"data\":{\"dir\":"+dir+",\"touchType\":"+touchType+"},\"type\":"+commandType+"}");
  }
  private void ReleaseCore()
  {
   if(session==IntPtr.Zero||!lastDirection.HasValue)return;
   if(!selectedTouchCommands){pressed=false;lastDirection=null;status="P2P 반복 전송 중지 · 실제 카메라 정지를 확인해 주세요.";return;}
   int code=bridge.Send(session,selectedIptimeCommands?BuildIptimePayload(lastDirection.Value,true):BuildPayload(PtzDirection.Left,1));
   if(code<0)throw new InvalidOperationException("P2P 정지 전송 실패 (SDK "+code.ToString(CultureInfo.InvariantCulture)+")");
   pressed=false;status="P2P 정지 요청 전송 · 기기 수신 확인과는 구분됩니다.";
  }
  private void CloseCore()
  {
   Exception error=null;
   try{if(pressed)ReleaseCore();}catch(Exception ex){error=ex;}
   if(session!=IntPtr.Zero){IntPtr old=session;session=IntPtr.Zero;sessionDevice=0;lastDirection=null;pressed=false;if(!ContainsVideoHandle(old))bridge.Close(old);}
   if(error!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
  }
  private void LogoutCore()
  {
   Exception error=null;try{CloseCore();}catch(Exception ex){error=ex;}
   try{CloseVideosCore();}catch(Exception ex){if(error==null)error=ex;}
   try{if(registered)bridge.Unregister();}finally{registered=false;}
   status="로그아웃됨 · 계정 접속 정보를 메모리에서 해제했습니다.";
   if(error!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
  }
  private void ThrowIfDisposed(){if(disposed)throw new ObjectDisposedException("CloudPtzController");}
  public void Dispose()
  {
   if(disposed)return;disposed=true;gate.Wait();
   try{try{LogoutCore();}catch{status="연결 종료 중 정지 응답을 확인하지 못했습니다.";}}
   finally{gate.Release();var lifetime=bridge as IDisposable;if(lifetime!=null)lifetime.Dispose();}
  }
 }
 internal sealed class CloudNativeBridge:ICloudNativeBridge,ICloudTokenCapabilities,ICloudVideoBridge,ICloudPtzModeBridge
 {
  private const uint LoadFlags=0x100|0x1000;
  private readonly IntPtr module;
  public bool SupportsExtendedTokens{get;private set;}
  public bool Available{get;private set;}public string Error{get;private set;}
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate uint VersionFn();
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int RegisterFn(long accessId,[In]byte[] token,int size);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int UnregisterFn();
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate IntPtr OpenFn(ulong deviceId,out int result);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int StateFn(IntPtr session,out int state,out int error,out int received);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int SendFn(IntPtr session,[In]byte[] bytes,int size);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int CloseFn(IntPtr session);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int VideoStartFn(IntPtr handle,IntPtr window);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int VideoStateFn(IntPtr handle,out int state,out int frames,out int error);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int VideoAudioFn(IntPtr handle,int enabled,int volume);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int ConfigureFn(IntPtr handle,int devConfig);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int PtzModeFn(IntPtr handle,out int mode);
  private PtzModeFn ptzMode;
  public int GetPtzMode(IntPtr handle){if(ptzMode==null)return 0;int mode;Check(ptzMode(handle,out mode));if(mode<0||mode>2)throw new InvalidOperationException("PTZ 능력 응답이 올바르지 않습니다.");return mode;}
  private VideoStartFn videoStart;private CloseFn videoStop;private VideoStateFn videoState;private VideoAudioFn videoAudio;private ConfigureFn configure;
  public void VideoStart(IntPtr handle,IntPtr window){RequireVideo();Check(videoStart(handle,window));}
  public void VideoStop(IntPtr handle){RequireVideo();Check(videoStop(handle));}
  public void VideoState(IntPtr handle,out int state,out int frames,out int error){RequireVideo();Check(videoState(handle,out state,out frames,out error));}
  public void VideoAudio(IntPtr handle,bool enabled,int volume){RequireVideo();Check(videoAudio(handle,enabled?1:0,Math.Max(0,Math.Min(100,volume))));}
  public void Configure(IntPtr handle,int devConfig){if(configure!=null)Check(configure(handle,devConfig));}
  private void RequireVideo(){if(videoStart==null)throw new InvalidOperationException("계정 영상 모듈이 준비되지 않았습니다.");}
  private RegisterFn register;private UnregisterFn unregister;private OpenFn open;private StateFn state;private SendFn send;private CloseFn close;
  internal static ICloudNativeBridge CreateConfigured(){
   string root=AppDomain.CurrentDomain.BaseDirectory;
   string pointer=Path.Combine(root,"modern-sdk-path.txt");
   if(File.Exists(pointer)){
    string modern=Path.Combine(root,"C500G.ModernIoTBridge.dll");
    string sdk=File.ReadAllText(pointer).Trim();
    if(Directory.Exists(sdk))sdk=Path.Combine(sdk,"IOT_LIB.dll");
    return new PtzWorkerProcess(System.Reflection.Assembly.GetEntryAssembly().Location,modern,sdk);
   }
   return new CloudNativeBridge(null,null);
  }
  public CloudNativeBridge(string bridgePath,string sdkPath)
  {
   try{
    if(!Environment.Is64BitProcess)throw new InvalidOperationException("계정 PTZ에는 x64 실행 파일이 필요합니다.");
    string baseDir=AppDomain.CurrentDomain.BaseDirectory;
    if(String.IsNullOrWhiteSpace(bridgePath))bridgePath=FindFile(baseDir,"C500G.IoTBridge.dll",Path.Combine("native","bin","C500G.IoTBridge.dll"));
    if(String.IsNullOrWhiteSpace(sdkPath)){
     string pointer=Path.Combine(baseDir,"sdk-path.txt");
     if(File.Exists(pointer)){string path=File.ReadAllText(pointer).Trim();if(Path.IsPathRooted(path))sdkPath=Directory.Exists(path)?Path.Combine(path,"IotVideoSDK.dll"):path;}
     if(String.IsNullOrWhiteSpace(sdkPath))sdkPath=FindFile(baseDir,Path.Combine("vendor","IoTVideo-PC","Bin","IotVideoSDK.dll"),null);
    }
    if(Directory.Exists(sdkPath))sdkPath=Path.Combine(sdkPath,"IotVideoSDK.dll");
    if(String.IsNullOrWhiteSpace(bridgePath)||!File.Exists(bridgePath))throw new InvalidOperationException("계정 PTZ 브리지 파일을 찾지 못했습니다.");
    if(String.IsNullOrWhiteSpace(sdkPath)||!File.Exists(sdkPath))throw new InvalidOperationException("로컬 IoTVideo SDK를 찾지 못했습니다. sdk-path.txt를 확인해 주세요.");
    // Different FFmpeg builds must never be loaded into the video viewer process.
    bool modern=String.Equals(Path.GetFileName(sdkPath),"IOT_LIB.dll",StringComparison.OrdinalIgnoreCase);
    if(modern){
     if(!Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--ptz-worker"))throw new InvalidOperationException("신형 PTZ 모듈은 전용 제어 프로세스에서만 사용할 수 있습니다.");
     VerifyModernFiles(Path.GetDirectoryName(Path.GetFullPath(sdkPath)));
    }else VerifyHash(sdkPath,"818fd0b2027dc63e12429cd51d0057e3f25fcccb2bd30ceecef506556108fb95");
    // Keep modules loaded for the process lifetime: the vendor owns background callbacks.
    if(LoadLibraryEx(Path.GetFullPath(sdkPath),IntPtr.Zero,LoadFlags)==IntPtr.Zero)throw new InvalidOperationException("IoTVideo SDK 로드 실패 · x64 Visual C++ 런타임을 확인해 주세요.");
    module=LoadLibraryEx(Path.GetFullPath(bridgePath),IntPtr.Zero,LoadFlags);
    if(module==IntPtr.Zero)throw new InvalidOperationException("계정 PTZ 브리지를 로드하지 못했습니다.");
    VersionFn version=Get<VersionFn>("c500g_bridge_version");if(version()!=(modern?0x20000u:0x10000u))throw new InvalidOperationException("계정 PTZ 브리지 버전이 맞지 않습니다.");
    register=Get<RegisterFn>("c500g_register");unregister=Get<UnregisterFn>("c500g_unregister");open=Get<OpenFn>("c500g_open");
    state=Get<StateFn>("c500g_state");send=Get<SendFn>("c500g_send");close=Get<CloseFn>("c500g_close");
    if(modern){videoStart=Get<VideoStartFn>("c500g_video_start");videoStop=Get<CloseFn>("c500g_video_stop");videoState=Get<VideoStateFn>("c500g_video_state");videoAudio=Get<VideoAudioFn>("c500g_video_audio");configure=Get<ConfigureFn>("c500g_configure");ptzMode=Get<PtzModeFn>("c500g_ptz_capability");}
    SupportsExtendedTokens=modern;Available=true;Error=null;
   }catch(Exception ex){Available=false;Error=ex is InvalidOperationException?ex.Message:"계정 PTZ 모듈 초기화에 실패했습니다.";}
  }
  private static void VerifyHash(string path,string expected){
   if(!File.Exists(path))throw new InvalidOperationException("PTZ 모듈 구성 파일이 없습니다.");
   using(SHA256 sha=SHA256.Create())using(FileStream file=File.OpenRead(path)){
    string digest=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();
    if(digest!=expected)throw new InvalidOperationException("검증된 버전과 다른 PTZ 모듈 파일입니다.");
   }
  }
  private static void VerifyModernFiles(string folder){
   VerifyHash(Path.Combine(folder,"avcodec-58.dll"),"915db67e218ac7bfb1a372e0956bfa6067d20b6cd9523288f87811d07afad20d");
   VerifyHash(Path.Combine(folder,"avfilter-7.dll"),"843b2733e05aa26c7a04c89befb5184f4779d7fbfda806cce8eb0d739566183b");
   VerifyHash(Path.Combine(folder,"avformat-58.dll"),"4a3f4efeab062b8465ccbe16c9a7ed33ecba62b9015f64db876c27837ec4eb88");
   VerifyHash(Path.Combine(folder,"avutil-56.dll"),"de3d076abd6cae02121f23dabcda99854b4e3dfc72f22e3d11d3be511c2a7ca3");
   VerifyHash(Path.Combine(folder,"D3Dcompiler_47.dll"),"e994847e01a6f1e4cbdc5a864616ac262f67ee4f14db194984661a8d927ab7f4");
   VerifyHash(Path.Combine(folder,"glew32.dll"),"498ee06b39c2ecb18232a192002c7eeafb04f84a9dfc429b1e837f11b49cfbb0");
   VerifyHash(Path.Combine(folder,"GWPlayerLibV1.dll"),"622e31d0428182d53edbabeb53aab7e095ac944053a1b90ff9d379cc8b75c2d2");
   VerifyHash(Path.Combine(folder,"IOT_LIB.dll"),"ec07cb4b98bc486e8bdb058c8695d16f0fb2c524be50dd8c9d0f0cfa87fa0990");
   VerifyHash(Path.Combine(folder,"libcurl.dll"),"58a6ff4992bcf9ffabda91ce2d8d03503cb7f8de464e1c9b1be1082b6d0ebe95");
   VerifyHash(Path.Combine(folder,"libxml2.dll"),"7defb30ef09ec069049b0c6e37b10acf92797de031ee005b63ce4272123c4a28");
   VerifyHash(Path.Combine(folder,"msvcp140.dll"),"b99eb28a471311113f5c4109cb3c463f39cfd9bdb3b07f706204dedddb4516a1");
   VerifyHash(Path.Combine(folder,"msvcp140_1.dll"),"e10fb192620193cb721516c30533f71ca6b2a4396b48f3858b571143e94aba31");
   VerifyHash(Path.Combine(folder,"msvcr100.dll"),"2b7ab898b2f64f4cd1ae651c93d7eb9556208fd8d8d40d425e856c83d86d2346");
   VerifyHash(Path.Combine(folder,"msvcrt.dll"),"a7f4196d9476d36894ce17c3b0dd97504ca2cc31b3e0a774fee326e867565885");
   VerifyHash(Path.Combine(folder,"postproc-55.dll"),"602608f1df882e67f536e48cb6c0b78816bcd1c38fbcab9076173e61f4270cf2");
   VerifyHash(Path.Combine(folder,"Qt5Core.dll"),"8d2ff4ce9096ddccc4f4cd62c2e41fc854cfd1b0d6e8d296645a7f5fd4ae565a");
   VerifyHash(Path.Combine(folder,"Qt5Gui.dll"),"5e7d2d41b8b92a880e83b8cc0ca173f5da61218604186196787ee1600956be1e");
   VerifyHash(Path.Combine(folder,"Qt5Multimedia.dll"),"1a59ae2a9ff768ad6bfb888fe3dd2544e238f0b28da83cf375ebd803ce713dc4");
   VerifyHash(Path.Combine(folder,"Qt5Network.dll"),"4ef341ae9302e793878020f0740b09b0f31cb380408a697f75c69fdbd20fc7a1");
   VerifyHash(Path.Combine(folder,"swresample-3.dll"),"bea5da195e1ce239cb1b050c750fbbfcac2d2f997432e9592e6bbe23513c8831");
   VerifyHash(Path.Combine(folder,"swscale-5.dll"),"9b3543e468d148a66c661205da64b28f2f92ffda3012d0282b753f52a7e0d6c8");
   VerifyHash(Path.Combine(folder,"vcruntime140.dll"),"8ee6b49830436ff3bec9ba89213395427b5535813930489f118721fd3d2d942c");
   VerifyHash(Path.Combine(folder,"vcruntime140_1.dll"),"f8bc270449ca6bb6345e88be3632d465c0a7595197c7954357dc5066ed50ae44");
  }
  private static string FindFile(string start,string relative,string alternative){
   DirectoryInfo dir=new DirectoryInfo(start);
   for(int depth=0;dir!=null&&depth<7;depth++,dir=dir.Parent){
    string first=Path.Combine(dir.FullName,relative);if(File.Exists(first))return first;
    if(alternative!=null){string second=Path.Combine(dir.FullName,alternative);if(File.Exists(second))return second;}
   }return null;
  }
  private T Get<T>(string name)where T:class{IntPtr ptr=GetProcAddress(module,name);if(ptr==IntPtr.Zero)throw new InvalidOperationException("계정 PTZ 브리지 API 누락: "+name);return Marshal.GetDelegateForFunctionPointer(ptr,typeof(T))as T;}
  private void Check(int code,string operation="영상 모듈 요청 실패"){if(code<0)throw new NativePtzException(code);}
  public void Register(long id,byte[] token){Check(register(id,token,token.Length),"계정 SDK 등록 실패");}
  public void Unregister(){Check(unregister(),"계정 SDK 해제 실패");}
  public IntPtr Open(ulong id){int result;IntPtr handle=open(id,out result);Check(result,"P2P 세션 생성 실패");if(handle==IntPtr.Zero)throw new InvalidOperationException("P2P 세션이 비어 있습니다.");return handle;}
  public void State(IntPtr handle,out int s,out int e,out int r){Check(state(handle,out s,out e,out r),"P2P 상태 조회 실패");}
  public int Send(IntPtr handle,byte[] payload){return send(handle,payload,payload.Length);}
  public void Close(IntPtr handle){Check(close(handle),"P2P 세션 종료 실패");}
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr LoadLibraryEx(string path,IntPtr reserved,uint flags);
  [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)]private static extern IntPtr GetProcAddress(IntPtr module,string name);
 }
}
