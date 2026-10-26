using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using C500GDesktop;
class CloudOfflineTests
{
    static int checks;
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    static Dictionary<string,object> Json(string text) { return (Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(text); }
    static void Expect(string code, Action action)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch(CloudAccountException e) { Check(e.Code == code, code); Check(!e.Message.Contains("PRIVATE_SENTINEL"), "error sanitization"); }
    }
    static int Main()
    {
        Environment.SetEnvironmentVariable("C500G_APP_ID", "offline-fixture-app-id");
        Environment.SetEnvironmentVariable("C500G_APP_TOKEN", "offline-fixture-app-token");
        CloudAccountClient client = new CloudAccountClient();
        Dictionary<string,object> login = client.BuildLoginBody(" user@example.invalid ", "password");
        Check((string)login["pwd"] == "5F4DCC3B5AA765D61D8327DEB882CF99", "password MD5 uppercase matches original APK Java");
        Check(CloudAccountClient.PasswordDigest("P@ssw0rd!") == "8A24367A1F46C141048752F2D5BBD14B", "original Java symbols vector");
        Check(CloudAccountClient.PasswordDigest("비밀번호123!") == "D8053DAD03A86D14858EF18A23BE999D", "original Java Korean truncated UTF8 vector");
        Check(CloudAccountClient.PasswordDigest("🙂test") == "08C70AF3FE7AC04CDDC6A22E8D9B8945", "original Java supplementary UTF16 vector");
        Check(CloudAccountClient.PasswordDigest("A한B🙂C") == "8B4D3C5780B950F59E58ED5FF04E5B16", "original Java mixed Unicode vector");
        Check(CloudAccountClient.PasswordDigest("A\ud800B") == "439472DEB5E163696F83A9903770D363", "original Java malformed UTF16 replacement");
        Check(CloudAccountClient.PasswordDigest(new string('x',65)) == "1BC932052302D074BDEC39795FE00CF6", "original Java 65-byte block boundary");
        var unicodeRequest = client.BuildLoginBody("offline@example.invalid", "비밀번호123!");
        Check((string)unicodeRequest["pwd"] == "D8053DAD03A86D14858EF18A23BE999D", "login body uses APK password encoding");
        Check((string)login["email"] == "user@example.invalid" && (string)login["loginMode"] == "email", "email shape");
        Check((string)login["appVersion"] == "134283524" && (long)login["accessId"] == -1 && (int)login["terminalOS"] == 3, "APK field types and service version");
        Check((string)login["loginRegion"] == "" && (string)login["accessToken"] == "", "anonymous fields");
        var phone = client.BuildLoginBody("+82-01012345678", "offline-password");
        Check((string)phone["mobile"] == "01012345678" && (string)phone["mobileArea"] == "+82", "explicit phone format matches APK");
        var domestic = client.BuildLoginBody("010-1234-5678", "offline-password");
        Check((string)domestic["mobile"] == "01012345678", "domestic phone normalization");
        Expect("PHONE_FORMAT", delegate { client.BuildLoginBody("+8201012345678", "offline-password"); });
        Expect("ACCOUNT_FORMAT", delegate { client.BuildLoginBody("not-an-account", "offline-password"); });
        Expect("INPUT_REQUIRED", delegate { client.BuildLoginBody("user@example.invalid", ""); });
        string canonical = CloudAccountClient.Canonical("https://openapi.dophigo.com", Encoding.UTF8.GetBytes("{}"), "123", "456", "789", false);
        Check(canonical == "host:openapi.dophigo.com\npayload:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a\nx-iotvideo-accessid:123\nx-iotvideo-nonce:456\nx-iotvideo-timestamp:789", "independent SHA256 and canonical ordering vector");
        string fakeToken = "01" + new string('a',126);
        var parsed = Json("{\"code\":0,\"data\":{\"accessId\":\"123456\",\"accessToken\":\""+fakeToken+"\",\"userId\":\"7\",\"area\":\"AS\",\"regRegion\":\"KR\"}}");
        CloudAccountSession session = CloudAccountClient.ParseLogin(parsed);
        Check(session.AccessId == "123456" && session.AccessToken.Length == 128, "session uses complete token");
        Check(session.ToString() == "CloudAccountSession", "session string does not expose credentials");
        session.Dispose(); Check(session.AccessToken == null && session.AccessId == null && session.Devices.Count == 0, "dispose releases secret references");
        Expect("TOKEN_FORMAT", delegate { CloudAccountClient.ParseLogin(Json("{\"code\":0,\"data\":{\"accessId\":\"12\",\"accessToken\":\"PRIVATE_SENTINEL\"}}")); });
        Expect("SERVER_10902011", delegate { CloudAccountClient.CheckResult(Json("{\"code\":10902011,\"msg\":\"PRIVATE_SENTINEL\"}")); });
        Expect("SERVER_10902016", delegate { CloudAccountClient.CheckResult(Json("{\"code\":10902016,\"msg\":\"PRIVATE_SENTINEL\",\"accessToken\":\"PRIVATE_SENTINEL\"}")); });
        Expect("RESPONSE_INVALID", delegate { CloudAccountClient.CheckResult(Json("{\"msg\":\"PRIVATE_SENTINEL\"}")); });
        using(var cancel=new CancellationTokenSource())
        {
            cancel.Cancel();
            try { client.LoginAsync("unused@example.invalid","offline-password",cancel.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation not propagated"); }
            catch(OperationCanceledException) { Check(true,"pre-cancelled login makes no network request"); }
        }
        Check(CloudAccountClient.IsSupportedTokenStructure("01"+new string('a',126)), "legacy version1 token shape");
        Check(CloudAccountClient.IsSupportedTokenStructure("01"+new string('a',126)+"01200000"+new string('b',64)), "version1 password extension shape");
        Check(!CloudAccountClient.IsSupportedTokenStructure("01"+new string('a',126)+"011f0000"+new string('b',64)), "version1 short extension descriptor rejected");
        Check(!CloudAccountClient.IsSupportedTokenStructure("01"+new string('a',126)+"01200000"+new string('b',62)), "version1 truncated extension rejected");
        string version2 = "02" + new string('b',318);
        Check(CloudAccountClient.IsSupportedTokenStructure(version2), "version2 320-character minimum");
        Check(!CloudAccountClient.IsSupportedTokenStructure(version2.Substring(0,318)), "version2 truncated token rejected");
        string version3 = "03"+new string('c',126)+"02000000"+new string('d',384);
        Check(CloudAccountClient.IsSupportedTokenStructure(version3), "version3 little-endian count2");
        Check(!CloudAccountClient.IsSupportedTokenStructure(version3.Substring(0,version3.Length-2)), "version3 missing declared record rejected");
        Check(CloudAccountClient.IsSupportedTokenStructure("03"+new string('c',126)+"00000000"), "version3 native count0 accepted");
        Check(!CloudAccountClient.IsSupportedTokenStructure("03"+new string('c',126)+"09000000"+new string('d',1728)), "version3 count above8 rejected");
        Check(!CloudAccountClient.IsSupportedTokenStructure("03"+new string('c',126)+"ffffffff"), "version3 unsigned count overflow rejected");
        Check(!CloudAccountClient.IsSupportedTokenStructure("04"+new string('a',126)), "unknown token version rejected");
        Check(!CloudAccountClient.IsSupportedTokenStructure("01"+new string('a',127)), "odd token length rejected");
        Check(!CloudAccountClient.IsSupportedTokenStructure("01"+new string('a',125)+"g"), "nonhex token rejected");
        Check(!CloudAccountClient.IsSupportedTokenStructure("01"+new string('0',4096)), "client4096 character bound");
        var extended = CloudAccountClient.ParseLogin(Json("{\"code\":0,\"data\":{\"accessId\":\"42\",\"accessToken\":\""+version2+"\"}}"));
        Check(extended.AccessToken == version2 && extended.AccessToken.Length == 320, "extended token preserved without truncation");
        Check(extended.TokenFormatInfo == "type=string;chars=320;hex=true;even=true;version=2", "safe fixed metadata only");
        Check(CloudAccountClient.IsSafeTokenMetadata(extended.TokenFormatInfo), "metadata grammar allows generated shape");
        Check(!CloudAccountClient.IsSafeTokenMetadata(extended.TokenFormatInfo+";token=PRIVATE_SENTINEL"), "metadata grammar rejects appended secret fields");
        var rejectedDetail = new CloudAccountException("TEST", "safe", "PRIVATE_SENTINEL");
        Check(rejectedDetail.SafeDetail == "", "exception suppresses arbitrary diagnostics");
        try { CloudAccountClient.ParseLogin(Json("{\"code\":0,\"data\":{\"accessId\":\"42\",\"accessToken\":12345}}")); throw new Exception("numeric token accepted"); }
        catch(CloudAccountException e) { Check(e.SafeDetail == "type=number;chars=0;hex=false;even=true;version=unknown", "nonstring response gives metadata without value"); }
        // Synthetic session values only; request construction and signing do not contact a server.
        const string listPath = "/openapi/v2/user/device/list";
        var route = new CloudAccountSession();
        Check(CloudAccountClient.BuildRequestUri(listPath, route).Host == "openapi.dophigo.com", "null area uses root");
        route.Area = "";
        Check(CloudAccountClient.BuildRequestUri(listPath, route).Host == "openapi.dophigo.com", "empty area uses root");
        route.Area = "KR";
        Uri regional = CloudAccountClient.BuildRequestUri(listPath, route);
        Check(regional.AbsoluteUri == "https://openapi-kr.dophigo.com/openapi/v2/user/device/list", "authenticated device list uses regional host");
        Check(regional.Scheme == "https" && regional.Port == 443 && regional.UserInfo == "", "regional URI retains fixed TLS authority");
        Check(CloudAccountClient.BuildRequestUri("/openapi/app/user/login/account", route).Host == "openapi.dophigo.com", "login stays on root despite supplied session area");
        Check(CloudAccountClient.BuildRequestUri(listPath, null).Host == "openapi.dophigo.com", "no session uses root without lookup");
        string regionalCanonical = CloudAccountClient.Canonical(regional.AbsoluteUri, Encoding.UTF8.GetBytes("{}"), "123", "456", "789", false);
        Check(regionalCanonical == canonical.Replace("host:openapi.dophigo.com", "host:" + regional.Host), "canonical host matches actual regional request URI");
        Check(CloudNativeSigning.Sign(regionalCanonical, fakeToken) == "6Q8hZWvrj4BYA/9x4cjIygu7XWk=", "independent Python HMAC regional signature vector");
        route.Area = "kR";
        Check(CloudAccountClient.BuildRequestUri(listPath, route).AbsoluteUri == regional.AbsoluteUri, "DNS case normalization preserves routing");
        route.Area = "ap-northeast-2";
        Check(CloudAccountClient.BuildRequestUri(listPath, route).Host == "openapi-ap-northeast-2.dophigo.com", "hyphenated region remains one label");
        route.Area = new string('a',55);
        Check(CloudAccountClient.BuildRequestUri(listPath, route).Host.Split('.')[0].Length == 63, "regional DNS label length boundary");
        string[] invalidAreas = { "kr.example.invalid", "https://example.invalid", "kr:443", "kr@evil", "kr/", "kr%2e", "kr_1", " kr", "kr ", "-kr", "kr-", "한국", "kr\r\nX-Test:1", "kr\n", new string('a',56) };
        foreach (string invalidArea in invalidAreas)
        {
            route.Area = invalidArea;
            Expect("REGION_FORMAT", delegate { CloudAccountClient.BuildRequestUri(listPath, route); });
        }
        try { CloudAccountClient.BuildRequestUri("https://example.invalid/", route); throw new Exception("unlisted URI accepted"); }
        catch (InvalidOperationException) { Check(true, "request path whitelist rejects external URI"); }
        var device = CloudAccountClient.ParseDeviceInfo(Json(@"{""devId"":42,""remarkName"":""Lobby"",""relation"":1,""gwell"":{""devConfig"":513,""permission"":""view""}}"));
        Check(device.DeviceId == "42" && device.Name == "Lobby" && device.DevConfig == 513 && device.Permission == "view" && device.Relation == 1, "account device metadata reaches playback DTO");
        Check(CloudAccountClient.ParseDeviceInfo(Json(@"{""devId"":42}")).DevConfig == 0, "absent gwell defaults device flags to0");
        Check(CloudAccountClient.ParseDeviceInfo(Json(@"{""devId"":42,""gwell"":{}}")).DevConfig == 0, "absent devConfig defaults to0");
        Check(CloudAccountClient.ParseDeviceInfo(Json(@"{""devId"":42,""gwell"":{""devConfig"":2147483648}}")).DevConfig == Int32.MinValue, "unsigned bit31 preserved in native signed int");
        Check(CloudAccountClient.ParseDeviceInfo(Json(@"{""devId"":42,""gwell"":{""devConfig"":4294967295}}")).DevConfig == -1, "complete unsigned32 flag mask preserved");
        Check(CloudAccountClient.ParseDeviceInfo(Json(@"{""devId"":42,""gwell"":{""devConfig"":-2147483648}}")).DevConfig == Int32.MinValue, "signed32 lower boundary preserved");
        foreach (string raw in new[] { "null", "true", @"""513""", "513.0", "1.5", "{}", "[]", "4294967296", "-2147483649" })
            Check(CloudAccountClient.ParseDeviceInfo(Json(@"{""devId"":42,""gwell"":{""devConfig"":" + raw + "}}")).DevConfig == 0, "invalid or unbounded device flag metadata defaults to0");
        Check(CloudAccountClient.ParseDeviceInfo(Json(@"{""devId"":42,""status"":2,""properties"":2048,""gwell"":{""devConfig"":7}}")).DevConfig == 7, "server status and properties are not confused with playback options");
        Expect("RESPONSE_INVALID", delegate { CloudAccountClient.ParseDeviceInfo(Json(@"{""gwell"":{""devConfig"":513}}")); });
        route.Dispose();
        extended.Dispose();
        Console.WriteLine("All " + checks.ToString(CultureInfo.InvariantCulture) + " checks passed. No network requests made.");
        return 0;
    }
}
