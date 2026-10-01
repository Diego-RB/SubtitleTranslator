using Microsoft.EntityFrameworkCore;
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

// Add services to the container.
builder.Services.AddRazorPages();
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
app.MapGet("/metrics", async (TranslationMetricsService metrics, CancellationToken cancellationToken) =>
    Results.Text(
        await metrics.RenderPrometheusAsync(cancellationToken),
        "text/plain; version=0.0.4; charset=utf-8"));

app.Run();
