using System;
namespace C500GDesktop {
 internal sealed class VideoReconnectState {
  internal bool Wanted{get;private set;}
  internal DateTime? RetryAt{get;private set;}
  internal int Failures{get;private set;}
  private DateTime? playingSince;
  internal void Request(){Wanted=true;Failures=0;RetryAt=null;playingSince=null;}
  internal void Stop(){Wanted=false;Failures=0;RetryAt=null;playingSince=null;}
  internal void BeginAttempt(){Wanted=true;RetryAt=null;playingSince=null;}
  internal void Failed(DateTime now){
   playingSince=null;if(!Wanted)return;
   int seconds=Failures<4?2<<Failures:30;Failures=Math.Min(5,Failures+1);RetryAt=now.AddSeconds(seconds);
  }
  internal void Healthy(DateTime now){
   RetryAt=null;if(!playingSince.HasValue)playingSince=now;
   if((now-playingSince.Value).TotalSeconds>=30)Failures=0;
  }
  internal void NotPlaying(){playingSince=null;}
  internal bool IsDue(DateTime now){return Wanted&&RetryAt.HasValue&&now>=RetryAt.Value;}
 }
}
