using System.Security.Cryptography;
using System.Text;

namespace SubtitleTranslator.Infrastructure.Security;

public static class DashboardAccessTokenValidator
{
    public static bool IsValid(string? suppliedToken, string? configuredToken)
    {
        if (string.IsNullOrEmpty(suppliedToken) || string.IsNullOrEmpty(configuredToken))
        {
            return false;
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredToken);
        return suppliedBytes.Length == configuredBytes.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
    }
}
