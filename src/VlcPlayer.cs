using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
namespace C500GDesktop {
 public enum PlayerState { Idle=0,Opening=1,Buffering=2,Playing=3,Paused=4,Stopped=5,Ended=6,Error=7 }
 public sealed class VlcEngine:IDisposable {
  private IntPtr instance; private static readonly object loaderLock=new object();private static string loadedDirectory;
  public string LibraryDirectory{get;private set;}
  internal IntPtr Handle{get{if(instance==IntPtr.Zero)throw new ObjectDisposedException("VlcEngine");return instance;}}
  public VlcEngine(){
   string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"ipTIME","ipTIME_CAM_Multi_Viewer");
   LibraryDirectory=Path.GetFullPath(root);
   if(!Environment.Is64BitProcess)throw new InvalidOperationException("64비트 버전으로 실행해 주세요.");
   if(!File.Exists(Path.Combine(root,"libvlc.dll"))||!Directory.Exists(Path.Combine(root,"plugins")))
    throw new InvalidOperationException("공식 ipTIME CAM Multi Viewer를 먼저 설치해 주세요. 영상 라이브러리를 찾을 수 없습니다.");
   lock(loaderLock){
    if(loadedDirectory==null){
     const uint Flags=0x00000100|0x00001000;
     IntPtr core=Native.LoadLibraryEx(Path.Combine(root,"libvlccore.dll"),IntPtr.Zero,Flags);
     IntPtr vlc=Native.LoadLibraryEx(Path.Combine(root,"libvlc.dll"),IntPtr.Zero,Flags);
     if(core==IntPtr.Zero||vlc==IntPtr.Zero)throw new InvalidOperationException("설치된 VLC 영상 라이브러리를 읽지 못했습니다. 공식 뷰어 설치를 확인해 주세요.");
     loadedDirectory=root;
    }
    Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH",Path.Combine(root,"plugins"),EnvironmentVariableTarget.Process);
   }
   string[] args={"--no-video-title-show","--no-osd","--no-snapshot-preview","--no-stats","--quiet","--aout=directsound","--no-volume-save","--network-caching=450","--rtsp-tcp","--no-media-library","--no-plugins-cache"};
   IntPtr[] argv=new IntPtr[args.Length];
   try{for(int i=0;i<args.Length;i++)argv[i]=Native.Utf8(args[i]);instance=Native.libvlc_new(argv.Length,argv);}
   finally{foreach(IntPtr ptr in argv)if(ptr!=IntPtr.Zero)Marshal.FreeHGlobal(ptr);}
   if(instance==IntPtr.Zero)throw new InvalidOperationException("영상 엔진 초기화에 실패했습니다.");
  }
  public VlcPlayer CreatePlayer(IntPtr window){return new VlcPlayer(this,window);}
  public void Dispose(){if(instance!=IntPtr.Zero){Native.libvlc_release(instance);instance=IntPtr.Zero;}}
 }
 public sealed class VlcPlayer:IVideoPlayer {
  private readonly VlcEngine engine;private IntPtr player;private bool audioOn;private int audioVolume=70;
  public PlayerState State{get{return player==IntPtr.Zero?PlayerState.Idle:(PlayerState)Native.libvlc_media_player_get_state(player);}}
  internal VlcPlayer(VlcEngine engine,IntPtr window){
   this.engine=engine;player=Native.libvlc_media_player_new(engine.Handle);
   if(player==IntPtr.Zero)throw new InvalidOperationException("영상 플레이어를 만들지 못했습니다.");
   Native.libvlc_media_player_set_hwnd(player,window);Native.libvlc_video_set_mouse_input(player,0);Native.libvlc_video_set_key_input(player,0);
   SetAudio(false,70);
  }
  public void SetAudio(bool enabled,int volume){
   audioOn=enabled;audioVolume=Math.Max(0,Math.Min(100,volume));if(player==IntPtr.Zero)return;
   int effective=enabled?audioVolume:0,mute=enabled&&audioVolume>0?0:1;
   if(Native.libvlc_audio_get_volume(player)!=effective)Native.libvlc_audio_set_volume(player,effective);
   if(Native.libvlc_audio_get_mute(player)!=mute)Native.libvlc_audio_set_mute(player,mute);
  }
  internal int AudioTrack{get{return Native.libvlc_audio_get_track(player);}}
  internal int AudioLevel{get{return Native.libvlc_audio_get_volume(player);}}
  internal bool AudioMuted{get{return Native.libvlc_audio_get_mute(player)==1;}}
  public void Play(CameraProfile camera){PlayLocation(camera.BuildRtspUri());}
  internal void PlayLocation(string location){
   IntPtr uri=Native.Utf8(location);IntPtr media=IntPtr.Zero;
   try{
    media=Native.libvlc_media_new_location(engine.Handle,uri);if(media==IntPtr.Zero)throw new InvalidOperationException("카메라 주소를 열지 못했습니다.");
    Native.libvlc_media_player_set_media(player,media);
    if(Native.libvlc_media_player_play(player)!=0)throw new InvalidOperationException("영상 연결을 시작하지 못했습니다.");
    SetAudio(audioOn,audioVolume);
   }finally{if(media!=IntPtr.Zero)Native.libvlc_media_release(media);Marshal.FreeHGlobal(uri);}
  }
  public void Stop(){if(player!=IntPtr.Zero)Native.libvlc_media_player_stop(player);}
  public void Dispose(){if(player!=IntPtr.Zero){Native.libvlc_media_player_stop(player);Native.libvlc_media_player_release(player);player=IntPtr.Zero;}}
 }
 internal static class Native {
  internal static IntPtr Utf8(string value){byte[] bytes=Encoding.UTF8.GetBytes(value+"\0");IntPtr result=Marshal.AllocHGlobal(bytes.Length);Marshal.Copy(bytes,0,result,bytes.Length);return result;}
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern IntPtr LoadLibraryEx(string name,IntPtr file,uint flags);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr libvlc_new(int count,IntPtr[] args);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_release(IntPtr instance);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr libvlc_media_new_location(IntPtr instance,IntPtr location);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_media_release(IntPtr media);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr libvlc_media_player_new(IntPtr instance);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_media_player_release(IntPtr player);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_media(IntPtr player,IntPtr media);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_hwnd(IntPtr player,IntPtr window);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int libvlc_media_player_play(IntPtr player);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_media_player_stop(IntPtr player);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int libvlc_media_player_get_state(IntPtr player);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int libvlc_audio_get_mute(IntPtr player);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_audio_set_mute(IntPtr player,int muted);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int libvlc_audio_get_volume(IntPtr player);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int libvlc_audio_set_volume(IntPtr player,int volume);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int libvlc_audio_get_track(IntPtr player);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_video_set_mouse_input(IntPtr player,uint enabled);
  [DllImport("libvlc.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern void libvlc_video_set_key_input(IntPtr player,uint enabled);
 }
}