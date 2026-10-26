using System;
using System.Globalization;
namespace C500GDesktop {
 internal sealed class NativePtzException:InvalidOperationException {
  public int Code{get;private set;}
  public NativePtzException(int code):base(Description(code)+" (코드 "+code.ToString(CultureInfo.InvariantCulture)+")"){Code=code;}
  private static string Description(int code){
   if(code==-1101)return "계정 등록 단계에서 계정 식별 정보가 비어 있습니다.";
   if(code==-1102)return "계정 등록 단계에서 인증 토큰의 길이를 확인하지 못했습니다.";
   if(code==-1103)return "계정 등록 단계에서 인증 토큰의 문자 형식을 확인하지 못했습니다.";
   return "카메라 제어 모듈 요청에 실패했습니다.";
  }
 }
}
