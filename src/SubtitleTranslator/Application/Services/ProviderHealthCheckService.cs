using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Application.Services;

public sealed class ProviderHealthCheckService(
    HttpClient httpClient,
    SubtitleTranslatorDbContext db,
    ICredentialResolver credentialResolver,
    ProviderHealthRegistry registry,
    IOptions<ProviderHealthOptions> options,
    ILogger<ProviderHealthCheckService> logger)
{
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };

    public async Task CheckAllAsync(CancellationToken cancellationToken = default)
    {
        var providers = await db.Providers
            .AsNoTracking()
            .Include(provider => provider.Models)
            .OrderBy(provider => provider.Id)
            .ToListAsync(cancellationToken);

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await CheckProviderAsync(provider, cancellationToken);
            registry.Update(snapshot);
        }
    }

    private async Task<ProviderHealthSnapshot> CheckProviderAsync(
        Provider provider,
        CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        if (!provider.Enabled)
        {
            return Snapshot(ProviderHealthState.Unknown, "Provider is disabled.");
        }

        if (provider.Type == ProviderType.AzureOpenAi && provider.AuthenticationType == AuthenticationType.ManagedIdentity)
        {
            return Snapshot(ProviderHealthState.Degraded, "Managed Identity health checks are not implemented.");
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl)
            || !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            return Snapshot(ProviderHealthState.Degraded, "Provider BaseUrl is missing or invalid.");
        }

        var credential = credentialResolver.Resolve(provider.CredentialId);
        if (provider.AuthenticationType != AuthenticationType.None
            && string.IsNullOrWhiteSpace(credential))
        {
            return Snapshot(ProviderHealthState.Degraded, "Configured credential is unavailable.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildProbeUri(provider, baseUri));
            AddAuthentication(request, provider, credential);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 1, 30)));

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                var state = (int)response.StatusCode >= 500
                    ? ProviderHealthState.Offline
                    : ProviderHealthState.Degraded;
                logger.LogWarning(
                    "Provider health probe failed for provider {ProviderId} with HTTP {StatusCode}",
                    provider.Id,
                    (int)response.StatusCode);
                return Snapshot(state, $"Endpoint returned HTTP {(int)response.StatusCode}.");
            }

            if (provider.Type == ProviderType.Ollama)
            {
                return await CheckOllamaModelsAsync(provider, response, checkedAt, cancellationToken);
            }

            return Snapshot(ProviderHealthState.Online, "Provider endpoint responded successfully.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Provider health probe timed out for provider {ProviderId}", provider.Id);
            return Snapshot(ProviderHealthState.Offline, "Provider health probe timed out.");
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Provider health probe could not reach provider {ProviderId}", provider.Id);
            return Snapshot(ProviderHealthState.Offline, "Provider endpoint could not be reached.");
        }
        catch (JsonException)
        {
            return Snapshot(ProviderHealthState.Degraded, "Provider returned an invalid health response.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning("Provider health probe configuration is invalid for provider {ProviderId}", provider.Id);
            return Snapshot(ProviderHealthState.Degraded, "Provider health check configuration is invalid.");
        }

        ProviderHealthSnapshot Snapshot(ProviderHealthState state, string message) =>
            new(provider.Id, state, message, checkedAt);
    }

    private static async Task<ProviderHealthSnapshot> CheckOllamaModelsAsync(
        Provider provider,
        HttpResponseMessage response,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            JsonOptions,
            cancellationToken);
        if (!document.RootElement.TryGetProperty("models", out var modelsElement)
            || modelsElement.ValueKind != JsonValueKind.Array)
        {
            return new ProviderHealthSnapshot(
                provider.Id,
                ProviderHealthState.Degraded,
                "Ollama returned a response without a model catalog.",
                checkedAt);
        }

        var installedModels = modelsElement.EnumerateArray()
            .Select(model => ReadString(model, "name") ?? ReadString(model, "model"))
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .ToArray();
        var configuredModels = provider.Models
            .Where(model => model.Enabled)
            .Select(model => model.ModelId)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .ToArray();

        if (configuredModels.Length == 0)
        {
            return new ProviderHealthSnapshot(provider.Id, ProviderHealthState.Degraded, "No enabled model is configured.", checkedAt);
        }

        var missingModels = configuredModels
            .Where(configured => !installedModels.Any(installed => ModelMatches(configured, installed!)))
            .ToArray();
        return missingModels.Length == 0
            ? new ProviderHealthSnapshot(provider.Id, ProviderHealthState.Online, "Endpoint is reachable and configured models are installed.", checkedAt)
            : new ProviderHealthSnapshot(provider.Id, ProviderHealthState.Degraded, "One or more configured models are not installed in Ollama.", checkedAt);
    }

    private static Uri BuildProbeUri(Provider provider, Uri baseUri)
    {
        var path = baseUri.AbsolutePath.TrimEnd('/');
        var pathPrefix = path;
        string relativePath;
        string query = string.Empty;

        switch (provider.Type)
        {
            case ProviderType.Ollama:
                if (path.EndsWith("/api/tags", StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = string.Empty;
                }
                else if (path.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase))
                {
                    pathPrefix = path[..^"/api/generate".Length];
                    relativePath = "api/tags";
                }
                else
                {
                    relativePath = path.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? "tags" : "api/tags";
                }
                break;
            case ProviderType.AzureOpenAi:
                var deploymentPath = path.IndexOf("/openai/deployments/", StringComparison.OrdinalIgnoreCase);
                if (deploymentPath >= 0)
                {
                    pathPrefix = path[..deploymentPath];
                }

                relativePath = pathPrefix.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase)
                    ? "models"
                    : "openai/models";
                if (!pathPrefix.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(provider.ApiVersion))
                    {
                        throw new InvalidOperationException("Azure API version is required for health checks.");
                    }

                    query = $"api-version={Uri.EscapeDataString(provider.ApiVersion)}";
                }
                break;
            default:
                if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                {
                    pathPrefix = path[..^"/chat/completions".Length];
                }

                relativePath = pathPrefix.EndsWith("/models", StringComparison.OrdinalIgnoreCase) ? string.Empty : "models";
                break;
        }

        var builder = new UriBuilder(baseUri)
        {
            Path = string.IsNullOrEmpty(relativePath)
                ? baseUri.AbsolutePath
                : $"{pathPrefix}/{relativePath}".Replace("//", "/", StringComparison.Ordinal),
            Query = query
        };
        return builder.Uri;
    }

    private static void AddAuthentication(HttpRequestMessage request, Provider provider, string? credential)
    {
        if (string.IsNullOrWhiteSpace(credential) || provider.AuthenticationType == AuthenticationType.None)
        {
            return;
        }

        if (provider.Type == ProviderType.AzureOpenAi)
        {
            request.Headers.TryAddWithoutValidation("api-key", credential);
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
    }

    private static bool ModelMatches(string configuredModel, string installedModel) =>
        installedModel.Equals(configuredModel, StringComparison.OrdinalIgnoreCase)
        || installedModel.StartsWith($"{configuredModel}:", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public sealed class ProviderHealthOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 60;
    public int TimeoutSeconds { get; set; } = 5;
}
