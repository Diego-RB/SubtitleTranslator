using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Pages.Jobs;

public class DetailsModel(DashboardQueryService dashboard) : PageModel
{
    public TranslationJob Job { get; private set; } = null!;
    public IReadOnlyList<JobTimelineEvent> Timeline { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var job = await dashboard.GetJobAsync(id, cancellationToken);
        if (job is null)
        {
            return NotFound();
        }

        Job = job;
        Timeline = BuildTimeline(job);
        return Page();
    }

    private static IReadOnlyList<JobTimelineEvent> BuildTimeline(TranslationJob job)
    {
        var events = new List<JobTimelineEvent>
        {
            new(job.CreatedAt, "Job discovered", null)
        };

        if (job.StartedAt is not null)
        {
            events.Add(new JobTimelineEvent(job.StartedAt.Value, "Processing started", null));
        }

        foreach (var attempt in job.Chunks.SelectMany(chunk => chunk.Attempts))
        {
            var outcome = attempt.Success ? "succeeded" : "failed";
            var message = $"Chunk attempt {attempt.AttemptNumber} {outcome} via {attempt.Provider} / {attempt.Model}";
            if (!string.IsNullOrWhiteSpace(attempt.ErrorCode))
            {
                message += $" ({attempt.ErrorCode})";
            }

            events.Add(new JobTimelineEvent(attempt.CreatedAt, message, attempt.ErrorMessage));
        }

        if (job.CompletedAt is not null)
        {
            events.Add(new JobTimelineEvent(job.CompletedAt.Value, $"Job {job.Status.ToString().ToLowerInvariant()}", job.LastError));
        }
        else if (!string.IsNullOrWhiteSpace(job.LastError))
        {
            events.Add(new JobTimelineEvent(job.UpdatedAt, "Latest job error", job.LastError));
        }

        return events.OrderBy(item => item.At).ToArray();
    }
}

public sealed record JobTimelineEvent(DateTimeOffset At, string Title, string? Details);
