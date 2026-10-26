using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace C500GDesktop
{
    // Reconstructed from the user-supplied APK's libiotvideomulti.so.
    // Local reverse-engineering evidence is excluded from the public source package.
    // No account credentials or tokens are stored by this helper.
    internal static class CloudNativeSigning
    {
        private const string Base32Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        internal static string[] Anonymous(string appTag, long epochSeconds)
        {
            byte[] random = new byte[1];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            return Anonymous(appTag, epochSeconds, random[0] & 31);
        }

        // Fixed nonce overload is solely useful for reproducible protocol verification.
        internal static string[] Anonymous(string appTag, long epochSeconds, int nonce)
        {
            if (String.IsNullOrEmpty(appTag)) throw new ArgumentException("Application tag is required.", "appTag");
            if (appTag.IndexOf('\0') >= 0) throw new ArgumentException("Application tag contains a null character.", "appTag");
            if (epochSeconds < 0 || epochSeconds > UInt32.MaxValue) throw new ArgumentOutOfRangeException("epochSeconds");
            if (nonce < 0 || nonce > 31) throw new ArgumentOutOfRangeException("nonce");
            byte[] tag = ModifiedUtf8(appTag);
            uint hash = 0;
            unchecked { foreach (byte c in tag) hash = hash * 131U + c; }
            ulong id = 0xe000000000000000UL | ((ulong)(hash & 0xffffffU) << 32) |
                ((ulong)(uint)epochSeconds / 60UL << 5) | (uint)nonce;
            byte[] packed = new byte[8];
            for (int i = 0; i < 8; i++) packed[i] = (byte)(id >> (i * 8));
            // Native xor_encrypt(id_bytes+1,6,NULL,0), sequential in-place.
            for (int i = 0; i < 6; i++) packed[i + 1] ^= packed[((i + 1) % 6) + 1];
            id = 0;
            for (int i = 0; i < 8; i++) id |= (ulong)packed[i] << (i * 8);
            ulong encoded = id ^ 0x7e18fc2d035a4b69UL;
            StringBuilder digits = new StringBuilder(13);
            for (int shift = 60; shift >= 0; shift -= 5)
            {
                int digit = (int)((encoded >> shift) & 31UL);
                if (digit != 0 || digits.Length != 0) digits.Append(Base32Alphabet[digit]);
            }
            byte[] md5;
            using (HMACMD5 hmac = new HMACMD5(tag)) md5 = hmac.ComputeHash(Encoding.ASCII.GetBytes(digits.ToString()));
            return new string[] { id.ToString(CultureInfo.InvariantCulture), Hex(md5) };
        }

        internal static string Sign(string canonical, string fullToken)
        {
            if (canonical == null) throw new ArgumentNullException("canonical");
            if (fullToken == null || fullToken.Length < 128)
                throw new ArgumentException("The complete 128-character access token is required.", "fullToken");
            byte[] token = new byte[64];
            for (int i = 0; i < token.Length; i++)
                token[i] = (byte)((Nibble(fullToken[i * 2]) << 4) | Nibble(fullToken[i * 2 + 1]));
            byte[] key = new byte[16];
            Buffer.BlockCopy(token, 48, key, 0, key.Length);
            byte[] message = ModifiedUtf8(canonical);
            try
            {
                using (HMACSHA1 hmac = new HMACSHA1(key))
                    // The shipped JNI calls GetStringLength, not GetStringUTFLength.
                    return Convert.ToBase64String(hmac.ComputeHash(message, 0, canonical.Length));
            }
            finally { Array.Clear(token, 0, token.Length); Array.Clear(key, 0, key.Length); }
        }

        private static int Nibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            throw new ArgumentException("Access token is not hexadecimal.", "fullToken");
        }
        private static string Hex(byte[] value)
        {
            StringBuilder text = new StringBuilder(value.Length * 2);
            foreach (byte b in value) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }
        private static byte[] ModifiedUtf8(string value)
        {
            // JNI modified UTF-8 encodes each UTF-16 code unit independently.
            byte[] output = new byte[value.Length * 3]; int count = 0;
            foreach (char c in value)
            {
                if (c > 0 && c <= 0x7f) output[count++] = (byte)c;
                else if (c <= 0x7ff)
                { output[count++] = (byte)(0xc0 | (c >> 6)); output[count++] = (byte)(0x80 | (c & 63)); }
                else
                { output[count++] = (byte)(0xe0 | (c >> 12)); output[count++] = (byte)(0x80 | ((c >> 6) & 63)); output[count++] = (byte)(0x80 | (c & 63)); }
            }
            Array.Resize(ref output, count); return output;
        }
    }
}
