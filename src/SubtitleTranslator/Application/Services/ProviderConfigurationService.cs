using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Application.Services;

public sealed class ProviderConfigurationService(
    SubtitleTranslatorDbContext db,
    IProviderApiKeyProtector? apiKeyProtector = null)
{
    public Task<List<Provider>> ListAsync(CancellationToken cancellationToken = default) =>
        db.Providers
            .Include(provider => provider.Models)
            .OrderBy(provider => provider.Name)
            .ToListAsync(cancellationToken);

    public Task<Provider?> GetAsync(int id, CancellationToken cancellationToken = default) =>
        db.Providers.SingleOrDefaultAsync(provider => provider.Id == id, cancellationToken);

    public Task CreateAsync(Provider provider, CancellationToken cancellationToken = default) =>
        CreateAsync(provider, null, cancellationToken);

    public async Task CreateAsync(
        Provider provider,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        Validate(provider);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            provider.ProtectedApiKey = ProtectApiKey(apiKey);
            provider.CredentialId = null;
        }

        db.Providers.Add(provider);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(Provider input, CancellationToken cancellationToken = default) =>
        UpdateAsync(input, null, false, cancellationToken);

    public async Task UpdateAsync(
        Provider input,
        string? apiKey,
        bool removeStoredApiKey = false,
        CancellationToken cancellationToken = default)
    {
        var provider = await db.Providers.SingleOrDefaultAsync(item => item.Id == input.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"Provider {input.Id} was not found.");

        Validate(input);
        provider.Name = input.Name;
        provider.Type = input.Type;
        provider.BaseUrl = input.BaseUrl;
        provider.AuthenticationType = input.AuthenticationType;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            provider.ProtectedApiKey = ProtectApiKey(apiKey);
            provider.CredentialId = null;
        }
        else if (!string.IsNullOrWhiteSpace(input.CredentialId))
        {
            provider.ProtectedApiKey = null;
            provider.CredentialId = input.CredentialId.Trim();
        }
        else if (removeStoredApiKey)
        {
            provider.ProtectedApiKey = null;
        }

        provider.ApiVersion = input.ApiVersion;
        provider.Enabled = input.Enabled;
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> HasStoredApiKeyAsync(int id, CancellationToken cancellationToken = default) =>
        db.Providers.AsNoTracking().AnyAsync(
            provider => provider.Id == id && provider.ProtectedApiKey != null,
            cancellationToken);

    public async Task ToggleAsync(int id, CancellationToken cancellationToken = default)
    {
        var provider = await db.Providers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Provider {id} was not found.");
        provider.Enabled = !provider.Enabled;
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var provider = await db.Providers
            .Include(item => item.Models)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Provider {id} was not found.");
        if (provider.Models.Count > 0)
        {
            throw new InvalidOperationException("Provider cannot be deleted while models reference it. Disable it instead.");
        }

        db.Providers.Remove(provider);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void Validate(Provider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Name))
        {
            throw new ArgumentException("Provider name is required.", nameof(provider));
        }

        if (!Enum.IsDefined(provider.Type))
        {
            throw new ArgumentException("Provider type is invalid.", nameof(provider));
        }

        if (!string.IsNullOrWhiteSpace(provider.BaseUrl)
            && (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("Base URL must be an absolute HTTP or HTTPS URL.", nameof(provider));
        }

        if (provider.Type is ProviderType.OpenRouter or ProviderType.Ollama
            && string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            throw new ArgumentException("Base URL is required for this provider type.", nameof(provider));
        }

        if (provider.Type == ProviderType.AzureOpenAi)
        {
            if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var azureUri)
                || !string.Equals(azureUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Azure OpenAI requires an HTTPS base URL.", nameof(provider));
            }

            if (provider.AuthenticationType is not (AuthenticationType.ApiKey or AuthenticationType.AzureKeyCredential))
            {
                throw new ArgumentException("Azure OpenAI currently supports API-key authentication only.", nameof(provider));
            }
        }
    }

    private string ProtectApiKey(string apiKey)
    {
        var normalized = apiKey.Trim();
        if (normalized.Length > 8192 || normalized.Contains('\r') || normalized.Contains('\n'))
        {
            throw new ArgumentException("Credential is invalid or exceeds the maximum supported length.", nameof(apiKey));
        }

        if (apiKeyProtector is null)
        {
            throw new InvalidOperationException("Secure credential storage is unavailable.");
        }

        return apiKeyProtector.Protect(normalized);
    }
}
