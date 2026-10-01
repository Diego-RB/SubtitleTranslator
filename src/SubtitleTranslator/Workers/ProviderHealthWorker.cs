using Microsoft.Extensions.Options;
using SubtitleTranslator.Application.Services;

namespace SubtitleTranslator.Workers;

public sealed class ProviderHealthWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ProviderHealthOptions> options,
    ILogger<ProviderHealthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var configuration = options.CurrentValue;
            if (configuration.Enabled)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var healthChecks = scope.ServiceProvider.GetRequiredService<ProviderHealthCheckService>();
                    await healthChecks.CheckAllAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Provider health check cycle failed");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(configuration.IntervalSeconds, 10, 3600)), stoppingToken);
        }
    }
}
