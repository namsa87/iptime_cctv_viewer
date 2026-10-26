#pragma once
// Bounded JSON reader for one exact model field. It retains no whole JSON/config.
#include <cstddef>
#include <cstdint>
#include <string>
namespace c500g_capability {
// Numeric classifications only: never retain field names, scalar values or payload.
enum class Reason : int { NotRun=0, Resolved=1, Empty=2, TooLarge=3, NonObject=4,
 Syntax=5, MissingPath=6, InvalidStatus=7, DuplicatePath=8, DepthLimit=9,
 NodeLimit=10, StringLimit=11, TrailingData=12, Exception=13 };
// kind:0 absent,1 object,2 array,3 string,4 number,5 boolean,6 null,7 other.
struct Observation {
 int mode=0,reason=0,rootKind=0,pathDepth=0,targetPresent=0,targetKind=0,targetMode=0,bom=0;
 uint32_t bytes=0,trailingNul=0;
};
class Parser {
 const uint8_t* text;size_t length,pos=0;unsigned nodes=0;int found=0,result=0;
 Observation observation;
 bool Fail(Reason reason){if(!observation.reason)observation.reason=static_cast<int>(reason);return false;}
 static int Kind(uint8_t c){return c=='{'?1:c=='['?2:c=='"'?3:(c=='-'||(c>='0'&&c<='9'))?4:(c=='t'||c=='f')?5:c=='n'?6:7;}
 void Space(){while(pos<length&&(text[pos]==' '||text[pos]=='\r'||text[pos]=='\n'||text[pos]=='\t'))++pos;}
 bool Take(uint8_t c){Space();if(pos>=length||text[pos]!=c)return false;++pos;return true;}
 static int Hex(uint8_t c){return c>='0'&&c<='9'?c-'0':c>='a'&&c<='f'?c-'a'+10:c>='A'&&c<='F'?c-'A'+10:-1;}
 bool String(std::string* output){
  if(!Take('"'))return false;bool tooLong=false;
  while(pos<length){uint8_t c=text[pos++];if(c=='"')return tooLong?Fail(Reason::StringLimit):true;if(c<0x20)return false;
   if(c=='\\'){
    if(pos>=length)return false;c=text[pos++];
    if(c=='u'){unsigned value=0;for(int n=0;n<4;++n){if(pos>=length)return false;int h=Hex(text[pos++]);if(h<0)return false;value=value*16+h;}c=value<128?static_cast<uint8_t>(value):0xff;}
    else if(c=='b')c='\b';else if(c=='f')c='\f';else if(c=='n')c='\n';else if(c=='r')c='\r';else if(c=='t')c='\t';else if(c!='"'&&c!='\\'&&c!='/')return false;
   }
   if(output){if(output->size()<128)output->push_back(static_cast<char>(c));else tooLong=true;}
  }return false;
 }
 bool Number(size_t& begin,size_t& end){
  Space();begin=pos;if(pos<length&&text[pos]=='-')++pos;if(pos>=length)return false;
  if(text[pos]=='0')++pos;
  else {if(text[pos]<'1'||text[pos]>'9')return false;do{++pos;}while(pos<length&&text[pos]>='0'&&text[pos]<='9');}
  if(pos<length&&text[pos]=='.'){++pos;size_t p=pos;while(pos<length&&text[pos]>='0'&&text[pos]<='9')++pos;if(pos==p)return false;}
  if(pos<length&&(text[pos]=='e'||text[pos]=='E')){++pos;if(pos<length&&(text[pos]=='+'||text[pos]=='-'))++pos;size_t p=pos;while(pos<length&&text[pos]>='0'&&text[pos]<='9')++pos;if(pos==p)return false;}
  end=pos;return true;
 }
 bool Literal(const char* value){Space();for(size_t i=0;value[i];++i){if(pos>=length||text[pos++]!=static_cast<uint8_t>(value[i]))return false;}return true;}
 static bool Status(const uint8_t* p,size_t n,uint32_t& status){
  if(!n||n>11)return false;bool neg=p[0]=='-';size_t i=neg?1:0;if(i==n)return false;uint64_t v=0;
  for(;i<n;++i){if(p[i]<'0'||p[i]>'9')return false;v=v*10+(p[i]-'0');if(v>(neg?2147483648ull:4294967295ull))return false;}
  status=neg?static_cast<uint32_t>(0u-static_cast<uint32_t>(v)):static_cast<uint32_t>(v);return true;
 }
 bool Value(int path,unsigned depth){
  if(++nodes>32768)return Fail(Reason::NodeLimit);if(depth>48)return Fail(Reason::DepthLimit);
  if(path>observation.pathDepth)observation.pathDepth=path;
  Space();if(pos>=length)return false;
  if(path==5){
   ++found;observation.targetPresent=1;observation.targetKind=Kind(text[pos]);uint32_t status=0;bool ok=false;
   if(text[pos]=='"'){std::string n;if(!String(&n))return false;ok=Status(reinterpret_cast<const uint8_t*>(n.data()),n.size(),status);}
   else {size_t b=0,e=0;if(!Number(b,e))return Fail(Reason::InvalidStatus);ok=Status(text+b,e-b,status);}
   if(!ok)return Fail(Reason::InvalidStatus);result=(status&0x800u)?2:1;observation.targetMode=result;return true;
  }
  if(text[pos]=='{'){
   ++pos;Space();if(pos<length&&text[pos]=='}'){++pos;return true;}
   bool matched=false;const char* keys[]={"ProReadonly","devInfo","stVal","ptzInfo","id0_status"};
   while(true){std::string key;if(!String(&key)||!Take(':'))return false;int next=-1;
    if(path>=0&&path<5&&key==keys[path]){if(matched)return Fail(Reason::DuplicatePath);matched=true;next=path+1;}
    if(!Value(next,depth+1))return false;Space();if(pos>=length)return false;
    if(text[pos]=='}'){++pos;return true;}if(text[pos++]!=',')return false;
   }
  }
  if(text[pos]=='['){++pos;Space();if(pos<length&&text[pos]==']'){++pos;return true;}
   while(true){if(!Value(-1,depth+1))return false;Space();if(pos>=length)return false;if(text[pos]==']'){++pos;return true;}if(text[pos++]!=',')return false;}
  }
  if(text[pos]=='"')return String(nullptr);
  if(text[pos]=='t')return Literal("true");if(text[pos]=='f')return Literal("false");if(text[pos]=='n')return Literal("null");
  size_t b=0,e=0;return Number(b,e);
 }
public:
 Parser(const uint8_t* data,size_t size):text(data),length(size){}
 Observation Analyze(){
  observation.bytes=length>UINT32_MAX?UINT32_MAX:static_cast<uint32_t>(length);
  if(!text||!length){Fail(Reason::Empty);return observation;}
  if(length>1024*1024){Fail(Reason::TooLarge);return observation;}
  observation.bom=length>=3&&text[0]==0xef&&text[1]==0xbb&&text[2]==0xbf?1:0;
  for(size_t end=length;end&&text[end-1]==0;--end)++observation.trailingNul;
  Space();if(pos>=length){Fail(Reason::Empty);return observation;}
  observation.rootKind=Kind(text[pos]);
  if(text[pos]!='{'){Fail(Reason::NonObject);return observation;}
  if(!Value(0,0)){Fail(Reason::Syntax);return observation;}
  Space();if(pos!=length){Fail(Reason::TrailingData);return observation;}
  if(found!=1){Fail(Reason::MissingPath);return observation;}
  observation.mode=result;observation.reason=static_cast<int>(Reason::Resolved);return observation;
 }
};
inline Observation Inspect(const uint8_t* data,size_t length)noexcept{
 try{return Parser(data,length).Analyze();}catch(...){Observation result;result.reason=static_cast<int>(Reason::Exception);return result;}
}
inline int ExtractMode(const uint8_t* data,size_t length)noexcept{return Inspect(data,length).mode;}
}
