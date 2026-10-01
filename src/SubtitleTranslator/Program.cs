using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Net.Http.Headers;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Infrastructure.Persistence;
using SubtitleTranslator.Infrastructure.Providers.AzureOpenAi;
using SubtitleTranslator.Infrastructure.Providers.AzureBatch;
using SubtitleTranslator.Infrastructure.Providers.OpenRouter;
using SubtitleTranslator.Infrastructure.Providers.Ollama;
using SubtitleTranslator.Infrastructure.Security;
using SubtitleTranslator.Srt;
using SubtitleTranslator.Srt.Chunking;
using SubtitleTranslator.Srt.Parser;
using SubtitleTranslator.Srt.Validation;
using SubtitleTranslator.Srt.Writer;
using SubtitleTranslator.Workers;

var builder = WebApplication.CreateBuilder(args);

var dashboardAccessToken = builder.Configuration["Dashboard:AccessToken"];
var metricsAccessToken = builder.Configuration["Metrics:AccessToken"];
if (!builder.Environment.IsDevelopment()
    && (string.IsNullOrWhiteSpace(dashboardAccessToken) || dashboardAccessToken.Length < 32
        || string.IsNullOrWhiteSpace(metricsAccessToken) || metricsAccessToken.Length < 32))
{
    throw new InvalidOperationException(
        "Production requires Dashboard:AccessToken and Metrics:AccessToken, each at least 32 characters long. Configure them through environment variables or a secret store.");
}

// Add services to the container.
var requireDashboardAuthentication = !builder.Environment.IsDevelopment()
    || !string.IsNullOrWhiteSpace(dashboardAccessToken);
builder.Services.AddRazorPages(options =>
{
    if (requireDashboardAuthentication)
    {
        options.Conventions.AuthorizeFolder("/");
        options.Conventions.AllowAnonymousToPage("/Login");
    }
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Login";
        options.Cookie.Name = ".SubtitleTranslator.Operator";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddSingleton<SrtParser>();
builder.Services.AddSingleton<SrtWriter>();
builder.Services.AddSingleton<SrtValidator>();
builder.Services.AddSingleton<SrtChunker>();
builder.Services.AddSingleton<SrtFileService>();
builder.Services.AddScoped<IPipelineDefinitionService, PipelineDefinitionService>();
builder.Services.AddScoped<ProviderConfigurationService>();
builder.Services.AddScoped<ModelConfigurationService>();
builder.Services.AddScoped<PipelineConfigurationService>();
builder.Services.AddScoped<PipelineStepConfigurationService>();
builder.Services.AddScoped<DashboardQueryService>();
builder.Services.AddScoped<TranslationMetricsService>();
builder.Services.AddSingleton<ProviderHealthRegistry>();
builder.Services.AddHttpClient<ProviderHealthCheckService>();
builder.Services.AddScoped<OrchestratorSettingsService>();
builder.Services.AddScoped<AzureBatchJobStateService>();
builder.Services.AddScoped<TranslationPipelineExecutor>();
builder.Services.AddScoped<SubtitleFileScanner>();
builder.Services.AddScoped<TranslationRecoveryService>();
builder.Services.Configure<ScannerOptions>(builder.Configuration.GetSection("Scanner"));
builder.Services.Configure<ProviderHealthOptions>(builder.Configuration.GetSection("ProviderHealth"));
builder.Services.AddHostedService<SubtitleTranslationWorker>();
builder.Services.AddHostedService<ProviderHealthWorker>();
builder.Services.AddHttpClient<OllamaProvider>();
builder.Services.AddSingleton<ITranslationProvider>(serviceProvider =>
    serviceProvider.GetRequiredService<OllamaProvider>());
builder.Services.AddSingleton<ICredentialResolver, EnvironmentCredentialResolver>();
builder.Services.AddHttpClient<OpenRouterProvider>();
builder.Services.AddSingleton<ITranslationProvider>(serviceProvider =>
    serviceProvider.GetRequiredService<OpenRouterProvider>());
builder.Services.AddHttpClient<AzureOpenAiProvider>();
builder.Services.AddSingleton<ITranslationProvider>(serviceProvider =>
    serviceProvider.GetRequiredService<AzureOpenAiProvider>());
builder.Services.AddHttpClient<AzureBatchProvider>();
builder.Services.AddSingleton<IBatchTranslationProvider>(serviceProvider =>
    serviceProvider.GetRequiredService<AzureBatchProvider>());
builder.Services.AddSingleton<ITranslationProviderFactory, TranslationProviderFactory>();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=subtitletranslator.db";

builder.Services.AddDbContext<SubtitleTranslatorDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live"])
    .AddDbContextCheck<SubtitleTranslatorDbContext>(tags: ["ready"]);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SubtitleTranslatorDbContext>();
    db.Database.Migrate();
    var orchestratorSettingsService = scope.ServiceProvider.GetRequiredService<OrchestratorSettingsService>();
    await orchestratorSettingsService.EnsureDefaultsAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
app.MapGet("/metrics", async (
    HttpRequest request,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    TranslationMetricsService metrics,
    CancellationToken cancellationToken) =>
{
    var expectedToken = configuration["Metrics:AccessToken"];
    if (!environment.IsDevelopment() || !string.IsNullOrWhiteSpace(expectedToken))
    {
        var authorization = request.Headers.Authorization.ToString();
        if (!AuthenticationHeaderValue.TryParse(authorization, out var credentials)
            || !string.Equals(credentials.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || !DashboardAccessTokenValidator.IsValid(credentials.Parameter, expectedToken))
        {
            return Results.Unauthorized();
        }
    }

    return Results.Text(
        await metrics.RenderPrometheusAsync(cancellationToken),
        "text/plain; version=0.0.4; charset=utf-8");
});

app.Run();
