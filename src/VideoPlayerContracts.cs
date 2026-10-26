using System;
using System.Collections.Generic;
namespace C500GDesktop {
 public interface IVideoPlayer:IDisposable { PlayerState State{get;} void SetAudio(bool enabled,int volume); }
 internal interface IAccountVideoController {
  IReadOnlyList<CloudDeviceInfo> AccountDevices{get;}
  string DeviceListErrorCode{get;}
  IVideoPlayer CreateVideoPlayer(CameraProfile camera,IntPtr window);
 }
 internal struct CloudVideoStatus {internal int State,Frames,Error;}
}
