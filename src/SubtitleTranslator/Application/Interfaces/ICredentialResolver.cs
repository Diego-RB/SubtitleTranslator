namespace SubtitleTranslator.Application.Interfaces;

public interface ICredentialResolver
{
    string? Resolve(string? credentialReference);

    string? Resolve(string? credentialReference, string? protectedCredential) =>
        string.IsNullOrWhiteSpace(protectedCredential)
            ? Resolve(credentialReference)
            : throw new InvalidOperationException("Protected credential resolution is not configured.");
}
