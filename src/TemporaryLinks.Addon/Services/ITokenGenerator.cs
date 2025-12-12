namespace TemporaryLinks.Addon.Services;

public interface ITokenGenerator
{
    string GenerateSecureToken(int length = 32);
}
