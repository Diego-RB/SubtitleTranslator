namespace SubtitleTranslator.Application.Interfaces;

public interface ICredentialResolver
{
    string? Resolve(string? credentialReference);
}
