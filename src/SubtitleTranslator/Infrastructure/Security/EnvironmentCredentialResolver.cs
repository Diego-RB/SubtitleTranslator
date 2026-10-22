using System.Security.Cryptography;
using SubtitleTranslator.Application.Interfaces;

namespace SubtitleTranslator.Infrastructure.Security;

public sealed class EnvironmentCredentialResolver(IProviderApiKeyProtector apiKeyProtector) : ICredentialResolver
{
    public string? Resolve(string? credentialReference) => Resolve(credentialReference, null);

    public string? Resolve(string? credentialReference, string? protectedCredential)
    {
        if (!string.IsNullOrWhiteSpace(protectedCredential))
        {
            try
            {
                return apiKeyProtector.Unprotect(protectedCredential);
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("Stored provider credential could not be decrypted.");
            }
        }

        return string.IsNullOrWhiteSpace(credentialReference)
            ? null
            : Environment.GetEnvironmentVariable(credentialReference);
    }
}
