namespace SubtitleTranslator.Application.Interfaces;

public interface IProviderApiKeyProtector
{
    string Protect(string apiKey);
    string Unprotect(string protectedApiKey);
}