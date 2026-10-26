using System;

namespace C500GDesktop
{
    internal interface ICloudPtzModeBridge { int GetPtzMode(IntPtr session); } // 0=unknown,1=legacy,2=touch

    // Optional video support; PTZ-only bridges do not need to implement this contract.
    internal interface ICloudVideoBridge
    {
        void Configure(IntPtr session, int devConfig);
        void VideoStart(IntPtr session, IntPtr window);
        void VideoStop(IntPtr session);
        // state: Idle=0, Opening=1, Buffering=2, Playing=3, Paused=4,
        // Stopped=5, Ended=6, Error=7. frames=-1 means unavailable.
        void VideoState(IntPtr session, out int state, out int frames, out int error);
        void VideoAudio(IntPtr session, bool enabled, int volume);
    }
}
