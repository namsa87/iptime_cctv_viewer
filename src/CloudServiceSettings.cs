using System;
namespace C500GDesktop {
 internal static class CloudServiceSettings {
  internal static string AppId { get { return Required("C500G_APP_ID"); } }
  internal static string AppToken { get { return Required("C500G_APP_TOKEN"); } }
  private static string Required(string name) {
   string value=Environment.GetEnvironmentVariable(name);
   if(String.IsNullOrWhiteSpace(value)||value.Length>4096||value.IndexOfAny(new[]{'\r','\n','\0'})>=0)
    throw new CloudAccountException("APP_CONFIG_REQUIRED", "서비스 연동 설정이 없습니다. 로컬 환경변수 C500G_APP_ID와 C500G_APP_TOKEN을 설정해 주세요. 자세한 내용은 readme.txt를 확인해 주세요.");
   return value.Trim();
  }
 }
}
