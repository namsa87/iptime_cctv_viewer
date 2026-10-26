// MSVC x64 ABI adapter reconstructed from the exact shipped PDB and host call sites.
// Pinned SDK metadata: native/modern-sdk-manifest.json. Local analysis files are excluded.
// The vendor DLL is an external local dependency, never modified or redistributed here.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include <cstdint>
#include <atomic>
#include <cstddef>
#include <cstdio>
#include <string>
#include <vector>
#include <memory>
#include <mutex>
#include <unordered_set>
#include <algorithm>
#include <stdexcept>
#include <thread>
#include <functional>
#include <deque>
#include <mmsystem.h>
#include <cmath>
#include "ModernCredentials.h"
#include "ModernMessageCallback.h"
#include "PtzCapabilityParser.h"
#pragma comment(lib,"bcrypt.lib")
#pragma comment(lib,"user32.lib")
#pragma comment(lib,"winmm.lib")
#define API extern "C" __declspec(dllexport)
namespace {
// PDB InitializerParams: 88 bytes; strings use the MSVC release CRT ABI.
struct InitParams {
 uint64_t access_id=0; std::string access_token; std::string iv_host;
 int device_type=1; int16_t app_sys_lang=0; uint32_t p2p_port_type=0; int log_level=0;
};
struct InitListener;
class Initializer {
 public:
 virtual void register_p2p(InitParams)=0;
 virtual void unregister_p2p(bool)=0;
 virtual void set_state_listener(std::shared_ptr<InitListener>)=0;
 virtual int link_state()=0;
 virtual void update_access_token(std::string)=0;
};
struct Route { std::string device_id; int source_id=0; int ch_control=0; int type=1; };
struct SdkError { std::string domain; int cat; std::string cat_desc; int code; std::string code_desc; std::string user_info; };
struct Unpacked {uint8_t type;std::vector<uint8_t> data;uint64_t pts;int keyfrm;int st_idx;uint8_t pkt_seq_num;};
struct MsgPktHdr;
class ConnectionListener {public: virtual void on_state_change(int)=0;virtual void on_rcv_data(Unpacked)=0;virtual void on_rcv_builtin_cmd(uint8_t*,int,const MsgPktHdr&)=0;virtual void on_rcv_error(std::shared_ptr<SdkError>)=0;};
static_assert(sizeof(SdkError)==144 && offsetof(SdkError,code)==72,"Error PDB layout");
static_assert(sizeof(Unpacked)==56 && offsetof(Unpacked,data)==8 && offsetof(Unpacked,pkt_seq_num)==48,"Unpacked PDB layout");
using MessageCallback=c500g_message::MessageCallback<SdkError>;
struct SendDataArgs {
 uint64_t category=0; uint8_t type=3; std::vector<uint8_t> data;
 uint16_t camId=0; uint8_t sequence=0; int64_t timeout=10000000;
 std::shared_ptr<MessageCallback> callback; uint64_t batch_num=1; int64_t pts=0; uint8_t keyfrm=0;
};
static_assert(sizeof(std::string)==32,"MSVC release string ABI required");
static_assert(sizeof(std::shared_ptr<void>)==16,"MSVC shared_ptr ABI required");
static_assert(sizeof(InitParams)==88 && offsetof(InitParams,access_token)==8 && offsetof(InitParams,iv_host)==40 && offsetof(InitParams,log_level)==84,"InitializerParams PDB layout");
static_assert(sizeof(Route)==48 && offsetof(Route,source_id)==32 && offsetof(Route,type)==40,"Route PDB layout");
static_assert(sizeof(SendDataArgs)==96 && offsetof(SendDataArgs,data)==16 && offsetof(SendDataArgs,camId)==40 && offsetof(SendDataArgs,timeout)==48 && offsetof(SendDataArgs,callback)==56 && offsetof(SendDataArgs,batch_num)==72,"send_data_args PDB layout");
struct alignas(8) SdkObject24 { unsigned char bytes[24]{}; };
using Factory=std::shared_ptr<Initializer>(*)();
using Constructor=void(*)(void*,const Route&);
using Destructor=void(*)(void*);
using Connect=int(*)(void*,bool);
using ConnInt=int(*)(void*);
using Send=int(*)(void*,const SendDataArgs&);
using SetListener=void(*)(void*,std::weak_ptr<ConnectionListener>);
using LogConstructor=void(*)(void*);
using LogInit=void(*)(void*,int);
struct SdkApi { HMODULE module=nullptr; Factory factory=nullptr; Constructor ctor=nullptr; Destructor dtor=nullptr;
 Connect connect=nullptr;ConnInt disconnect=nullptr,state=nullptr,platform=nullptr;Send send=nullptr;
 SetListener listener=nullptr;LogConstructor logCtor=nullptr;Destructor logDtor=nullptr;LogInit logInit=nullptr;
};
SdkApi sdk; std::mutex gate; std::shared_ptr<Initializer> initializer; bool registered=false;
// Kept alive for the process lifetime: its profile is shared by both vendor logger modules.
SdkObject24 logObject; bool logConstructed=false,loggingDisabled=false;
constexpr unsigned char expectedHash[32]={0xec,0x07,0xcb,0x4b,0x98,0xbc,0x48,0x6e,0x8b,0xdb,0x05,0x8c,0x86,0x95,0xd1,0x6f,0x0f,0xb2,0xc5,0x24,0xbe,0x50,0xdd,0x8c,0x9d,0x0f,0x0c,0xfa,0x87,0xfa,0x09,0x90};
bool PinnedModule(HMODULE module,const unsigned char* requiredHash=expectedHash){
 wchar_t path[32768];DWORD length=GetModuleFileNameW(module,path,32768);if(!length||length>=32768)return false;
 HANDLE file=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);if(file==INVALID_HANDLE_VALUE)return false;
 BCRYPT_ALG_HANDLE alg=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;bool ok=false;
 if(BCryptOpenAlgorithmProvider(&alg,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0 && BCryptCreateHash(alg,&hash,nullptr,0,nullptr,0,0)>=0){
  unsigned char buffer[65536],digest[32];DWORD got=0;bool readOk=true;
  while(true){if(!ReadFile(file,buffer,sizeof(buffer),&got,nullptr)){readOk=false;break;}if(!got)break;if(BCryptHashData(hash,buffer,got,0)<0){readOk=false;break;}}
  ok=readOk && BCryptFinishHash(hash,digest,sizeof(digest),0)>=0 && std::equal(digest,digest+32,requiredHash);
 }
 if(hash)BCryptDestroyHash(hash);if(alg)BCryptCloseAlgorithmProvider(alg,0);CloseHandle(file);return ok;
}
template<class T>T Resolve(const char* name){auto value=GetProcAddress(sdk.module,name);if(!value)throw std::runtime_error("SDK_EXPORT");return reinterpret_cast<T>(value);}
void EnsureApi(){
 if(sdk.factory)return;
 sdk.module=GetModuleHandleW(L"IOT_LIB.dll");if(!sdk.module||!PinnedModule(sdk.module))throw std::runtime_error("SDK_HASH");
 sdk.ctor=Resolve<Constructor>("??0Connection@iotvideo@@QEAA@AEBURoute@IConnection@1@@Z");
 sdk.dtor=Resolve<Destructor>("??1Connection@iotvideo@@QEAA@XZ");
 sdk.connect=Resolve<Connect>("?connect@Connection@iotvideo@@QEAAH_N@Z");
 sdk.disconnect=Resolve<ConnInt>("?disconnect@Connection@iotvideo@@QEAAHXZ");
 sdk.state=Resolve<ConnInt>("?get_conn_state@Connection@iotvideo@@UEAA?AW4State@IConnection@2@XZ");
 sdk.platform=Resolve<ConnInt>("?get_device_platform@Connection@iotvideo@@QEAA?AW4DevicePlatform@IConnection@2@XZ");
 sdk.send=Resolve<Send>("?send_data@Connection@iotvideo@@UEAAHAEBUsend_data_args@IConnection@2@@Z");
 sdk.listener=Resolve<SetListener>("?set_listener@Connection@iotvideo@@QEAAXV?$weak_ptr@VListener@Connection@iotvideo@@@std@@@Z");
 sdk.logCtor=Resolve<LogConstructor>("??0WinAlth@@QEAA@XZ");
 sdk.logDtor=Resolve<Destructor>("??1WinAlth@@QEAA@XZ");
 sdk.logInit=Resolve<LogInit>("?initLog@WinAlth@@QEAAXW4LogLevel@gwbase@@@Z");
 sdk.factory=Resolve<Factory>("?instance_ptr@IIoTVideoInitializer@iotvideo@@SA?AV?$shared_ptr@VIIoTVideoInitializer@iotvideo@@@std@@XZ");
}
void DisableLogging(){
 if(loggingDisabled)return;
 // Public SDK API clears previous pipelines and applies LOG_OFF to IOT and GWPlayer.
 if(!logConstructed){sdk.logCtor(&logObject);logConstructed=true;}
 sdk.logInit(&logObject,0);
 // Helper IPC must use a named pipe; no credentials may reach stdout/stderr.
 FILE* sink=nullptr;if(freopen_s(&sink,"NUL","w",stdout)!=0 || freopen_s(&sink,"NUL","w",stderr)!=0)throw std::runtime_error("SDK_LOG_SINK");
 loggingDisabled=true;
}
struct WipeStringOnExit {
 std::string& value;
 explicit WipeStringOnExit(std::string& text):value(text){}
 ~WipeStringOnExit(){if(!value.empty())SecureZeroMemory(&value[0],value.size());}
};

// Public SDK interfaces only. Opaque shared_ptr keeps the SDK-owned control block and RTTI.
// Explicit sret signatures are essential for MSVC x64 member functions returning shared_ptr.
using OpaquePtr=std::shared_ptr<void>;
using LiveFactory=OpaquePtr(*)(std::string,int,int);
using MessageFactory=OpaquePtr(*)();
using ReadProperty=void(*)(void*,std::string,std::string,int64_t,std::shared_ptr<MessageCallback>);
using GetConn=OpaquePtr*(*)(void*,OpaquePtr*);
using GetError=std::shared_ptr<SdkError>*(*)(void*,std::shared_ptr<SdkError>*);
using PlayerInt=int(*)(void*);
using PlayerMute=void(*)(void*,bool);
using PlayerPtr=void(*)(void*,OpaquePtr);
using OptInt=void(*)(void*,std::string,int64_t);
using SetDefinition=void(*)(void*,int,std::function<void(std::shared_ptr<SdkError>)>);
static_assert(sizeof(std::function<void(std::shared_ptr<SdkError>)>)==64,"SDK callback ABI");
using RenderConstructor=void(*)(void*,int);
using RenderCreate=void(*)(void*,HWND,int);
using RenderSize=void(*)(void*,int&,int&);
struct VideoApi {
 HMODULE module=nullptr;LiveFactory create=nullptr;
 PlayerInt state=nullptr;GetError error=nullptr;PlayerMute mute=nullptr;
 PlayerPtr add=nullptr,remove=nullptr,setAudio=nullptr;
 RenderConstructor renderCtor=nullptr;Destructor renderDtor=nullptr,renderStart=nullptr,renderStop=nullptr;
 RenderCreate renderCreate=nullptr;RenderSize renderSize=nullptr;
};
VideoApi video;
constexpr unsigned char expectedPlayerHash[32]={0x62,0x2e,0x31,0xd0,0x42,0x81,0x82,0xd5,0x3e,0xdb,0xab,0xeb,0x53,0xaa,0xb7,0xe0,0x95,0xac,0x94,0x40,0x53,0xa1,0xb9,0x0f,0xf9,0xd3,0x79,0xcc,0x8b,0x75,0xc2,0xd2};
template<class T>T ResolvePlayer(const char* name){auto p=GetProcAddress(video.module,name);if(!p)throw std::runtime_error("PLAYER_EXPORT");return reinterpret_cast<T>(p);}
template<class T>T Virtual(void* p,size_t offset){return reinterpret_cast<T>((*reinterpret_cast<void***>(p))[offset/sizeof(void*)]);}
void EnsureVideoApi(){
 if(video.create)return;
 video.module=GetModuleHandleW(L"GWPlayerLibV1.dll");
 if(!video.module||!PinnedModule(video.module,expectedPlayerHash))throw std::runtime_error("PLAYER_HASH");
 video.state=ResolvePlayer<PlayerInt>("?get_state@APlayer@gwplayer@@QEAA?AW4PlaybackState@2@XZ");
 video.error=ResolvePlayer<GetError>("?get_error@APlayer@gwplayer@@UEAA?AV?$shared_ptr@UError@gwbase@@@std@@XZ");
 video.mute=ResolvePlayer<PlayerMute>("?set_mute@APlayer@gwplayer@@QEAAX_N@Z");
 video.add=ResolvePlayer<PlayerPtr>("?add_video_view@APlayer@gwplayer@@QEAAXV?$shared_ptr@VVideoRenderView@gwplayer@@@std@@@Z");
 video.remove=ResolvePlayer<PlayerPtr>("?remove_video_view@APlayer@gwplayer@@QEAAXV?$shared_ptr@VVideoRenderView@gwplayer@@@std@@@Z");
 video.setAudio=ResolvePlayer<PlayerPtr>("?set_audio_render@APlayer@gwplayer@@QEAAXV?$shared_ptr@VAbstractAudioRenderer@gwplayer@@@std@@@Z");
 video.renderCtor=ResolvePlayer<RenderConstructor>("??0WinVideoRenderView@gwplayer@@QEAA@H@Z");
 video.renderDtor=ResolvePlayer<Destructor>("??1WinVideoRenderView@gwplayer@@UEAA@XZ");
 video.renderCreate=ResolvePlayer<RenderCreate>("?CreateRnederMode@WinVideoRenderView@gwplayer@@QEAAXPEAUHWND__@@H@Z");
 video.renderStart=ResolvePlayer<Destructor>("?start@WinVideoRenderView@gwplayer@@QEAAXXZ");
 video.renderStop=ResolvePlayer<Destructor>("?stop@WinVideoRenderView@gwplayer@@QEAAXXZ");
 video.renderSize=ResolvePlayer<RenderSize>("?getVideoSize@WinVideoRenderView@gwplayer@@QEAAXAEAH0@Z");
 video.create=Resolve<LiveFactory>("?create@ILivePlayer@iotvideo@@SA?AV?$shared_ptr@VILivePlayer@iotvideo@@@std@@V?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@4@HH@Z");
}
void* PlayerBase(void* live){
 // PDB: ILivePlayer virtual IPlayer base uses vbptr at8, vbtable displacement index1.
 // Official host EF85F/EEBA3 performs this exact adjustment; IPlayer inherits APlayer at0.
 unsigned char* vb=static_cast<unsigned char*>(live)+8;
 int displacement=(*reinterpret_cast<const int**>(vb))[1];
 if(displacement!=200)throw std::runtime_error("PLAYER_LAYOUT"); // exact pinned LivePlayer232-byte layout
 return vb+displacement;
}
struct AudioConfig {
 float volume;double rate;int channels,bitWidth,sampleRate;double timebase;
 OpaquePtr frameQueues;OpaquePtr syncStrategy;
};
static_assert(sizeof(AudioConfig)==72 && offsetof(AudioConfig,sampleRate)==24 && offsetof(AudioConfig,timebase)==32,"AudioRendererConfigure PDB");
// Exact public AbstractAudioRenderer vtable from the PDB. SDK supplies decoded PCM.
class AudioOutput {
 struct Block {WAVEHDR header{};std::vector<char> bytes;};
 std::mutex mutex;HWAVEOUT handle=nullptr;std::deque<std::unique_ptr<Block>> blocks;
 size_t queued=0;DWORD byteRate=0;std::atomic<int> volume{100};std::atomic<int> lastError{0};
 void Reap(){
  while(!blocks.empty()&&(blocks.front()->header.dwFlags&WHDR_DONE)){
   auto& b=blocks.front();if(waveOutUnprepareHeader(handle,&b->header,sizeof(WAVEHDR))!=MMSYSERR_NOERROR)break;
   queued-=b->bytes.size();blocks.pop_front();
  }
 }
 void ReleaseLocked(){
  if(!handle)return;
  // waveOutReset synchronously returns queued buffers. Never free a still-owned buffer.
  MMRESULT reset=waveOutReset(handle);
  if(reset==MMSYSERR_NOERROR){
   bool released=true;
   for(auto& b:blocks){
    if(b->header.dwFlags&WHDR_PREPARED){
     MMRESULT rc=waveOutUnprepareHeader(handle,&b->header,sizeof(WAVEHDR));
     if(rc!=MMSYSERR_NOERROR){lastError.store(-400-static_cast<int>(rc));released=false;}
    }
   }
   if(released){
    blocks.clear();queued=0;MMRESULT rc=waveOutClose(handle);
    if(rc==MMSYSERR_NOERROR)handle=nullptr;else lastError.store(-400-static_cast<int>(rc));
   }
  }else{
   lastError.store(-400-static_cast<int>(reset));
   // Keep device/buffer ownership if a driver refuses reset; worker exit reclaims these.
  }
 }
 void ApplyVolume(){if(handle){DWORD level=static_cast<DWORD>(volume.load()*65535/100);waveOutSetVolume(handle,level|(level<<16));}}
 public:
 virtual ~AudioOutput(){std::lock_guard<std::mutex> lock(mutex);ReleaseLocked();if(handle){for(auto& b:blocks)b.release();blocks.clear();}}
 virtual void init(const AudioConfig& config){
  try{std::lock_guard<std::mutex> lock(mutex);ReleaseLocked();if(handle)return;
   if(config.channels<1||config.channels>2||config.sampleRate<4000||config.sampleRate>192000||
     (config.bitWidth!=8&&config.bitWidth!=16&&config.bitWidth!=24&&config.bitWidth!=32)){lastError.store(-410);return;}
   WAVEFORMATEX format{};format.wFormatTag=WAVE_FORMAT_PCM;format.nChannels=static_cast<WORD>(config.channels);
   format.nSamplesPerSec=static_cast<DWORD>(config.sampleRate);format.wBitsPerSample=static_cast<WORD>(config.bitWidth);
   format.nBlockAlign=static_cast<WORD>(config.channels*config.bitWidth/8);format.nAvgBytesPerSec=format.nSamplesPerSec*format.nBlockAlign;
   MMRESULT rc=waveOutOpen(&handle,WAVE_MAPPER,&format,0,0,CALLBACK_NULL);
   if(rc!=MMSYSERR_NOERROR){handle=nullptr;lastError.store(-400-static_cast<int>(rc));return;}
   byteRate=format.nAvgBytesPerSec;lastError.store(0);ApplyVolume();
  }catch(...){lastError.store(-499);}
 }
 virtual void set_channel_id(int){}
 virtual void frameUpdate(const uint8_t* data,int length){
  if(!data||length<=0||length>1024*1024)return;
  try{std::lock_guard<std::mutex> lock(mutex);if(!handle)return;Reap();
   if(queued>byteRate/2)return; // Bound latency and memory if an output device stalls.
   std::unique_ptr<Block> b(new Block);b->bytes.assign(reinterpret_cast<const char*>(data),reinterpret_cast<const char*>(data)+length);
   b->header.lpData=b->bytes.data();b->header.dwBufferLength=static_cast<DWORD>(length);
   // Acquire container ownership before the driver can retain this buffer. A later
   // deque allocation failure must never free memory already passed to waveOutWrite.
   blocks.push_back(std::move(b));Block* owned=blocks.back().get();
   MMRESULT rc=waveOutPrepareHeader(handle,&owned->header,sizeof(WAVEHDR));
   if(rc!=MMSYSERR_NOERROR){blocks.pop_back();lastError.store(-400-static_cast<int>(rc));return;}
   rc=waveOutWrite(handle,&owned->header,sizeof(WAVEHDR));
   if(rc!=MMSYSERR_NOERROR){
    MMRESULT release=waveOutUnprepareHeader(handle,&owned->header,sizeof(WAVEHDR));
    if(release==MMSYSERR_NOERROR)blocks.pop_back();else queued+=owned->bytes.size();
    lastError.store(-400-static_cast<int>(rc));return;
   }
   queued+=owned->bytes.size();
  }catch(...){lastError.store(-499);}
 }
 virtual void release(){try{std::lock_guard<std::mutex> lock(mutex);ReleaseLocked();}catch(...){lastError.store(-499);}}
 virtual int setVolume(float level){
  if(!std::isfinite(level))return -1;int n=static_cast<int>(std::max(0.0f,std::min(1.0f,level))*100.0f);
  volume.store(n);try{std::lock_guard<std::mutex> lock(mutex);ApplyVolume();return lastError.load();}catch(...){return -499;}
 }
 virtual void setRenderState(int){}
 virtual int64_t computeRenderPosition(int64_t timestamp,double timebase,float){return static_cast<int64_t>(static_cast<double>(timestamp)*timebase);}
 int Error()const{return lastError.load();}
};
// A worker-owned child HWND avoids passing a foreign-process swap-chain window to D3D.
// All HWND lifecycle calls and renderer construction/destruction run on its owner thread.
struct RenderHost {
 HWND parent=nullptr;std::atomic<HWND> window{nullptr};void* player=nullptr;OpaquePtr keepPlayer,view;
 std::thread uiThread,sizeThread;HANDLE ready=nullptr;
 std::atomic<bool> stopping{false},frameObserved{false};std::atomic<int> error{0};
 explicit RenderHost(HWND target,void* base,OpaquePtr owner):parent(target),player(base),keepPlayer(std::move(owner)){
  ready=CreateEventW(nullptr,TRUE,FALSE,nullptr);if(!ready)throw std::runtime_error("VIDEO_EVENT");
 }
 ~RenderHost(){Stop();if(ready)CloseHandle(ready);}
 static LRESULT CALLBACK WindowProc(HWND hwnd,UINT message,WPARAM wp,LPARAM lp){
  RenderHost* self=reinterpret_cast<RenderHost*>(GetWindowLongPtrW(hwnd,GWLP_USERDATA));
  if(message==WM_NCCREATE){self=static_cast<RenderHost*>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams);SetWindowLongPtrW(hwnd,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(self));}
  if(self){
   if(message==WM_TIMER){
    if(!IsWindow(self->parent)){self->error.store(-312);DestroyWindow(hwnd);return 0;}
    RECT r{};GetClientRect(self->parent,&r);SetWindowPos(hwnd,nullptr,0,0,std::max(1L,r.right),std::max(1L,r.bottom),SWP_NOZORDER|SWP_NOACTIVATE);return 0;
   }
   if(message==WM_LBUTTONDOWN||message==WM_LBUTTONUP||message==WM_LBUTTONDBLCLK){PostMessageW(self->parent,message,wp,lp);return 0;}
   if(message==WM_MOUSEACTIVATE)return MA_NOACTIVATE;
   if(message==WM_CLOSE){DestroyWindow(hwnd);return 0;}
   if(message==WM_DESTROY){KillTimer(hwnd,1);self->window.store(nullptr);PostQuitMessage(0);return 0;}
   if(message==WM_ERASEBKGND)return 1;
  }
  return DefWindowProcW(hwnd,message,wp,lp);
 }
 void Run()noexcept{
  bool attached=false;
  try{
   static std::once_flag registration;std::call_once(registration,[]{
    WNDCLASSEXW wc{};wc.cbSize=sizeof(wc);wc.lpfnWndProc=WindowProc;wc.hInstance=GetModuleHandleW(nullptr);
    wc.lpszClassName=L"C500G.P2P.Render.1";wc.hCursor=LoadCursorW(nullptr,MAKEINTRESOURCEW(32512));wc.style=CS_DBLCLKS;
    if(!RegisterClassExW(&wc)&&GetLastError()!=ERROR_CLASS_ALREADY_EXISTS)throw std::runtime_error("VIDEO_CLASS");
   });
   RECT r{};if(!IsWindow(parent)||!GetClientRect(parent,&r))throw std::runtime_error("VIDEO_PARENT");
   HWND child=CreateWindowExW(WS_EX_NOPARENTNOTIFY,L"C500G.P2P.Render.1",L"",WS_CHILD|WS_VISIBLE|WS_CLIPSIBLINGS,
     0,0,std::max(1L,r.right),std::max(1L,r.bottom),parent,nullptr,GetModuleHandleW(nullptr),this);
   if(!child)throw std::runtime_error("VIDEO_WINDOW");window.store(child);SetTimer(child,1,150,nullptr);
   // PDB WinVideoRenderView size952 (0x3B8), VideoRenderView base at0.
   void* raw=::operator new(952);try{video.renderCtor(raw,0);}catch(...){::operator delete(raw);throw;}
   view=OpaquePtr(raw,[](void* p){try{video.renderDtor(p);}catch(...){}::operator delete(p);});
   video.renderCreate(view.get(),child,2);video.renderStart(view.get());video.add(player,view);attached=true;
   sizeThread=std::thread([this]{
    try{while(!stopping.load()){
      int height=0,width=0;video.renderSize(view.get(),height,width);
      if(height>0&&width>0){frameObserved.store(true);break;}
      Sleep(100);
    }}catch(...){error.store(-314);}
   });
   SetEvent(ready);
   MSG msg{};while(!stopping.load()&&GetMessageW(&msg,nullptr,0,0)>0){TranslateMessage(&msg);DispatchMessageW(&msg);}
  }catch(...){error.store(-310);SetEvent(ready);}
  stopping.store(true);
  if(sizeThread.joinable())sizeThread.join(); // getVideoSize has a bounded2.5s wait when no frame exists.
  if(attached){try{video.remove(player,view);}catch(...){error.store(-315);}}
  if(view){try{video.renderStop(view.get());}catch(...){error.store(-315);}view.reset();}
  HWND child=window.exchange(nullptr);if(child&&IsWindow(child))DestroyWindow(child);
 }
 int Start(){uiThread=std::thread([this]{Run();});if(WaitForSingleObject(ready,10000)!=WAIT_OBJECT_0){error.store(-311);return -311;}return error.load();}
 void Stop()noexcept{stopping.store(true);HWND child=window.load();if(child)PostMessageW(child,WM_CLOSE,0,0);if(uiThread.joinable())uiThread.join();}
};
struct CapabilityState {
 // phase0 idle,1 pending,2 resolved,3 absent/malformed,4 SDK error.
 std::atomic<int> mode{0},phase{0};
 std::atomic<int> parseReason{0},rootKind{0},pathDepth{0},targetPresent{0},targetKind{0},targetMode{0},bom{0};
 std::atomic<uint32_t> bytes{0},trailingNul{0};
 std::shared_ptr<c500g_message::NumericStatus> numbers=std::make_shared<c500g_message::NumericStatus>();
};
uint32_t nextSessionIndex=1;
struct Session {
 const uint32_t diagnosticIndex=nextSessionIndex++;
 std::string deviceId;
 std::shared_ptr<c500g_message::NumericStatus> ptzNumbers=std::make_shared<c500g_message::NumericStatus>();
 std::shared_ptr<CapabilityState> capability=std::make_shared<CapabilityState>();
 bool capabilityRequested=false;
 OpaquePtr live,connection;void* player=nullptr;std::shared_ptr<AudioOutput> audio;
 std::unique_ptr<RenderHost> render;bool started=false,everConnected=false;ULONGLONG startedAt=0;
 int error=0;bool audioEnabled=false;int audioVolume=100;
 explicit Session(uint64_t id,int* diagnosticStep=nullptr):deviceId(std::to_string(id)){
  auto step=[diagnosticStep](int value){if(diagnosticStep)*diagnosticStep=value;};
  step(20);EnsureVideoApi();step(21);live=video.create(deviceId,0,18);if(!live)throw std::runtime_error("PLAYER_CREATE");
  try{
   step(22);player=PlayerBase(live.get());step(23);Virtual<GetConn>(player,0x98)(player,&connection);
   if(!connection)throw std::runtime_error("PLAYER_CONNECTION");
   step(24);audio=std::make_shared<AudioOutput>();step(25);video.setAudio(player,std::static_pointer_cast<void>(audio));step(26);video.mute(player,true);
   step(27);Virtual<OptInt>(player,0xB8)(player,"app_state",0);
   // Public Definition SD=2; request configuration is applied before play/connect.
   // In set_definitions' unknown-platform path the SDK invokes the callback
   // unconditionally. A valid no-op avoids its explicit _Xbad_function_call.
   step(28);Virtual<SetDefinition>(live.get(),0x28)(live.get(),2,[](std::shared_ptr<SdkError>){});step(29);
  }catch(...){Release();throw;}
 }
 void RequestCapability()noexcept{
  if(capabilityRequested||!everConnected)return;capabilityRequested=true;
  auto cap=capability;cap->phase.store(1);
  try{
   auto factory=Resolve<MessageFactory>("?shared_mgr@IMessageMgr@iotvideo@@SA?AV?$shared_ptr@VIMessageMgr@iotvideo@@@std@@XZ");
   OpaquePtr manager=factory();if(!manager)throw std::runtime_error("MODEL_MANAGER");
   auto callback=c500g_message::MakeCallback<SdkError>(cap->numbers);
   callback->on_recv_data=[cap](std::vector<uint8_t> bytes,int64_t)noexcept->c500g_message::Result{
    // Only the exact camera0 bit is retained. Do not log/forward full model JSON.
    auto parsed=c500g_capability::Inspect(bytes.data(),bytes.size());
    const int mode=parsed.mode;
    cap->bytes.store(parsed.bytes);cap->parseReason.store(parsed.reason);cap->rootKind.store(parsed.rootKind);
    cap->pathDepth.store(parsed.pathDepth);cap->targetPresent.store(parsed.targetPresent);cap->targetKind.store(parsed.targetKind);
    cap->targetMode.store(parsed.targetMode);cap->bom.store(parsed.bom);cap->trailingNul.store(parsed.trailingNul);
    if(!bytes.empty())SecureZeroMemory(bytes.data(),bytes.size());
    cap->numbers->responses.fetch_add(1,std::memory_order_relaxed);
    cap->mode.store(mode);cap->phase.store(mode?2:3);
    return c500g_message::Result::Finished;
   };
   auto recordError=callback->on_error;
   callback->on_error=[cap,recordError](std::shared_ptr<SdkError> error)noexcept->c500g_message::Result{
    recordError(error);cap->phase.store(4);return c500g_message::Result::Finished;
   };
   // APK readDeviceInfo uses the empty model selector and SDK default timeout.
   // This public read operation leaves the LivePlayer's connection listener intact.
   Virtual<ReadProperty>(manager.get(),0x08)(manager.get(),deviceId,"",0,callback);
   cap->numbers->queued.fetch_add(1,std::memory_order_relaxed);
  }catch(...){cap->numbers->errors.fetch_add(1);cap->numbers->last_code.store(-601);cap->phase.store(4);}
 }
 void Configure(int config){if(started)throw std::runtime_error("PLAYER_ALREADY_STARTED");Virtual<OptInt>(player,0xB8)(player,"dev_func_cfg",static_cast<int64_t>(config));}
 int ObserveError(){
  std::shared_ptr<SdkError> observed;video.error(player,&observed);
  if(observed&&observed->code)error=observed->code;
  return error;
 }
 bool StopVideo()noexcept{
  bool ok=true;audioEnabled=false;
  try{if(player)video.mute(player,true);}catch(...){ok=false;}
  try{render.reset();}catch(...){ok=false;}
  return ok;
 }
 bool Release()noexcept{
  bool ok=StopVideo();
  if(player){try{Virtual<PlayerInt>(player,0x38)(player);}catch(...){ok=false;}}
  connection.reset();live.reset();player=nullptr;audio.reset();return ok;
 }
 ~Session(){Release();}
};
std::unordered_set<Session*> sessions;
Session* Checked(void* handle){auto* s=static_cast<Session*>(handle);return sessions.count(s)?s:nullptr;}
// C# treats negative return codes as failure. Preserve raw SDK codes in state.error,
// but never return a positive SDK failure as successful C ABI completion.
int FailureCode(int error,int fallback){return error>0?-error:(error?error:fallback);}
// Fixed numeric transport diagnostics only. Never write identifiers, tokens, packets or SDK text.
void RecordTransportPlatform(int platform) noexcept {
 static int last=-99;if(platform<0||platform>2)platform=-1;if(last==platform)return;last=platform;
 wchar_t folder[32768];DWORD n=GetEnvironmentVariableW(L"LOCALAPPDATA",folder,32768);if(!n||n>=32768)return;
 wchar_t suffix[96];swprintf_s(suffix,L"\\C500GDesktop\\ptz-transport-%lu.txt",GetCurrentProcessId());
 std::wstring path=folder;path+=suffix;
 HANDLE file=CreateFileW(path.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);if(file==INVALID_HANDLE_VALUE)return;
 char report[96];int size=sprintf_s(report,"bridge=modern2 platform=%d\r\n",platform);DWORD written=0;
 if(size>0)WriteFile(file,report,static_cast<DWORD>(size),&written,nullptr);CloseHandle(file);
}
void RecordPtzDiagnostics()noexcept{
 try{
  std::vector<Session*> ordered(sessions.begin(),sessions.end());
  std::sort(ordered.begin(),ordered.end(),[](Session* a,Session* b){return a->diagnosticIndex<b->diagnosticIndex;});
  std::string report="version=1.3.3 numeric_observations_only=1\r\n";
  for(auto* s:ordered){
   auto& t=*s->ptzNumbers;auto& c=*s->capability;auto& m=*c.numbers;char row[1024];
   int n=sprintf_s(row,"session=%u mode=%d modelphase=%d modelqueued=%u modelsent=%u modelresponse=%u modelerrors=%u modelcategory=%d modelcode=%d modelbytes=%u parsereason=%d rootkind=%d pathdepth=%d targetpresent=%d targetkind=%d targetmode=%d bom=%d trailingnul=%u transportqueued=%u transportsent=%u transportresponse=%u transporterrors=%u transporttimeouts=%u transportcategory=%d transportcode=%d\r\n",
    s->diagnosticIndex,c.mode.load(),c.phase.load(),m.queued.load(),m.sent.load(),m.responses.load(),m.errors.load(),m.last_category.load(),m.last_code.load(),
    c.bytes.load(),c.parseReason.load(),c.rootKind.load(),c.pathDepth.load(),c.targetPresent.load(),c.targetKind.load(),c.targetMode.load(),c.bom.load(),c.trailingNul.load(),
    t.queued.load(),t.sent.load(),t.responses.load(),t.errors.load(),t.timeouts.load(),t.last_category.load(),t.last_code.load());
   if(n>0)report.append(row,static_cast<size_t>(n));
  }
  static std::string previous;if(report==previous)return;
  wchar_t folder[32768];DWORD count=GetEnvironmentVariableW(L"LOCALAPPDATA",folder,32768);if(!count||count>=32768)return;
  std::wstring path=folder;path+=L"\\C500GDesktop";CreateDirectoryW(path.c_str(),nullptr);
  wchar_t suffix[96];swprintf_s(suffix,L"\\ptz-status-1.3.3-%lu.txt",GetCurrentProcessId());path+=suffix;
  HANDLE file=CreateFileW(path.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
  if(file==INVALID_HANDLE_VALUE)return;DWORD written=0;
  bool ok=WriteFile(file,report.data(),static_cast<DWORD>(report.size()),&written,nullptr)!=FALSE&&written==report.size();CloseHandle(file);
  if(ok)previous=std::move(report);
 }catch(...){}
}
struct RecordPtzOnExit {~RecordPtzOnExit(){RecordPtzDiagnostics();}};
int StateOf(Session* s,int* error){
 int link=initializer?initializer->link_state():-10;
 if(link>=3){*error=1000+link;return 7;}
 if(!s->started){
  if(link!=1){*error=0;return 2;}
  int rc=Virtual<PlayerInt>(s->player,0x28)(s->player);
  if(rc!=0){s->error=rc;*error=rc;return 7;}s->started=true;s->startedAt=GetTickCount64();
 }
 int state=sdk.state(s->connection.get());*error=s->ObserveError();
 if(state==4){s->everConnected=true;RecordTransportPlatform(sdk.platform(s->connection.get()));return 5;}
 if(state==6&&!s->everConnected&&!*error&&GetTickCount64()-s->startedAt<15000)return 2;
 if(state==5||state==6){if(!*error)*error=-201;return 7;}
 return 2;
}
}
API uint32_t __cdecl c500g_bridge_version(){return 0x00020000u;}
API int __cdecl c500g_local_smoke(int* initialState){
 if(!initialState)return -1;int step=0;
 try{std::lock_guard<std::mutex> lock(gate);step=10;EnsureApi();step=11;DisableLogging();
  // No initializer registration, connect(), or camera traffic in this function.
  Session local(1,&step);step=40;*initialState=sdk.state(local.connection.get());step=41;int platform=sdk.platform(local.connection.get());if(platform<0||platform>2)return -101;
  // Prepare the public model manager and callback ABI without issuing any read.
  step=42;auto modelFactory=Resolve<MessageFactory>("?shared_mgr@IMessageMgr@iotvideo@@SA?AV?$shared_ptr@VIMessageMgr@iotvideo@@@std@@XZ");
  OpaquePtr manager=modelFactory();if(!manager)return -1042;
  step=43;auto callback=c500g_message::MakeCallback<SdkError>(local.ptzNumbers);
  if(!Virtual<ReadProperty>(manager.get(),0x08)||!callback)return -1043;return 0;
 }catch(...){return -1000-step;}
}
API int __cdecl c500g_register(int64_t accessId,const uint8_t* token,int length){
 int validation=c500g::ValidateCredentials(accessId,token,length);if(validation)return validation;
 try{std::lock_guard<std::mutex> lock(gate);if(registered)return -2;EnsureApi();DisableLogging();
  if(!initializer)initializer=sdk.factory();if(!initializer)return -6;
  InitParams params;WipeStringOnExit wipe(params.access_token);params.access_id=c500g::SdkAccessId(accessId);params.access_token.assign(reinterpret_cast<const char*>(token),length);
  params.iv_host="|list.iotvideo.cloudlinks.cn";params.log_level=0;
  initializer->register_p2p(params);registered=true;return 0;
 }catch(...){return -100;}
}
API int __cdecl c500g_unregister(){
 try{std::lock_guard<std::mutex> lock(gate);if(!sessions.empty())return -8;
  if(registered&&initializer){initializer->unregister_p2p(true);registered=false;}return 0;
 }catch(...){return -100;}
}
API void* __cdecl c500g_open(uint64_t deviceId,int* result){
 if(result)*result=-1;if(!result||!deviceId)return nullptr;
 try{std::lock_guard<std::mutex> lock(gate);if(!registered){*result=-2;return nullptr;}
  std::unique_ptr<Session> s(new Session(deviceId));sessions.insert(s.get());*result=0;return s.release();
 }catch(...){*result=-100;return nullptr;}
}
API int __cdecl c500g_state(void* handle,int* state,int* error,int* received){
 if(!handle||!state||!error||!received)return -1;
 try{std::lock_guard<std::mutex> lock(gate);Session* s=Checked(handle);if(!s)return -1;
  *received=0;*state=StateOf(s,error);return 0;
 }catch(...){return -100;}
}
API int __cdecl c500g_send(void* handle,const uint8_t* data,int length){
 if(!handle||!data||length<=0||length>4096)return -1;
 try{std::lock_guard<std::mutex> lock(gate);Session* s=Checked(handle);if(!s)return -1;int error=0;
  if(StateOf(s,&error)!=5||error)return FailureCode(error,-4);
  // Original Android PTZ callback completes after send; it does not await a reply.
  SendDataArgs args;args.timeout=15000000;args.data.assign(data,data+length);
  args.callback=c500g_message::MakeCallback<SdkError>(s->ptzNumbers,c500g_message::CompletionPolicy::FinishOnSend);
  int rc=sdk.send(s->connection.get(),args);if(rc==0)c500g_message::RecordQueued(s->ptzNumbers);return rc>0?-rc:rc;
 }catch(...){return -100;}
}
API int __cdecl c500g_close(void* handle){
 if(!handle)return 0;
 try{std::lock_guard<std::mutex> lock(gate);Session* s=Checked(handle);if(!s)return -1;sessions.erase(s);bool ok=s->Release();delete s;return ok?0:-100;}catch(...){return -100;}
}
API int __cdecl c500g_configure(void* handle,int devConfig){
 if(!handle)return -1;
 try{std::lock_guard<std::mutex> lock(gate);Session* s=Checked(handle);if(!s)return -1;
  if(s->started)return -12;s->Configure(devConfig);return 0;
 }catch(...){return -100;}
}
API int __cdecl c500g_video_start(void* handle,uintptr_t hwnd){
 if(!handle||!hwnd||!IsWindow(reinterpret_cast<HWND>(hwnd)))return -1;
 try{std::lock_guard<std::mutex> lock(gate);Session* s=Checked(handle);if(!s)return -1;
  if(s->render&&s->render->parent==reinterpret_cast<HWND>(hwnd)&&!s->render->error.load())return 0;
  s->StopVideo();int error=0;int state=StateOf(s,&error);if(error||state==7)return FailureCode(error,-201);
  s->render.reset(new RenderHost(reinterpret_cast<HWND>(hwnd),s->player,s->live));
  int rc=s->render->Start();if(rc){s->render.reset();return rc;}return 0;
 }catch(...){return -100;}
}
API int __cdecl c500g_video_stop(void* handle){
 if(!handle)return -1;
 try{std::lock_guard<std::mutex> lock(gate);Session* s=Checked(handle);if(!s)return -1;return s->StopVideo()?0:-100;}catch(...){return -100;}
}
API int __cdecl c500g_video_state(void* handle,int* state,int* frames,int* error){
 if(!handle||!state||!frames||!error)return -1;
 *frames=-1;*state=0;*error=0;
 try{std::lock_guard<std::mutex> lock(gate);Session* s=Checked(handle);if(!s)return -1;RecordPtzOnExit record;
  if(!s->render){*state=5;return 0;}
  int connectionState=StateOf(s,error);int renderError=s->render->error.load();if(renderError)*error=renderError;
  int ps=video.state(s->player);
  if(*error||connectionState==7||ps==10){if(!*error)*error=-316;*state=7;return 0;}
  if(ps==5){*state=s->render->frameObserved.load()?3:2;return 0;}
  if(ps==6)*state=4;else if(ps==7||ps==8)*state=5;else if(ps==9)*state=6;else if(ps==3||ps==4)*state=2;else *state=1;
  return 0;
 }catch(...){*state=7;*error=-100;return -100;}
}
API int __cdecl c500g_video_audio(void* handle,int enabled,int volume){
 if(!handle||(enabled!=0&&enabled!=1)||volume<0||volume>100)return -1;
 try{std::lock_guard<std::mutex> lock(gate);Session* s=Checked(handle);if(!s||!s->audio)return -1;
  s->audioEnabled=enabled!=0;s->audioVolume=volume;int rc=s->audio->setVolume(static_cast<float>(volume)/100.0f);
  video.mute(s->player,!s->audioEnabled);return s->audioEnabled?rc:0;
 }catch(...){return -100;}
}

