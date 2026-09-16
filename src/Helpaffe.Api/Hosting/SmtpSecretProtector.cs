using System.Security.Cryptography;
using System.Text;

namespace Helpaffe.Api.Hosting;

public sealed class SmtpSecretProtector
{
    private readonly byte[] _key;

    private SmtpSecretProtector(byte[] key) => _key = key;

    public static SmtpSecretProtector? FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration["Secrets:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(configured)) return null;
        try
        {
            var key = Convert.FromBase64String(configured);
            return key.Length == 32 ? new SmtpSecretProtector(key) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var source = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[source.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, source, ciphertext, tag);
        var payload = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
        payload[0] = 1;
        nonce.CopyTo(payload, 1);
        tag.CopyTo(payload, 13);
        ciphertext.CopyTo(payload, 29);
        CryptographicOperations.ZeroMemory(source);
        return Convert.ToBase64String(payload);
    }

    public string Unprotect(string protectedValue)
    {
        var payload = Convert.FromBase64String(protectedValue);
        if (payload.Length < 29 || payload[0] != 1) throw new CryptographicException("The SMTP secret format is invalid.");
        var plaintext = new byte[payload.Length - 29];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(payload.AsSpan(1, 12), payload.AsSpan(29), payload.AsSpan(13, 16), plaintext);
        var value = Encoding.UTF8.GetString(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        return value;
    }
}
