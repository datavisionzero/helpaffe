using System.Security.Cryptography;
using System.Text;

namespace Helpaffe.Infrastructure.Identity;

public static class AccessToken
{
    public static (string Token, string Hash, string Prefix) Create(string kind)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"hf{kind}_{secret}";
        return (token, Hash(token), token[..12]);
    }

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
