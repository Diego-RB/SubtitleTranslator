using Microsoft.AspNetCore.DataProtection;
using SubtitleTranslator.Application.Interfaces;

namespace SubtitleTranslator.Infrastructure.Security;

public sealed class DataProtectionProviderApiKeyProtector(IDataProtectionProvider dataProtectionProvider)
    : IProviderApiKeyProtector
{
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector(
        "SubtitleTranslator.ProviderApiKey.v1");

    public string Protect(string apiKey) => protector.Protect(apiKey);

    public string Unprotect(string protectedApiKey) => protector.Unprotect(protectedApiKey);
}