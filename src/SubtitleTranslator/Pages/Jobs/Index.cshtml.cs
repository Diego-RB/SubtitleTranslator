using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Enums;

namespace SubtitleTranslator.Pages.Jobs;

public class IndexModel(DashboardQueryService dashboard) : PageModel
{
    public DashboardJobPage JobPage { get; private set; } = new([], 0, 1, 1, null, null);

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public JobStatus? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        JobPage = await dashboard.GetJobsAsync(Search, Status, PageNumber, cancellationToken: cancellationToken);
        PageNumber = JobPage.Page;
    }
}
