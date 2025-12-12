using System.Security.Cryptography;

namespace TemporaryLinks.Addon.Services;

public class TokenGenerator : ITokenGenerator
{
    public string GenerateSecureToken(int length = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "")[..length];
    }
}
