using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.UnitTests;

public class ProviderHealthCheckServiceTests
{
    [Fact]
    public async Task CheckAllAsync_UsesBearerCredentialAndReportsOpenAiCompatibleEndpointOnline()
    {
        await using var context = CreateContext();
        context.Providers.Add(new Provider
        {
            Name = "OpenRouter",
            Type = ProviderType.OpenRouter,
            BaseUrl = "https://provider.example/api/v1/chat/completions",
            AuthenticationType = AuthenticationType.ApiKey,
            CredentialId = "TEST_PROVIDER_KEY",
            Enabled = true
        });
        await context.SaveChangesAsync();

        var registry = new ProviderHealthRegistry();
        var handler = new FakeHandler(request =>
        {
            Assert.Equal("https://provider.example/api/v1/models", request.RequestUri?.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-secret", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var service = CreateService(context, registry, handler, new FakeCredentialResolver("test-secret"));

        await service.CheckAllAsync();

        var snapshot = registry.Get(1);
        Assert.Equal(ProviderHealthState.Online, snapshot.State);
        Assert.DoesNotContain("test-secret", snapshot.Message, StringComparison.Ordinal);
        Assert.NotNull(snapshot.CheckedAt);
    }

    [Fact]
    public async Task CheckAllAsync_ConfirmsConfiguredOllamaModelIsInstalled()
    {
        await using var context = CreateContext();
        context.Providers.Add(new Provider
        {
            Name = "Local Ollama",
            Type = ProviderType.Ollama,
            BaseUrl = "http://localhost:11434/api/generate",
            AuthenticationType = AuthenticationType.None,
            Enabled = true,
            Models = [new ModelDefinition { Name = "Llama", ModelId = "llama3.2" }]
        });
        await context.SaveChangesAsync();

        var registry = new ProviderHealthRegistry();
        var handler = new FakeHandler(request =>
        {
            Assert.Equal("http://localhost:11434/api/tags", request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"models\":[{\"name\":\"llama3.2:latest\"}]}")
            };
        });

        await CreateService(context, registry, handler, new FakeCredentialResolver(null)).CheckAllAsync();

        Assert.Equal(ProviderHealthState.Online, registry.Get(1).State);
    }

    [Fact]
    public async Task CheckAllAsync_UsesAzureApiKeyHeaderAndConfiguredApiVersion()
    {
        await using var context = CreateContext();
        context.Providers.Add(new Provider
        {
            Name = "Azure",
            Type = ProviderType.AzureOpenAi,
            BaseUrl = "https://resource.example/openai/deployments/deploy/chat/completions",
            ApiVersion = "2024-10-21",
            AuthenticationType = AuthenticationType.AzureKeyCredential,
            CredentialId = "AZURE_TEST_KEY",
            Enabled = true
        });
        await context.SaveChangesAsync();

        var registry = new ProviderHealthRegistry();
        var handler = new FakeHandler(request =>
        {
            Assert.Equal("https://resource.example/openai/models?api-version=2024-10-21", request.RequestUri?.ToString());
            Assert.Contains(request.Headers.GetValues("api-key"), value => value == "azure-secret");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await CreateService(context, registry, handler, new FakeCredentialResolver("azure-secret")).CheckAllAsync();

        Assert.Equal(ProviderHealthState.Online, registry.Get(1).State);
        Assert.Contains("deployment availability was not invoked", registry.Get(1).Message, StringComparison.Ordinal);
        Assert.DoesNotContain("azure-secret", registry.Get(1).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAllAsync_ReportsOllamaModelMismatchAsDegraded()
    {
        await using var context = CreateContext();
        context.Providers.Add(new Provider
        {
            Name = "Local Ollama",
            Type = ProviderType.Ollama,
            BaseUrl = "http://localhost:11434",
            AuthenticationType = AuthenticationType.None,
            Enabled = true,
            Models = [new ModelDefinition { Name = "Missing", ModelId = "missing-model" }]
        });
        await context.SaveChangesAsync();

        var registry = new ProviderHealthRegistry();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"models\":[{\"name\":\"available-model\"}]}")
        });

        await CreateService(context, registry, handler, new FakeCredentialResolver(null)).CheckAllAsync();

        var snapshot = registry.Get(1);
        Assert.Equal(ProviderHealthState.Degraded, snapshot.State);
        Assert.Contains("not installed", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAllAsync_DoesNotCallEndpointWhenCredentialIsMissing()
    {
        await using var context = CreateContext();
        context.Providers.Add(new Provider
        {
            Name = "OpenAI-compatible",
            Type = ProviderType.OpenAiCompatible,
            BaseUrl = "https://provider.example/v1",
            AuthenticationType = AuthenticationType.ApiKey,
            CredentialId = "MISSING_KEY",
            Enabled = true
        });
        await context.SaveChangesAsync();

        var registry = new ProviderHealthRegistry();
        var handler = new FakeHandler(_ => throw new InvalidOperationException("HTTP must not be called"));

        await CreateService(context, registry, handler, new FakeCredentialResolver(null)).CheckAllAsync();

        var snapshot = registry.Get(1);
        Assert.Equal(ProviderHealthState.Degraded, snapshot.State);
        Assert.Contains("unavailable", snapshot.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MISSING_KEY", snapshot.Message, StringComparison.Ordinal);
    }

    private static ProviderHealthCheckService CreateService(
        SubtitleTranslatorDbContext context,
        ProviderHealthRegistry registry,
        HttpMessageHandler handler,
        ICredentialResolver resolver) => new(
            new HttpClient(handler),
            context,
            resolver,
            registry,
            Options.Create(new ProviderHealthOptions { TimeoutSeconds = 2 }),
            NullLogger<ProviderHealthCheckService>.Instance);

    private static SubtitleTranslatorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SubtitleTranslatorDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new SubtitleTranslatorDbContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private sealed class FakeCredentialResolver(string? value) : ICredentialResolver
    {
        public string? Resolve(string? credentialReference) => value;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
