using Microsoft.AspNetCore.Mvc;
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

    public async Task<JsonResult> OnGetLiveAsync(CancellationToken cancellationToken)
    {
        var threshold = TimeSpan.FromMinutes(Math.Max(1, scannerOptions.CurrentValue.StalledJobThresholdMinutes));
        var overview = await dashboard.GetOverviewAsync(threshold, cancellationToken);
        return new JsonResult(new
        {
            overview.IsWorkerOnline,
            overview.ActiveJobs,
            overview.QueuedJobs,
            overview.CompletedToday,
            overview.FailedJobs,
            overview.StalledJobs,
            RecentJobs = overview.RecentJobs.Select(job => new
            {
                job.Id,
                Status = job.Status.ToString(),
                job.TotalChunks,
                job.CompletedChunks,
                job.FailedChunks,
                job.ProgressPercent,
                job.ProgressLabel,
                job.CurrentProvider,
                job.CurrentModel,
                job.EstimatedCost
            })
        });
    }
}
