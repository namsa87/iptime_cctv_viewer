using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace C500GDesktop
{
    public sealed class CloudAccountException : Exception
    {
        public string Code { get; private set; }
        public string SafeDetail { get; private set; }
        public CloudAccountException(string code, string koreanMessage) : this(code, koreanMessage, "") { }
        internal CloudAccountException(string code, string koreanMessage, string safeDetail) : base(koreanMessage)
        {
            Code = code;
            // Only a fixed token-shape grammar may cross the diagnostics boundary.
            SafeDetail = CloudAccountClient.IsSafeTokenMetadata(safeDetail) ? safeDetail : "";
        }
    }

    public sealed class CloudDeviceInfo
    {
        public string DeviceId { get; internal set; }
        public string Name { get; internal set; }
        public string Model { get; internal set; }
        public string Authorization { get; internal set; }
        public string Permission { get; internal set; }
        public int Relation { get; internal set; }
        public int DevConfig { get; internal set; }
        public string ProductId { get; internal set; }
        public AccountPtzProtocol PtzProtocol { get; internal set; }
    }

    public sealed class CloudAccountSession : IDisposable
    {
        public string AccessId { get; internal set; }
        public string AccessToken { get; internal set; }
        public string AccountId { get; internal set; }
        public IReadOnlyList<CloudDeviceInfo> Devices { get; internal set; }
        public string DeviceListErrorCode { get; internal set; }
        public string TokenFormatInfo { get; internal set; }
        internal string Area;
        internal string RegistrationRegion;
        internal string UniqueId;
        internal bool Disposed;
        internal CloudAccountSession() { Devices = new List<CloudDeviceInfo>().AsReadOnly(); }
        // Managed strings cannot be securely zeroed; release all secret references and never persist them.
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            AccessToken = null; AccessId = null; AccountId = null;
            Area = null; RegistrationRegion = null; UniqueId = null;
            foreach (CloudDeviceInfo device in Devices) device.Authorization = null;
            Devices = new List<CloudDeviceInfo>().AsReadOnly();
        }
        public override string ToString() { return "CloudAccountSession"; }
    }

    // Protocol reconstructed from the supplied com.cctv.iptime APK 01.04.02.
    // Service configuration is supplied locally. This client never saves account credentials.
    public sealed class CloudAccountClient
    {
        internal const string Host = "https://openapi.dophigo.com";
        internal const string AppVersion = "134283524"; // 08.01.01.04
        private static string AppId { get { return CloudServiceSettings.AppId; } }
        private static string AppToken { get { return CloudServiceSettings.AppToken; } }
        private const int MaxResponseBytes = 2 * 1024 * 1024;
        private const int TimeoutMilliseconds = 25000;
        private readonly string uniqueId = Guid.NewGuid().ToString("N");
        private readonly string region;
        private readonly string language;

        public CloudAccountClient()
        {
            // The ipTIME Android build defaults to Korean; use the Windows region when available.
            try { region = RegionInfo.CurrentRegion.TwoLetterISORegionName; }
            catch (ArgumentException) { region = "KR"; }
            if (!Regex.IsMatch(region ?? "", "^[A-Z]{2}$")) region = "KR";
            language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (!Regex.IsMatch(language ?? "", "^[a-z]{2,3}$")) language = "ko";
        }

        public async Task<CloudAccountSession> LoginAsync(string username, string password, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Dictionary<string, object> body = BuildLoginBody(username, password);
            password = null;
            CloudAccountSession session = null;
            try
            {
                Dictionary<string, object> response = await PostAsync("/openapi/app/user/login/account", body, null, token).ConfigureAwait(false);
                body.Clear();
                session = ParseLogin(response);
                session.UniqueId = uniqueId;
                response.Clear();
                try { session.Devices = await RefreshDevicesAsync(session, token).ConfigureAwait(false); }
                catch (CloudAccountException error)
                {
                    // Account authentication is usable for SDK registration even if listing is unavailable.
                    session.DeviceListErrorCode = error.Code;
                }
                token.ThrowIfCancellationRequested();
                return session;
            }
            catch
            {
                if (session != null) session.Dispose();
                throw;
            }
            finally { body.Clear(); }
        }

        public async Task<IReadOnlyList<CloudDeviceInfo>> RefreshDevicesAsync(CloudAccountSession session, CancellationToken token)
        {
            RequireSession(session);
            List<CloudDeviceInfo> devices = new List<CloudDeviceInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            long last = 0;
            // APK RemoteDeviceDataSource pages with the last devId until an empty/short page.
            // Bounds also protect against a server accidentally repeating a page forever.
            for (int page = 0; page < 100; page++)
            {
                token.ThrowIfCancellationRequested();
                Dictionary<string, object> body = CommonBody(session);
                body["lastDeviceId"] = last;
                Dictionary<string, object> response = await PostAsync("/openapi/v2/user/device/list", body, session, token).ConfigureAwait(false);
                Dictionary<string, object> data = GetData(response);
                object raw;
                if (!data.TryGetValue("deviceList", out raw)) throw InvalidResponse();
                IList list = raw as IList;
                if (list == null) throw InvalidResponse();
                if (list.Count == 0) return devices.AsReadOnly();
                long next = last;
                foreach (object item in list)
                {
                    Dictionary<string, object> value = item as Dictionary<string, object>;
                    if (value == null) throw InvalidResponse();
                    string id = Value(value, "devId");
                    long number;
                    if (!Int64.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number <= 0) throw InvalidResponse();
                    next = number;
                    if (!seen.Add(id)) continue;
                    if (devices.Count >= 5000) throw new CloudAccountException("DEVICE_LIMIT", "기기 목록이 허용된 크기를 초과했습니다.");
                    devices.Add(ParseDeviceInfo(value));
                }
                int size;
                Int32.TryParse(Value(data, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                response.Clear(); body.Clear();
                if (next == last || (size > 0 && list.Count < size)) return devices.AsReadOnly();
                last = next;
            }
            throw new CloudAccountException("DEVICE_PAGING", "기기 목록을 모두 가져오지 못했습니다. 잠시 후 다시 시도해 주세요.");
        }

        internal static CloudDeviceInfo ParseDeviceInfo(Dictionary<string, object> value)
        {
            if (value == null) throw InvalidResponse();
            string id = Value(value, "devId");
            long number;
            if (!Int64.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number <= 0)
                throw InvalidResponse();
            Dictionary<string, object> gw = Object(value, "gwell", false);
            Dictionary<string, object> saas = Object(value, "saas", false);
            string productId = saas == null ? "" : Value(saas,"productId");
            int relation;
            Int32.TryParse(Value(value, "relation"), NumberStyles.Integer, CultureInfo.InvariantCulture, out relation);
            object rawConfig = null;
            if (gw != null) gw.TryGetValue("devConfig", out rawConfig);
            return new CloudDeviceInfo { DeviceId = id, Name = DisplayText(Value(value, "remarkName"), 120),
                Model = "", Relation = relation, DevConfig = DeviceConfigBits(rawConfig),
                ProductId = productId, PtzProtocol = ResolveProductPtz(productId),
                Permission = gw == null ? "" : Value(gw, "permission"),
                Authorization = gw == null ? "" : Value(gw, "secretKey") };
        }

        internal static AccountPtzProtocol ResolveProductPtz(string productId)
        {
            // Supplied APK assets/appConfig.json: C300E uses Yoosee; C500G/C400G/GS/Outdoor use REOQOO.
            // The APK also defaults an absent product solution to REOQOO. Never infer this from device-ID size.
            return productId == "343599531164151" ? AccountPtzProtocol.GWell : AccountPtzProtocol.Reoqoo;
        }

        private static int DeviceConfigBits(object value)
        {
            // GWellModel declares a nullable Long; TMonitorPlayer consumes an int
            // dev_func_cfg option. Accept JSON integers only, preserving 32 bits.
            // Missing/malformed metadata keeps the APK's unavailable-info default0.
            if (value is int) return (int)value;
            if (!(value is long)) return 0;
            long number = (long)value;
            if (number < Int32.MinValue || number > UInt32.MaxValue) return 0;
            return unchecked((int)number);
        }

        internal Dictionary<string, object> BuildLoginBody(string username, string password)
        {
            username = (username ?? "").Trim();
            if (username.Length == 0 || username.Length > 254 || String.IsNullOrEmpty(password) || password.Length > 1024)
                throw new CloudAccountException("INPUT_REQUIRED", "ipTIME CCTV 계정과 비밀번호를 입력해 주세요.");
            if (username.IndexOf('\r') >= 0 || username.IndexOf('\n') >= 0)
                throw new CloudAccountException("INPUT_INVALID", "계정 형식을 확인해 주세요.");
            Dictionary<string, object> body = CommonBody(null);
            if (username.IndexOf('@') > 0)
            {
                body["email"] = username; body["loginMode"] = "email";
            }
            else
            {
                // Android's phone UI passes +country-number. Bare domestic numbers use Korea.
                string area = "+82";
                string mobile = username;
                if (username.StartsWith("+", StringComparison.Ordinal))
                {
                    int separator = username.IndexOf('-');
                    if (separator <= 1) throw new CloudAccountException("PHONE_FORMAT", "휴대전화 계정은 +82-01012345678 형식으로 입력해 주세요.");
                    area = username.Substring(0, separator);
                    mobile = username.Substring(separator + 1);
                }
                mobile = mobile.Replace("-", "").Replace(" ", "");
                if (!Regex.IsMatch(area, "^\\+[0-9]{1,4}$") || !Regex.IsMatch(mobile, "^[0-9]{5,20}$"))
                    throw new CloudAccountException("ACCOUNT_FORMAT", "이메일 또는 휴대전화 계정을 입력해 주세요. 예: +82-01012345678");
                body["mobile"] = mobile; body["mobileArea"] = area; body["loginMode"] = "mobile";
            }
            body["pwd"] = PasswordDigest(password);
            body["uniqueId"] = uniqueId;
            body["loginRegion"] = "";
            return body;
        }

        internal static string PasswordDigest(string password)
        {
            if (password == null) throw new ArgumentNullException("password");
            // Exact shipped APK behavior: MD5.getMD5ofStr calls md5Update(str.getBytes(),
            // str.length()), then byteHEX uses uppercase A-F. Android's default UTF8
            // encoder replaces malformed UTF16 with '?'. Do not hash all UTF8 bytes:
            // that changes the result for existing accounts with non-ASCII passwords.
            Encoding apkUtf8 = Encoding.GetEncoding("UTF-8", new EncoderReplacementFallback("?"), DecoderFallback.ExceptionFallback);
            byte[] bytes = apkUtf8.GetBytes(password);
            try
            {
                using (MD5 md5 = MD5.Create())
                    return BitConverter.ToString(md5.ComputeHash(bytes, 0, password.Length)).Replace("-", "");
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        private Dictionary<string, object> CommonBody(CloudAccountSession session)
        {
            Dictionary<string, object> body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["language"] = language;
            body["terminalOS"] = 3;
            body["accessToken"] = session == null ? "" : session.AccessToken.Substring(0, 96);
            body["pkgName"] = "com.cctv.iptime";
            // HttpUtils.getAppVersionParam returns String, not numeric JSON.
            body["appVersion"] = AppVersion;
            body["appName"] = "ipTIME CCTV_android";
            body["appId"] = AppId;
            body["appToken"] = AppToken;
            body["apiVersion"] = 2;
            body["platform"] = 4;
            body["channel"] = "googleplay";
            body["region"] = region;
            body["accessId"] = session == null ? -1L : Int64.Parse(session.AccessId, CultureInfo.InvariantCulture);
            body["funcSupport"] = 1L;
            if (session != null && !String.IsNullOrEmpty(session.RegistrationRegion)) body["regRegion"] = session.RegistrationRegion;
            return body;
        }

        private async Task<Dictionary<string, object>> PostAsync(string path, Dictionary<string, object> body, CloudAccountSession session, CancellationToken token)
        {
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeoutMilliseconds);
                try { return await Task.Run(delegate { return SendPost(path, body, session, deadline.Token); }, deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    throw new CloudAccountException("TIMEOUT", "계정 서버 응답 시간이 초과되었습니다. 잠시 후 다시 시도해 주세요.");
                }
            }
        }

        private Dictionary<string, object> SendPost(string path, Dictionary<string, object> body, CloudAccountSession session, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Uri requestUri = BuildRequestUri(path, session);
            string json = NewSerializer().Serialize(body);
            byte[] payload = Encoding.UTF8.GetBytes(json);
            json = null;
            HttpWebRequest request = null;
            try
            {
                bool anonymous = session == null;
                long timestamp = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                string[] anonymousKey = anonymous ? CloudNativeSigning.Anonymous(AppId + AppVersion, timestamp) : null;
                string accessId = anonymous ? anonymousKey[0] : session.AccessId;
                string nonce = NewNonce();
                string canonical = Canonical(requestUri.AbsoluteUri, payload, accessId, nonce, timestamp.ToString(CultureInfo.InvariantCulture), anonymous);
                string signature;
                if (anonymous)
                {
                    byte[] key = Encoding.UTF8.GetBytes(anonymousKey[1]);
                    try { using (HMACSHA1 hmac = new HMACSHA1(key)) signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))); }
                    finally { Array.Clear(key, 0, key.Length); Array.Clear(anonymousKey, 0, anonymousKey.Length); }
                }
                else signature = CloudNativeSigning.Sign(canonical, session.AccessToken);
                // Normal Windows certificate and hostname validation remains enabled.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                request = (HttpWebRequest)WebRequest.Create(requestUri);
                request.Method = "POST";
                request.AllowAutoRedirect = false;
                request.UseDefaultCredentials = false;
                request.PreAuthenticate = false;
                request.Timeout = TimeoutMilliseconds;
                request.ReadWriteTimeout = TimeoutMilliseconds;
                request.ContentType = "application/json; charset=utf-8";
                request.Accept = "application/json";
                request.UserAgent = "C500GDesktop/1.1";
                request.ContentLength = payload.Length;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.Headers["x-iotvideo-accessid"] = accessId;
                request.Headers["x-iotvideo-nonce"] = nonce;
                request.Headers["x-iotvideo-timestamp"] = timestamp.ToString(CultureInfo.InvariantCulture);
                request.Headers["x-iotvideo-area"] = anonymous ? "" : SafeHeader(session.Area);
                request.Headers["x-iotvideo-appver"] = AppVersion;
                request.Headers["x-iotvideo-appid"] = AppId;
                request.Headers["x-iotvideo-uniqueid"] = anonymous ? uniqueId : session.UniqueId;
                request.Headers["x-iotvideo-signature"] = signature;
                if (anonymous) request.Headers["x-iotvideo-anonymous"] = "anonymous";
                using (token.Register(delegate { request.Abort(); }))
                {
                    token.ThrowIfCancellationRequested();
                    using (Stream output = request.GetRequestStream()) output.Write(payload, 0, payload.Length);
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    {
                        int status = (int)response.StatusCode;
                        if (status >= 300 && status < 400) throw new CloudAccountException("HTTP_REDIRECT", "로그인 서버 주소가 변경되었습니다. 보안을 위해 자동 이동을 중단했습니다.");
                        if (status < 200 || status >= 300) throw HttpFailure(status);
                        if (response.ContentLength > MaxResponseBytes) throw InvalidResponse();
                        using (Stream input = response.GetResponseStream())
                        using (MemoryStream memory = new MemoryStream())
                        {
                            byte[] buffer = new byte[8192];
                            int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                token.ThrowIfCancellationRequested();
                                if (memory.Length + count > MaxResponseBytes) throw InvalidResponse();
                                memory.Write(buffer, 0, count);
                            }
                            token.ThrowIfCancellationRequested();
                            string text = Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
                            Dictionary<string, object> parsed;
                            try { parsed = NewSerializer().DeserializeObject(text) as Dictionary<string, object>; }
                            catch (ArgumentException) { throw InvalidResponse(); }
                            catch (InvalidOperationException) { throw InvalidResponse(); }
                            finally { Array.Clear(memory.GetBuffer(), 0, (int)memory.Length); text = null; }
                            CheckResult(parsed);
                            return parsed;
                        }
                    }
                }
            }
            catch (WebException error)
            {
                token.ThrowIfCancellationRequested();
                HttpWebResponse response = error.Response as HttpWebResponse;
                if (response != null) { int status = (int)response.StatusCode; response.Dispose(); throw HttpFailure(status); }
                if (error.Status == WebExceptionStatus.TrustFailure || error.Status == WebExceptionStatus.SecureChannelFailure)
                    throw new CloudAccountException("TLS_FAILED", "로그인 서버의 보안 연결을 확인하지 못했습니다. PC 날짜와 인증서를 확인해 주세요.");
                if (error.Status == WebExceptionStatus.Timeout)
                    throw new CloudAccountException("TIMEOUT", "로그인 서버 응답 시간이 초과되었습니다. 잠시 후 다시 시도해 주세요.");
                throw new CloudAccountException("NETWORK_FAILED", "로그인 서버에 연결하지 못했습니다. 인터넷 연결을 확인해 주세요.");
            }
            catch (IOException) { token.ThrowIfCancellationRequested(); throw new CloudAccountException("NETWORK_READ_FAILED", "로그인 서버 응답을 끝까지 받지 못했습니다."); }
            finally { Array.Clear(payload, 0, payload.Length); }
        }

        internal static Uri BuildRequestUri(string path, CloudAccountSession session)
        {
            if (path != "/openapi/app/user/login/account" && path != "/openapi/v2/user/device/list")
                throw new InvalidOperationException("Unsupported account request.");
            // WebsiteImpl resolves the stored account area; HostProvider adds its
            // suffix only when nonempty. Login always uses the fixed root host.
            string host = Host;
            if (path == "/openapi/v2/user/device/list" && session != null && !String.IsNullOrEmpty(session.Area))
            {
                string area = session.Area;
                // Keep all server-supplied text inside one DNS label, with fixed
                // HTTPS and domain. 55 + the eight-character openapi- prefix =63.
                // Do not trim invalid whitespace or accept a URL/port as an area.
                if (area.Length > 55 || !Regex.IsMatch(area, "\\A[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\\z"))
                    throw new CloudAccountException("REGION_FORMAT", "계정 서버의 지역 정보를 확인하지 못했습니다.");
                host = "https://openapi-" + area.ToLowerInvariant() + ".dophigo.com";
            }
            return new Uri(host + path, UriKind.Absolute);
        }

        internal static string Canonical(string host, byte[] payload, string accessId, string nonce, string timestamp, bool anonymous)
        {
            SortedDictionary<string, string> fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
            fields["host"] = new Uri(host).Host;
            using (SHA256 sha = SHA256.Create()) fields["payload"] = Hex(sha.ComputeHash(payload));
            fields["x-iotvideo-accessid"] = accessId;
            fields["x-iotvideo-nonce"] = nonce;
            fields["x-iotvideo-timestamp"] = timestamp;
            if (anonymous) { fields["x-iotvideo-appid"] = AppId; fields["x-iotvideo-appver"] = AppVersion; }
            StringBuilder result = new StringBuilder();
            foreach (KeyValuePair<string, string> field in fields)
            {
                if (result.Length > 0) result.Append('\n');
                result.Append(field.Key).Append(':').Append(field.Value);
            }
            return result.ToString();
        }

        internal static CloudAccountSession ParseLogin(Dictionary<string, object> response)
        {
            Dictionary<string, object> data = GetData(response);
            string accessId = Value(data, "accessId");
            object rawToken;
            data.TryGetValue("accessToken", out rawToken);
            string accessToken = rawToken as string;
            string tokenFormatInfo = TokenMetadata(rawToken);
            long number;
            if (!Int64.TryParse(accessId, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number == 0)
                throw new CloudAccountException("ACCESS_ID_INVALID", "로그인 응답의 계정 식별 정보를 확인하지 못했습니다.");
            if (!IsSupportedTokenStructure(accessToken))
                throw new CloudAccountException("TOKEN_FORMAT", "계정 인증 응답은 받았지만 토큰 구조를 확인하지 못했습니다.", tokenFormatInfo);
            // Retain the whole token. HTTP signs with its base section; the caller must
            // independently check the Windows SDK's capabilities before registration.
            return new CloudAccountSession { AccessId = accessId, AccessToken = accessToken, TokenFormatInfo = tokenFormatInfo,
                AccountId = Value(data, "userId"), Area = SafeHeader(Value(data, "area")),
                RegistrationRegion = DisplayText(Value(data, "regRegion"), 16) };
        }

        private const int MaximumAcceptedTokenCharacters = 4096; // Client policy, not an Android native limit.

        internal static bool IsSafeTokenMetadata(string detail)
        {
            return detail != null && Regex.IsMatch(detail, "\\Atype=(string|null|number|boolean|array|object);chars=[0-9]{1,7};hex=(true|false);even=(true|false);version=(1|2|3|unknown)\\z");
        }

        internal static string TokenMetadata(object raw)
        {
            string value = raw as string;
            string type = value != null ? "string" : raw == null ? "null" : raw is bool ? "boolean" :
                raw is IList ? "array" : raw is int || raw is long || raw is decimal || raw is double ? "number" : "object";
            int length = value == null ? 0 : value.Length;
            bool hex = IsHex(value);
            int version = hex && length >= 2 ? HexByte(value, 0) : -1;
            string versionText = version >= 1 && version <= 3 ? version.ToString(CultureInfo.InvariantCulture) : "unknown";
            return "type=" + type + ";chars=" + length.ToString(CultureInfo.InvariantCulture) +
                ";hex=" + (hex ? "true" : "false") + ";even=" + ((length & 1) == 0 ? "true" : "false") + ";version=" + versionText;
        }

        internal static bool IsSupportedTokenStructure(string token)
        {
            if (token == null || token.Length < 128 || token.Length > MaximumAcceptedTokenCharacters ||
                (token.Length & 1) != 0 || !IsHex(token)) return false;
            int version = HexByte(token, 0);
            if (version == 1)
            {
                if (token.Length == 128) return true;
                // Byte64 is the extension flag. A nonzero flag's descriptor must have
                // at least32 bytes and bytes68..99 must exist before native AES use.
                if (HexByte(token, 128) == 0) return true;
                return token.Length >= 200 && HexByte(token, 130) >= 32;
            }
            if (version == 2) return token.Length >= 320;
            if (version == 3)
            {
                if (token.Length < 136) return false;
                uint count = (uint)HexByte(token, 128) | ((uint)HexByte(token, 130) << 8) |
                    ((uint)HexByte(token, 132) << 16) | ((uint)HexByte(token, 134) << 24);
                return count <= 8 && token.Length >= 136L + 192L * count;
            }
            return false;
        }

        private static bool IsHex(string value)
        {
            if (String.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
        private static int HexByte(string value, int index)
        {
            return (HexDigit(value[index]) << 4) | HexDigit(value[index + 1]);
        }
        private static int HexDigit(char c) { return c <= '9' ? c - '0' : (c | 32) - 'a' + 10; }

        internal static void CheckResult(Dictionary<string, object> root)
        {
            if (root == null) throw InvalidResponse();
            int code;
            if (!Int32.TryParse(Value(root, "code"), NumberStyles.Integer, CultureInfo.InvariantCulture, out code)) throw InvalidResponse();
            // APK HttpErrUtils maps 10902011 to AA0013 (account/password error).
            if (code == 10902011)
                throw new CloudAccountException("SERVER_10902011", "계정 또는 비밀번호 인증에 실패했습니다. ipTIME CCTV 앱의 계정 정보를 확인해 주세요. (코드 10902011)");
            if (code != 0)
                // Never surface raw server text: it could echo a credential or token.
                throw new CloudAccountException("SERVER_" + code.ToString(CultureInfo.InvariantCulture),
                    "계정 서버가 요청을 거부했습니다. 계정 정보를 확인해 주세요. (코드 " + code.ToString(CultureInfo.InvariantCulture) + ")");
        }
        private static Dictionary<string, object> GetData(Dictionary<string, object> root)
        {
            CheckResult(root);
            return Object(root, "data", true);
        }
        private static Dictionary<string, object> Object(Dictionary<string, object> value, string key, bool required)
        {
            object data;
            Dictionary<string, object> result = value != null && value.TryGetValue(key, out data) ? data as Dictionary<string, object> : null;
            if (required && result == null) throw InvalidResponse();
            return result;
        }
        private static string Value(Dictionary<string, object> value, string key)
        {
            object data;
            if (value == null || !value.TryGetValue(key, out data) || data == null) return "";
            if (data is string || data is int || data is long || data is decimal) return Convert.ToString(data, CultureInfo.InvariantCulture);
            return "";
        }
        private static void RequireSession(CloudAccountSession session)
        {
            if (session == null || session.Disposed || String.IsNullOrEmpty(session.AccessToken))
                throw new CloudAccountException("SESSION_CLOSED", "계정에 다시 로그인해 주세요.");
        }
        private static string SafeHeader(string value)
        {
            value = value ?? "";
            if (value.Length > 128 || !Regex.IsMatch(value, "^[A-Za-z0-9_.:-]*$")) throw InvalidResponse();
            return value;
        }
        private static string DisplayText(string value, int max)
        {
            value = Regex.Replace(value ?? "", "[\\p{Cc}\\p{Cf}]", "");
            return value.Length > max ? value.Substring(0, max) : value;
        }
        private static string NewNonce()
        {
            byte[] bytes = new byte[4];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return (BitConverter.ToUInt32(bytes, 0) & 0x7fffffffU).ToString(CultureInfo.InvariantCulture);
        }
        private static JavaScriptSerializer NewSerializer() { return new JavaScriptSerializer { MaxJsonLength = MaxResponseBytes, RecursionLimit = 32 }; }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        private static CloudAccountException InvalidResponse() { return new CloudAccountException("RESPONSE_INVALID", "계정 서버의 응답 형식을 확인하지 못했습니다."); }
        private static CloudAccountException HttpFailure(int status)
        {
            return new CloudAccountException("HTTP_" + status.ToString(CultureInfo.InvariantCulture), "계정 서버 연결이 거부되었습니다. (HTTP " + status.ToString(CultureInfo.InvariantCulture) + ")");
        }
    }
}
