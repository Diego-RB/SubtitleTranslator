using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Pages;

public class IndexModel(SubtitleTranslatorDbContext db) : PageModel
{
    public int ActiveJobs { get; set; }
    public int CompletedJobs { get; set; }
    public int FailedJobs { get; set; }

    public async Task OnGetAsync()
    {
        ActiveJobs = await db.Jobs.CountAsync(j =>
            j.Status != Domain.Enums.JobStatus.Completed &&
            j.Status != Domain.Enums.JobStatus.Failed &&
            j.Status != Domain.Enums.JobStatus.Cancelled);
        CompletedJobs = await db.Jobs.CountAsync(j => j.Status == Domain.Enums.JobStatus.Completed);
        FailedJobs = await db.Jobs.CountAsync(j => j.Status == Domain.Enums.JobStatus.Failed);
    }
}
