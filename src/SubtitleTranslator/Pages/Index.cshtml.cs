using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Workers;

namespace SubtitleTranslator.Pages;

public class IndexModel(
    DashboardQueryService dashboard,
    IOptionsMonitor<ScannerOptions> scannerOptions) : PageModel
{
    public DashboardOverview Overview { get; private set; } = new(
        false, 0, 0, 0, 0, 0, [], []);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var threshold = TimeSpan.FromMinutes(Math.Max(1, scannerOptions.CurrentValue.StalledJobThresholdMinutes));
        Overview = await dashboard.GetOverviewAsync(threshold, cancellationToken);
    }
}
