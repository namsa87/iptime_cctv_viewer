using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace C500GDesktop
{
    // Only this proxy lives with VLC. The SDK and its FFmpeg dependencies are loaded
    // by the same executable's --ptz-worker entry point in a separate process.
    internal sealed class PtzWorkerProcess : ICloudNativeBridge, ICloudTokenCapabilities, ICloudVideoBridge, ICloudPtzModeBridge, IDisposable
    {
        internal const int MaximumFrameBytes = 65536;
        internal const int MaximumTokenBytes = 4096;
        internal const int MaximumCommandBytes = 8192;
        private const byte ProtocolVersion = 1;
        private const byte Hello = 0, RegisterOp = 1, UnregisterOp = 2, OpenOp = 3,
            StateOp = 4, SendOp = 5, CloseOp = 6, ShutdownOp = 7,
            VideoStartOp = 8, VideoStopOp = 9, VideoStateOp = 10, VideoAudioOp = 11, ConfigureOp = 12, PtzModeOp = 13;
        private readonly object gate = new object();
        private readonly int callTimeout;
        private NamedPipeServerStream pipe;
        private Process child;
        private volatile bool available;
        private volatile bool disposed;
        private string error;
        public bool Available { get { return available && !disposed; } }
        public string Error { get { return error; } }
        public bool SupportsExtendedTokens { get { return true; } }
        internal int WorkerProcessId { get { lock (gate) { return child == null ? 0 : child.Id; } } }

        public PtzWorkerProcess(string exePath, string bridgePath, string sdkPath)
            : this(exePath, bridgePath, sdkPath, 15000, 10000) { }

        // Short bounded timeouts are used only by offline fake-worker tests.
        internal PtzWorkerProcess(string exePath, string bridgePath, string sdkPath, int startupMilliseconds, int callMilliseconds)
        {
            if (startupMilliseconds < 100 || startupMilliseconds > 60000 || callMilliseconds < 100 || callMilliseconds > 60000)
                throw new ArgumentOutOfRangeException("timeout");
            callTimeout = callMilliseconds;
            try
            {
                string executable = Path.GetFullPath(exePath);
                if (!File.Exists(executable)) throw new IOException();
                string bridge = String.IsNullOrWhiteSpace(bridgePath) ? "" : Path.GetFullPath(bridgePath);
                string sdk = String.IsNullOrWhiteSpace(sdkPath) ? "" : Path.GetFullPath(sdkPath);
                string name = "C500GPtz-" + Guid.NewGuid().ToString("N");
                pipe = CreateRestrictedPipe(name);
                long deadline = Deadline(startupMilliseconds);
                IAsyncResult connecting = pipe.BeginWaitForConnection(null, null);
                ProcessStartInfo start = new ProcessStartInfo(executable);
                start.Arguments = "--ptz-worker " + QuoteArgument(name) + " " + QuoteArgument(bridge) + " " + QuoteArgument(sdk) + " " +
                    Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.WindowStyle = ProcessWindowStyle.Hidden;
                start.WorkingDirectory = Path.GetDirectoryName(executable);
                // Std handles are redirected to NUL inside the worker before SDK load.
                // No token, account id, device id or command is in these arguments.
                child = Process.Start(start);
                if (child == null) throw new IOException();
                try { Wait(connecting, deadline); pipe.EndWaitForConnection(connecting); }
                finally { if (connecting.IsCompleted) connecting.AsyncWaitHandle.Close(); }
                uint connectedPid;
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out connectedPid) || connectedPid != (uint)child.Id)
                    throw new InvalidDataException();
                byte[] greeting = null;
                try
                {
                    greeting = ReadFrame(pipe, deadline);
                    ValidateResponse(greeting, Hello);
                    if (greeting[2] != 0)
                    {
                        int startupCode = greeting.Length == 7 ? GetInt32(greeting, 3) : 0;
                        error = StartupMessage(startupCode);
                        StopChild();
                        return;
                    }
                    if (greeting.Length != 4 || greeting[3] != 1) throw new InvalidDataException();
                }
                finally { Clear(greeting); }
                available = true;
            }
            catch (TimeoutException) { error = "PTZ 작업 프로세스 시작 시간이 초과되었습니다."; StopChild(); }
            catch { error = "독립 PTZ 작업 프로세스를 시작하지 못했습니다."; StopChild(); }
        }

        internal static NamedPipeServerStream CreateRestrictedPipe(string name)
        {
            SecurityIdentifier sid = WindowsIdentity.GetCurrent().User;
            if (sid == null) throw new InvalidOperationException();
            PipeSecurity security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(sid);
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, security);
        }

        public void Register(long accessId, byte[] token)
        {
            byte[] request = null, response = null;
            try
            {
                if (token == null || token.Length < 128 || token.Length > MaximumTokenBytes || (token.Length & 1) != 0)
                    throw new ArgumentException("계정 토큰 길이가 허용 범위를 벗어났습니다.");
                foreach (byte b in token)
                    if (!((b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F')))
                        throw new ArgumentException("계정 토큰 형식이 올바르지 않습니다.");
                request = Request(RegisterOp, 12 + token.Length);
                PutInt64(request, 2, accessId); PutInt32(request, 10, token.Length);
                Buffer.BlockCopy(token, 0, request, 14, token.Length);
                response = Exchange(request, RegisterOp, callTimeout);
                RequireLength(response, 3);
            }
            finally { Clear(request); Clear(response); Clear(token); }
        }
        public void Unregister() { Simple(UnregisterOp, 0, false); }
        public IntPtr Open(ulong deviceId)
        {
            byte[] request = Request(OpenOp, 8), response = null;
            try
            {
                PutInt64(request, 2, unchecked((long)deviceId));
                response = Exchange(request, OpenOp, callTimeout); RequireLength(response, 11);
                long id = GetInt64(response, 3);
                if (id <= 0 || (IntPtr.Size == 4 && id > Int32.MaxValue)) throw new InvalidDataException();
                return new IntPtr(id);
            }
            finally { Clear(request); Clear(response); }
        }
        public void State(IntPtr session, out int state, out int failure, out int received)
        {
            byte[] request = Request(StateOp, 8), response = null;
            state = failure = received = 0;
            try
            {
                PutInt64(request, 2, session.ToInt64());
                response = Exchange(request, StateOp, callTimeout); RequireLength(response, 15);
                state = GetInt32(response, 3); failure = GetInt32(response, 7); received = GetInt32(response, 11);
            }
            finally { Clear(request); Clear(response); }
        }
        public int Send(IntPtr session, byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload.Length > MaximumCommandBytes)
                throw new ArgumentException("PTZ 명령 크기가 허용 범위를 벗어났습니다.");
            byte[] request = Request(SendOp, 12 + payload.Length), response = null;
            try
            {
                PutInt64(request, 2, session.ToInt64()); PutInt32(request, 10, payload.Length);
                Buffer.BlockCopy(payload, 0, request, 14, payload.Length);
                response = Exchange(request, SendOp, callTimeout); RequireLength(response, 7);
                return GetInt32(response, 3);
            }
            finally { Clear(request); Clear(response); }
        }
        public void Close(IntPtr session) { Simple(CloseOp, session.ToInt64(), true); }
        public void VideoStart(IntPtr session, IntPtr window)
        {
            byte[] request = Request(VideoStartOp, 16), response = null;
            try
            {
                PutInt64(request, 2, session.ToInt64()); PutInt64(request, 10, window.ToInt64());
                response = Exchange(request, VideoStartOp, callTimeout);
            }
            finally { Clear(request); Clear(response); }
        }
        public void VideoStop(IntPtr session) { Simple(VideoStopOp, session.ToInt64(), true); }
        public void VideoState(IntPtr session, out int state, out int frames, out int error)
        {
            byte[] request = Request(VideoStateOp, 8), response = null;
            state = frames = error = 0;
            try
            {
                PutInt64(request, 2, session.ToInt64());
                response = Exchange(request, VideoStateOp, callTimeout);
                state = GetInt32(response, 3); frames = GetInt32(response, 7); error = GetInt32(response, 11);
            }
            finally { Clear(request); Clear(response); }
        }
        public void VideoAudio(IntPtr session, bool enabled, int volume)
        {
            if (volume < 0 || volume > 100) throw new ArgumentOutOfRangeException("volume");
            byte[] request = Request(VideoAudioOp, 13), response = null;
            try
            {
                PutInt64(request, 2, session.ToInt64()); request[10] = enabled ? (byte)1 : (byte)0; PutInt32(request, 11, volume);
                response = Exchange(request, VideoAudioOp, callTimeout);
            }
            finally { Clear(request); Clear(response); }
        }
        public int GetPtzMode(IntPtr session)
        {
            byte[] request=Request(PtzModeOp,8), response=null;
            try{PutInt64(request,2,session.ToInt64());response=Exchange(request,PtzModeOp,callTimeout);return GetInt32(response,3);}
            finally{Clear(request);Clear(response);}
        }
        public void Configure(IntPtr session, int devConfig)
        {
            byte[] request = Request(ConfigureOp, 12), response = null;
            try
            {
                PutInt64(request, 2, session.ToInt64()); PutInt32(request, 10, devConfig);
                response = Exchange(request, ConfigureOp, callTimeout);
            }
            finally { Clear(request); Clear(response); }
        }
        private void Simple(byte operation, long handle, bool hasHandle)
        {
            byte[] request = Request(operation, hasHandle ? 8 : 0), response = null;
            try
            {
                if (hasHandle) PutInt64(request, 2, handle);
                response = Exchange(request, operation, callTimeout); RequireLength(response, 3);
            }
            finally { Clear(request); Clear(response); }
        }
        private byte[] Exchange(byte[] request, byte operation, int timeout)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("PtzWorkerProcess");
                if (!available) throw new InvalidOperationException(error ?? "PTZ 작업 프로세스가 준비되지 않았습니다.");
                byte[] response = null;
                try
                {
                    long deadline = Deadline(timeout);
                    WriteFrame(pipe, request, deadline);
                    response = ReadFrame(pipe, deadline);
                    ValidateResponse(response, operation);
                    if (response[2] != 0)
                    {
                        RequireLength(response, 7);
                        int nativeCode = GetInt32(response, 3);
                        Clear(response); response = null;
                        throw new WorkerOperationException(nativeCode);
                    }
                    int expected = operation == OpenOp ? 11 : (operation == StateOp || operation == VideoStateOp) ? 15 : (operation == SendOp || operation == PtzModeOp) ? 7 : 3;
                    RequireLength(response, expected);
                    if (operation == OpenOp && (GetInt64(response, 3) <= 0 || GetInt64(response, 3) > Int32.MaxValue))
                        throw new InvalidDataException();
                    if (operation == VideoStateOp && !ValidVideoState(GetInt32(response, 3), GetInt32(response, 7)))
                        throw new InvalidDataException();
                    if(operation == PtzModeOp && (GetInt32(response,3)<0 || GetInt32(response,3)>2))throw new InvalidDataException();
                    byte[] result = response; response = null; return result;
                }
                catch (WorkerOperationException failure)
                {
                    if (failure.NativeCode != 0) throw new NativePtzException(failure.NativeCode);
                    throw new InvalidOperationException("PTZ 작업 프로세스에서 요청을 처리하지 못했습니다.");
                }
                catch (TimeoutException)
                {
                    error = "PTZ 작업 응답 시간이 초과되어 프로세스를 종료했습니다. 계정으로 다시 로그인해 주세요.";
                    StopChild(); throw new TimeoutException(error);
                }
                catch
                {
                    error = "PTZ 작업 프로세스 연결이 종료되었습니다. 계정으로 다시 로그인해 주세요.";
                    StopChild(); throw new InvalidOperationException(error);
                }
                finally { Clear(response); }
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                byte[] request = null, response = null;
                try
                {
                    if (available)
                    {
                        request = Request(ShutdownOp, 0);
                        response = Exchange(request, ShutdownOp, Math.Min(callTimeout, 1000));
                    }
                }
                catch { }
                finally { Clear(request); Clear(response); disposed = true; StopChild(); }
            }
        }
        private void StopChild()
        {
            available = false;
            if (pipe != null) { try { pipe.Dispose(); } catch { } pipe = null; }
            if (child != null)
            {
                try { if (!child.HasExited) { child.Kill(); child.WaitForExit(1000); } } catch { }
                try { child.Dispose(); } catch { } child = null;
            }
        }

        public static int RunWorker(string[] args)
        {
            return RunWorker(args, delegate { return new CloudNativeBridge(args[2], args[3]); });
        }
        // A local fake implements the same interface in offline protocol/process tests.
        internal static int RunWorker(string[] args, Func<ICloudNativeBridge> factory)
        {
            int parentPid;
            if (args == null || args.Length != 5 || args[0] != "--ptz-worker" ||
                !Regex.IsMatch(args[1] ?? "", "\\AC500GPtz-[a-f0-9]{32}\\z") ||
                !Int32.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out parentPid) || parentPid <= 0)
                return 2;
            Process parent = null;
            ICloudNativeBridge native = null;
            Dictionary<long, IntPtr> sessions = new Dictionary<long, IntPtr>();
            bool registered = false;
            try
            {
                SuppressStandardStreams();
                parent = Process.GetProcessById(parentPid);
                using (NamedPipeClientStream connection = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
                {
                    connection.Connect(15000);
                    uint serverPid;
                    if (!GetNamedPipeServerProcessId(connection.SafePipeHandle, out serverPid) || serverPid != (uint)parentPid)
                        return 3;
                    // Wait on the opened parent process object, so a later reused PID
                    // cannot keep the worker alive. This also exits a stuck native call.
                    Process watchedParent = parent;
                    Thread watcher = new Thread(delegate() { try { watchedParent.WaitForExit(); Environment.Exit(0); } catch { } });
                    watcher.IsBackground = true; watcher.Name = "PTZ parent lifetime"; watcher.Start();
                    native = factory();
                    ICloudTokenCapabilities capabilities = native as ICloudTokenCapabilities;
                    bool ready = native != null && native.Available && capabilities != null && capabilities.SupportsExtendedTokens;
                    byte[] greeting = Response(Hello, ready ? (byte)0 : (byte)1, ready ? 1 : 4);
                    if (ready) greeting[3] = 1; else PutInt32(greeting, 3, StartupCode(native == null ? null : native.Error));
                    try { WriteFrame(connection, greeting, Deadline(10000)); } finally { Clear(greeting); }
                    if (!ready) return 4;
                    long nextSession = 1;
                    while (true)
                    {
                        byte[] request = null, response = null;
                        bool shutdown = false;
                        try
                        {
                            request = ReadFrame(connection, 0);
                            if (request.Length < 2 || request[0] != ProtocolVersion || request[1] < RegisterOp || request[1] > PtzModeOp)
                                throw new InvalidDataException();
                            byte operation = request[1];
                            try
                            {
                                response = Dispatch(native, sessions, ref registered, ref nextSession, request, parentPid, out shutdown);
                            }
                            catch (InvalidDataException) { return 5; }
                            catch (NativePtzException failure) { response = Response(operation, 1, 4); PutInt32(response, 3, failure.Code); }
                            catch { response = Response(operation, 1, 4); }
                            WriteFrame(connection, response, Deadline(10000));
                            if (shutdown) return 0;
                        }
                        finally { Clear(request); Clear(response); }
                    }
                }
            }
            catch { return 6; }
            finally
            {
                // Cleanup can call the SDK; the parent always bounds it and kills a
                // stuck worker. No error text or command/token bytes are logged.
                if (native != null)
                {
                    foreach (IntPtr handle in sessions.Values) { try { native.Close(handle); } catch { } }
                    if (registered) { try { native.Unregister(); } catch { } }
                    IDisposable disposable = native as IDisposable;
                    if (disposable != null) { try { disposable.Dispose(); } catch { } }
                }
                if (parent != null) parent.Dispose();
            }
        }

        private static byte[] Dispatch(ICloudNativeBridge native, Dictionary<long, IntPtr> sessions,
            ref bool registered, ref long nextSession, byte[] request, int parentPid, out bool shutdown)
        {
            shutdown = false;
            byte op = request[1];
            byte[] response = null;
            if (op == RegisterOp)
            {
                if (request.Length < 14) throw new InvalidDataException();
                int size = GetInt32(request, 10);
                if (size < 128 || size > MaximumTokenBytes || (size & 1) != 0 || request.Length != 14 + size)
                    throw new InvalidDataException();
                byte[] token = new byte[size];
                try
                {
                    Buffer.BlockCopy(request, 14, token, 0, size);
                    foreach (byte b in token)
                        if (!((b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F')))
                            throw new InvalidDataException();
                    registered = true;
                    native.Register(GetInt64(request, 2), token);
                }
                finally { Clear(token); }
            }
            else if (op == UnregisterOp || op == ShutdownOp)
            {
                RequireLength(request, 2);
                foreach (IntPtr handle in sessions.Values) { try { native.Close(handle); } catch { } }
                sessions.Clear();
                if (registered) native.Unregister();
                registered = false;
                shutdown = op == ShutdownOp;
            }
            else if (op == OpenOp)
            {
                RequireLength(request, 10);
                if (!registered || sessions.Count >= 64 || nextSession > Int32.MaxValue) throw new InvalidOperationException();
                IntPtr nativeHandle = native.Open(unchecked((ulong)GetInt64(request, 2)));
                if (nativeHandle == IntPtr.Zero) throw new InvalidOperationException();
                long id = nextSession++; sessions.Add(id, nativeHandle);
                response = Response(op, 0, 8); PutInt64(response, 3, id);
            }
            else
            {
                if (request.Length < 10) throw new InvalidDataException();
                long id = GetInt64(request, 2);
                IntPtr handle;
                if (id <= 0 || !sessions.TryGetValue(id, out handle)) throw new InvalidOperationException();
                if (op == StateOp)
                {
                    RequireLength(request, 10);
                    int state, error, received;
                    native.State(handle, out state, out error, out received);
                    response = Response(op, 0, 12); PutInt32(response, 3, state); PutInt32(response, 7, error); PutInt32(response, 11, received);
                }
                else if (op == SendOp)
                {
                    if (request.Length < 14) throw new InvalidDataException();
                    int size = GetInt32(request, 10);
                    if (size < 1 || size > MaximumCommandBytes || request.Length != 14 + size) throw new InvalidDataException();
                    byte[] payload = new byte[size];
                    try
                    {
                        Buffer.BlockCopy(request, 14, payload, 0, size);
                        int code = native.Send(handle, payload);
                        response = Response(op, 0, 4); PutInt32(response, 3, code);
                    }
                    finally { Clear(payload); }
                }
                else if (op == CloseOp)
                {
                    RequireLength(request, 10); sessions.Remove(id); native.Close(handle);
                }
                else if(op == PtzModeOp){
                    RequireLength(request,10);var modes=native as ICloudPtzModeBridge;
                    int mode=modes==null?0:modes.GetPtzMode(handle);if(mode<0||mode>2)throw new InvalidOperationException();
                    response=Response(op,0,4);PutInt32(response,3,mode);
                }
                else if (op >= VideoStartOp && op <= ConfigureOp)
                {
                    // Every video operation uses the existing worker-local session map.
                    // Only the render target HWND crosses the boundary as a window handle.
                    int length = op == VideoStartOp ? 18 : op == VideoAudioOp ? 15 : op == ConfigureOp ? 14 : 10;
                    RequireLength(request, length);
                    if (op == VideoAudioOp && (request[10] > 1 || GetInt32(request, 11) < 0 || GetInt32(request, 11) > 100))
                        throw new InvalidDataException();
                    ICloudVideoBridge video = native as ICloudVideoBridge;
                    if (video == null) throw new InvalidOperationException();
                    if (op == VideoStartOp)
                    {
                        long rawWindow = GetInt64(request, 10);
                        if (IntPtr.Size == 4 && (rawWindow < Int32.MinValue || rawWindow > UInt32.MaxValue))
                            throw new InvalidOperationException();
                        IntPtr window = IntPtr.Size == 4 ? new IntPtr(unchecked((int)rawWindow)) : new IntPtr(rawWindow);
                        uint ownerPid;
                        // The pipe's server PID was authenticated before SDK loading.
                        // A caller cannot select another process's window as a render target.
                        if (window == IntPtr.Zero || !IsWindow(window) || GetWindowThreadProcessId(window, out ownerPid) == 0 || ownerPid != (uint)parentPid)
                            throw new InvalidOperationException();
                        video.VideoStart(handle, window);
                    }
                    else if (op == VideoStopOp) video.VideoStop(handle);
                    else if (op == VideoStateOp)
                    {
                        int state, frames, error;
                        video.VideoState(handle, out state, out frames, out error);
                        if (!ValidVideoState(state, frames)) throw new InvalidOperationException();
                        response = Response(op, 0, 12); PutInt32(response, 3, state); PutInt32(response, 7, frames); PutInt32(response, 11, error);
                    }
                    else if (op == VideoAudioOp) video.VideoAudio(handle, request[10] == 1, GetInt32(request, 11));
                    else video.Configure(handle, GetInt32(request, 10));
                }
                else throw new InvalidDataException();
            }
            return response ?? Response(op, 0, 0);
        }

        internal static byte[] ReadFrame(Stream stream, long deadline)
        {
            byte[] length = new byte[4], frame = null;
            try
            {
                ReadExact(stream, length, 0, 4, deadline);
                int size = GetInt32(length, 0);
                if (size < 2 || size > MaximumFrameBytes) throw new InvalidDataException();
                frame = new byte[size]; ReadExact(stream, frame, 0, size, deadline);
                byte[] result = frame; frame = null; return result;
            }
            finally { Clear(length); Clear(frame); }
        }
        internal static void WriteFrame(Stream stream, byte[] frame, long deadline)
        {
            if (frame == null || frame.Length < 2 || frame.Length > MaximumFrameBytes) throw new InvalidDataException();
            byte[] length = new byte[4];
            try
            {
                PutInt32(length, 0, frame.Length);
                WriteAll(stream, length, deadline); WriteAll(stream, frame, deadline);
            }
            finally { Clear(length); }
        }
        private static void ReadExact(Stream stream, byte[] bytes, int offset, int count, long deadline)
        {
            while (count > 0)
            {
                IAsyncResult reading = stream.BeginRead(bytes, offset, count, null, null);
                int got;
                try
                {
                    try { Wait(reading, deadline); }
                    catch (TimeoutException)
                    {
                        // Abort overlapped pipe I/O before its frame buffer is wiped.
                        stream.Dispose(); try { stream.EndRead(reading); } catch { } throw;
                    }
                    got = stream.EndRead(reading);
                }
                finally { if (reading.IsCompleted) reading.AsyncWaitHandle.Close(); }
                if (got <= 0) throw new EndOfStreamException();
                offset += got; count -= got;
            }
        }
        private static void WriteAll(Stream stream, byte[] bytes, long deadline)
        {
            IAsyncResult writing = stream.BeginWrite(bytes, 0, bytes.Length, null, null);
            try
            {
                try { Wait(writing, deadline); }
                catch (TimeoutException) { stream.Dispose(); try { stream.EndWrite(writing); } catch { } throw; }
                stream.EndWrite(writing);
            }
            finally { if (writing.IsCompleted) writing.AsyncWaitHandle.Close(); }
        }
        internal static long Deadline(int milliseconds)
        {
            return Stopwatch.GetTimestamp() + (long)(milliseconds * (Stopwatch.Frequency / 1000.0));
        }
        private static void Wait(IAsyncResult operation, long deadline)
        {
            int wait = Timeout.Infinite;
            if (deadline != 0)
            {
                double remaining = (deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
                if (remaining <= 0) throw new TimeoutException();
                wait = (int)Math.Min(Int32.MaxValue, Math.Ceiling(remaining));
            }
            if (!operation.AsyncWaitHandle.WaitOne(wait)) throw new TimeoutException();
        }
        private static byte[] Request(byte operation, int bytes) { byte[] b = new byte[2 + bytes]; b[0] = ProtocolVersion; b[1] = operation; return b; }
        private static byte[] Response(byte operation, byte status, int bytes) { byte[] b = new byte[3 + bytes]; b[0] = ProtocolVersion; b[1] = operation; b[2] = status; return b; }
        private static void ValidateResponse(byte[] frame, byte operation)
        {
            if (frame == null || frame.Length < 3 || frame[0] != ProtocolVersion || frame[1] != operation || frame[2] > 1)
                throw new InvalidDataException();
        }
        private static bool ValidVideoState(int state, int frames) { return state >= 0 && state <= 7 && frames >= -1; }
        private static void RequireLength(byte[] frame, int size) { if (frame.Length != size) throw new InvalidDataException(); }
        private static int GetInt32(byte[] bytes, int offset) { return BitConverter.ToInt32(bytes, offset); }
        private static long GetInt64(byte[] bytes, int offset) { return BitConverter.ToInt64(bytes, offset); }
        private static void PutInt32(byte[] bytes, int offset, int value) { byte[] temp = BitConverter.GetBytes(value); Buffer.BlockCopy(temp, 0, bytes, offset, 4); Clear(temp); }
        private static void PutInt64(byte[] bytes, int offset, long value) { byte[] temp = BitConverter.GetBytes(value); Buffer.BlockCopy(temp, 0, bytes, offset, 8); Clear(temp); }
        private static void Clear(byte[] bytes) { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); }
        internal static string QuoteArgument(string value)
        {
            if (value == null) value = "";
            StringBuilder quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { quoted.Append('\\', slashes * 2 + 1); quoted.Append('"'); slashes = 0; continue; }
                quoted.Append('\\', slashes); slashes = 0; quoted.Append(c);
            }
            quoted.Append('\\', slashes * 2); quoted.Append('"'); return quoted.ToString();
        }
        private static int StartupCode(string nativeError)
        {
            // Only classify fixed bridge errors. Vendor/exception text is never sent.
            if (nativeError == null) return 0;
            if (nativeError.IndexOf("x64 실행", StringComparison.Ordinal) >= 0) return 1;
            if (nativeError.IndexOf("브리지 파일", StringComparison.Ordinal) >= 0) return 2;
            if (nativeError.IndexOf("SDK를 찾지", StringComparison.Ordinal) >= 0) return 3;
            if (nativeError.IndexOf("검증된 버전", StringComparison.Ordinal) >= 0) return 4;
            if (nativeError.IndexOf("런타임", StringComparison.Ordinal) >= 0) return 5;
            if (nativeError.IndexOf("브리지를 로드", StringComparison.Ordinal) >= 0) return 6;
            if (nativeError.IndexOf("브리지 버전", StringComparison.Ordinal) >= 0) return 7;
            if (nativeError.IndexOf("API 누락", StringComparison.Ordinal) >= 0) return 8;
            return 0;
        }
        private static string StartupMessage(int code)
        {
            string[] messages = { "독립 PTZ 모듈 초기화에 실패했습니다.", "계정 PTZ에는 x64 실행 파일이 필요합니다.",
                "계정 PTZ 브리지 파일을 찾지 못했습니다.", "로컬 IoTVideo SDK를 찾지 못했습니다.",
                "검증된 버전과 다른 IoTVideo SDK입니다.", "IoTVideo SDK 로드 실패 · x64 Visual C++ 런타임을 확인해 주세요.",
                "계정 PTZ 브리지를 로드하지 못했습니다.", "계정 PTZ 브리지 버전이 맞지 않습니다.", "계정 PTZ 브리지 API가 누락되었습니다." };
            return code >= 0 && code < messages.Length ? messages[code] : messages[0];
        }
        private static void SuppressStandardStreams()
        {
            Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
            // Keep the native NUL handle for the short worker process lifetime.
            SafeFileHandle nul = CreateFile("NUL", 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (nul.IsInvalid) throw new IOException();
            if (!SetStdHandle(-11, nul.DangerousGetHandle()) || !SetStdHandle(-12, nul.DangerousGetHandle())) throw new IOException();
            nativeNull = nul;
        }
        private static SafeFileHandle nativeNull;
        private sealed class WorkerOperationException : Exception
        {
            internal readonly int NativeCode;
            internal WorkerOperationException(int code) { NativeCode = code; }
        }
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetStdHandle(int id, IntPtr handle);
    }
}
