using SubtitleTranslator.Application.Interfaces;

namespace SubtitleTranslator.Infrastructure.Security;

public sealed class EnvironmentCredentialResolver : ICredentialResolver
{
    public string? Resolve(string? credentialReference)
    {
        return string.IsNullOrWhiteSpace(credentialReference)
            ? null
            : Environment.GetEnvironmentVariable(credentialReference);
    }
}