// Offline ABI exercise only: no registration, connection, playback, or device commands.
// The caller supplies its own blank test HWND; the worker creates its own child HWND.
API int __cdecl c500g_video_local_smoke(uintptr_t hwnd,int* playerState,int* frames){
 if(!hwnd||!playerState||!frames||!IsWindow(reinterpret_cast<HWND>(hwnd)))return -1;
 *playerState=-1;*frames=-1;int step=0;
 try{std::lock_guard<std::mutex> lock(gate);step=10;EnsureApi();step=11;DisableLogging();
  if(registered||!sessions.empty())return -8;
  Session local(1,&step);step=30;local.Configure(0);
  step=31;local.render.reset(new RenderHost(reinterpret_cast<HWND>(hwnd),local.player,local.live));
  step=32;int rc=local.render->Start();if(rc)return rc;
  step=33;video.mute(local.player,true);local.audio->setVolume(0.0f);
  step=34;*playerState=video.state(local.player);
  // No input frames exist in this path; exact decoded-frame count remains unknown.
  step=35;if(!local.StopVideo())return -315;
  step=36;return local.Release()?0:-1036;
 }catch(...){return -1000-step;}
}

API int __cdecl c500g_ptz_capability(void* handle,int* mode){
 if(!handle||!mode)return -1;*mode=0;
 try{std::lock_guard<std::mutex> lock(gate);Session* s=Checked(handle);if(!s)return -1;
  s->RequestCapability();*mode=s->capability->mode.load();return 0;
 }catch(...){return -100;}
}
