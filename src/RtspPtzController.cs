using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace C500GDesktop {
 // This device's SET_PARAMETER / Content-type: ptzCmd protocol was confirmed by the owner.
 public sealed class RtspPtzController:IPtzController {
  private readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
  private TcpClient tcp;private NetworkStream stream;private string identity;private string challenge;
  private int sequence,nonceCount,epoch;private bool disposed;
  private string status="RTSP PTZ · 카메라 암호로 인증";
  public bool IsReady{get{return !disposed;}} public string Status{get{return status;}}
  public Task MoveAsync(CameraProfile camera,PtzDirection direction,CancellationToken token) {
   return MoveCore(camera,direction,token,null);
  }
  private async Task MoveCore(CameraProfile camera,PtzDirection direction,CancellationToken token,int? expectedEpoch) {
   if(disposed)throw new ObjectDisposedException("RtspPtzController");
   if(camera==null)throw new ArgumentNullException("camera");
   if(!camera.PtzEnabled&&direction!=PtzDirection.Stop)throw new InvalidOperationException("이 카메라에서 PTZ 사용을 켜 주세요.");
   await gate.WaitAsync(token).ConfigureAwait(false);
   Exception failure=null;int requestEpoch=0;bool attempted=false;
   try {
    token.ThrowIfCancellationRequested();
    if(disposed)throw new ObjectDisposedException("RtspPtzController");
    if(expectedEpoch.HasValue&&expectedEpoch.Value!=Volatile.Read(ref epoch))return;
    requestEpoch=Interlocked.Increment(ref epoch);
    using(CancellationTokenSource deadline=CancellationTokenSource.CreateLinkedTokenSource(token)) {
     deadline.CancelAfter(3500);attempted=true;
     await Task.Run(()=>Send(camera,direction,deadline.Token),deadline.Token).ConfigureAwait(false);
    }
    status=direction==PtzDirection.Stop?"RTSP PTZ · 정지 명령 응답 확인":"RTSP PTZ · 이동 명령 응답 확인";
   }catch(Exception ex) {
    CloseConnection();failure=ex;
    status=direction==PtzDirection.Stop?"정지 응답 없음 · 정지 버튼을 다시 눌러 주세요":"PTZ 연결 실패 · 정지 요청 중";
   }finally{gate.Release();}
   if(failure!=null) {
    // No timed movement limit. A failed/cancelled command still gets a best-effort STOP.
    if(attempted&&direction!=PtzDirection.Stop&&!disposed) {
     try{using(CancellationTokenSource recovery=new CancellationTokenSource(3500))await MoveCore(camera,PtzDirection.Stop,recovery.Token,requestEpoch).ConfigureAwait(false);}
     catch{status="정지 응답 없음 · 정지 버튼을 다시 눌러 주세요";}
    }
    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
   }
  }
  private static string Md5(string text){using(MD5 hash=MD5.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","").ToLowerInvariant();}
  private static string Parameter(string header,string key){
   Match m=Regex.Match(header,"(?:^|[ ,])"+Regex.Escape(key)+"=(?:\"([^\"]*)\"|([^,\\s]+))",RegexOptions.IgnoreCase);
   return !m.Success?null:m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value;
  }
  private static string Quoted(string value){if((value??"").IndexOfAny(new[]{'\r','\n'})>=0)throw new ArgumentException("Invalid header");return "\""+(value??"").Replace("\\","\\\\").Replace("\"","\\\"")+"\"";}
  private string Digest(CameraProfile c,string method,string uri) {
   string realm=Parameter(challenge,"realm"),nonce=Parameter(challenge,"nonce"),qop=Parameter(challenge,"qop"),algorithm=Parameter(challenge,"algorithm"),opaque=Parameter(challenge,"opaque");
   if(realm==null||nonce==null||(algorithm!=null&&!algorithm.Equals("MD5",StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Unsupported Digest authentication");
   bool useQop=qop!=null;if(useQop&&!Array.Exists(qop.Split(','),s=>s.Trim()=="auth"))throw new InvalidOperationException("Unsupported Digest qop");
   string nc=(++nonceCount).ToString("x8"),cnonce=Guid.NewGuid().ToString("N"),ha1=Md5(c.Username+":"+realm+":"+c.GetPassword()),ha2=Md5(method+":"+uri);
   string response=useQop?Md5(ha1+":"+nonce+":"+nc+":"+cnonce+":auth:"+ha2):Md5(ha1+":"+nonce+":"+ha2);
   return "Digest username="+Quoted(c.Username)+", realm="+Quoted(realm)+", nonce="+Quoted(nonce)+", uri="+Quoted(uri)+", response="+Quoted(response)+(useQop?", qop=auth, nc="+nc+", cnonce="+Quoted(cnonce):"")+(opaque==null?"":", opaque="+Quoted(opaque));
  }
  private sealed class Reply {public int Code;public Dictionary<string,string> Headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);}
  private Reply Exchange(CameraProfile camera,string method,string uri,string extra,CancellationToken token) {
   token.ThrowIfCancellationRequested();int seq=++sequence;
   string packet=method+" "+uri+" RTSP/1.0\r\nCSeq: "+seq+"\r\nUser-Agent: C500GDesk/1.0\r\n"+(challenge==null?"":"Authorization: "+Digest(camera,method,uri)+"\r\n")+extra+"\r\n";
   byte[] request=Encoding.UTF8.GetBytes(packet);stream.Write(request,0,request.Length);
   List<byte> buffer=new List<byte>();int matched=0;byte[] delimiter={13,10,13,10};
   while(buffer.Count<16384){token.ThrowIfCancellationRequested();int b=stream.ReadByte();if(b<0)throw new EndOfStreamException();buffer.Add((byte)b);matched=b==delimiter[matched]?matched+1:b==13?1:0;if(matched==4)break;}
   if(matched!=4)throw new InvalidDataException("RTSP header too large");
   string[] lines=Encoding.UTF8.GetString(buffer.ToArray()).Split(new[]{"\r\n"},StringSplitOptions.None);
   string[] statusLine=lines[0].Split(' ');Reply reply=new Reply();if(statusLine.Length<2||statusLine[0]!="RTSP/1.0"||!Int32.TryParse(statusLine[1],out reply.Code))throw new InvalidDataException("Invalid RTSP response");
   foreach(string line in lines){int colon=line.IndexOf(':');if(colon>0)reply.Headers[line.Substring(0,colon).Trim()]=line.Substring(colon+1).Trim();}
   string receivedSequence;if(!reply.Headers.TryGetValue("CSeq",out receivedSequence)||receivedSequence!=seq.ToString())throw new InvalidDataException("RTSP sequence mismatch");
   string length;int count=0;if(reply.Headers.TryGetValue("Content-Length",out length)&&(!Int32.TryParse(length,out count)||count<0||count>1048576))throw new InvalidDataException("Invalid RTSP body size");
   byte[] discard=new byte[4096];while(count>0){token.ThrowIfCancellationRequested();int n=stream.Read(discard,0,Math.Min(count,discard.Length));if(n==0)throw new EndOfStreamException();count-=n;}
   return reply;
  }
  private Reply Authenticated(CameraProfile camera,string method,string uri,string extra,CancellationToken token) {
   Reply reply=Exchange(camera,method,uri,extra,token);
   if(reply.Code==401){string value;if(!reply.Headers.TryGetValue("WWW-Authenticate",out value)||!value.StartsWith("Digest ",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Digest authentication required");challenge=value;nonceCount=0;reply=Exchange(camera,method,uri,extra,token);}
   if(reply.Code!=200)throw new InvalidOperationException("RTSP request failed: "+reply.Code.ToString());return reply;
  }
  private void Send(CameraProfile camera,PtzDirection direction,CancellationToken token) {
   string error=camera.Validate();if(error!=null)throw new ArgumentException(error);
   string host=camera.Host.Trim().Trim('[',']'),uri="rtsp://"+(host.Contains(":")?"["+host+"]":host)+":"+camera.Port+camera.StreamPath;
   string key=uri+"|"+camera.Username+"|"+camera.ProtectedPassword;
   if(tcp==null||key!=identity){
    CloseConnection();tcp=new TcpClient();tcp.NoDelay=true;tcp.ReceiveTimeout=2500;tcp.SendTimeout=2500;
    using(token.Register(()=>{try{tcp.Close();}catch{}})) {
     IAsyncResult connecting=tcp.BeginConnect(host,camera.Port,null,null);
     using(WaitHandle wait=connecting.AsyncWaitHandle){if(!wait.WaitOne(2500))throw new TimeoutException();}
     token.ThrowIfCancellationRequested();tcp.EndConnect(connecting);
    }
    stream=tcp.GetStream();identity=key;
    using(token.Register(()=>{try{tcp.Close();}catch{}}))Authenticated(camera,"DESCRIBE",uri,"Accept: application/sdp\r\n",token);
   }
   // Vendor RTSP spells DOWN as DWON. Do not normalize this wire command.
   string command=direction==PtzDirection.Up?"UP":direction==PtzDirection.Down?"DWON":direction==PtzDirection.Left?"LEFT":direction==PtzDirection.Right?"RIGHT":"STOP";
   using(token.Register(()=>{try{tcp.Close();}catch{}}))Authenticated(camera,"SET_PARAMETER",uri,"Content-type: ptzCmd: "+command+"\r\n",token);
  }
  private void CloseConnection(){if(tcp!=null)tcp.Close();tcp=null;stream=null;identity=null;challenge=null;sequence=nonceCount=0;}
  public void Dispose(){disposed=true;Interlocked.Increment(ref epoch);CloseConnection();}
 }
}
