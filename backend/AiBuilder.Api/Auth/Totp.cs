using System.Security.Cryptography;
using OtpNet;
using QRCoder;

namespace AiBuilder.Api.Auth;

// RFC 6238: HMAC-SHA-1, 6 digits, 30s step. Verification accepts ±1 step
// to absorb client/server clock skew. Secret is 20 random bytes (160 bits)
// — RFC 6238's recommended size for SHA-1.
public static class Totp
{
    public const int SecretBytes = 6 * 4 - 4; // 20 bytes
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const string Issuer = "AiBuilder";

    public static string GenerateSecretBase32()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretBytes);
        return Base32Encoding.ToString(bytes);
    }

    public static string OtpAuthUri(string username, string secretBase32)
    {
        var label = Uri.EscapeDataString($"{Issuer}:{username}");
        var issuer = Uri.EscapeDataString(Issuer);
        return $"otpauth://totp/{label}?secret={secretBase32}&issuer={issuer}&digits={Digits}&period={StepSeconds}";
    }

    public static string QrPngDataUri(string otpAuthUri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(otpAuthUri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 6);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    // Returns true iff the code matches within ±1 step. Wrong-length input is
    // rejected without invoking the verifier — keeps brute-force noise out of
    // hot paths and avoids exception-based control flow on malformed Base32.
    public static bool Verify(string secretBase32, string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length != Digits) return false;
        for (int i = 0; i < code.Length; i++)
            if (code[i] < '0' || code[i] > '9') return false;

        byte[] key;
        try { key = Base32Encoding.ToBytes(secretBase32); }
        catch { return false; }

        var totp = new OtpNet.Totp(key, step: StepSeconds, mode: OtpHashMode.Sha1, totpSize: Digits);
        return totp.VerifyTotp(code, out _, new VerificationWindow(previous: 1, future: 1));
    }

    // Test hook: compute the code at a specific UTC instant. Used by SmokeTest
    // to drive boundary verification without sleeping.
    public static string ComputeAt(string secretBase32, DateTime utc)
    {
        var key = Base32Encoding.ToBytes(secretBase32);
        var totp = new OtpNet.Totp(key, step: StepSeconds, mode: OtpHashMode.Sha1, totpSize: Digits);
        return totp.ComputeTotp(utc);
    }
}
