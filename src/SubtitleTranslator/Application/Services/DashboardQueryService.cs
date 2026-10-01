using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Application.Services;

public sealed class DashboardQueryService(
    SubtitleTranslatorDbContext db,
    ProviderHealthRegistry providerHealthRegistry)
{
    private static readonly JobStatus[] TerminalStatuses =
    [
        JobStatus.Completed,
        JobStatus.Failed,
        JobStatus.Cancelled
    ];

    private static readonly JobStatus[] QueueStatuses =
    [
        JobStatus.Discovered,
        JobStatus.Queued
    ];

    public async Task<DashboardOverview> GetOverviewAsync(
        TimeSpan stalledThreshold,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var today = now.UtcDateTime.Date;
        var tomorrow = today.AddDays(1);
        var activeJobs = await db.Jobs
            .Where(job => !TerminalStatuses.Contains(job.Status))
            .Select(job => new StalledJobCandidate(
                job.Status,
                job.LastHeartbeatAt,
                job.LastActivityAt,
                job.UpdatedAt,
                job.AzureBatchId,
                job.AzureBatchStatus,
                job.AzureBatchLastProgressAt))
            .ToListAsync(cancellationToken);

        var providerData = await db.Providers
            .AsNoTracking()
            .OrderBy(provider => provider.Name)
            .Select(provider => new
            {
                provider.Id,
                provider.Name,
                provider.Type,
                provider.Enabled,
                EnabledModelCount = provider.Models.Count(model => model.Enabled)
            })
            .ToListAsync(cancellationToken);
        var providers = providerData.Select(provider =>
        {
            var health = providerHealthRegistry.Get(provider.Id);
            return new DashboardProvider(
                provider.Id,
                provider.Name,
                provider.Type,
                provider.Enabled,
                provider.EnabledModelCount,
                health.State,
                health.Message,
                health.CheckedAt);
        }).ToArray();

        var recentJobs = await db.Jobs
            .AsNoTracking()
            .OrderByDescending(job => job.Id)
            .Take(8)
            .Select(job => new DashboardJobRow(
                job.Id,
                job.SourcePath,
                job.Status,
                job.TotalChunks,
                job.CompletedChunks,
                job.FailedChunks,
                job.CurrentProvider,
                job.CurrentModel,
                job.CreatedAt,
                job.StartedAt,
                job.CompletedAt,
                job.EstimatedCost,
                job.AzureBatchId))
            .ToListAsync(cancellationToken);
        var completedAtValues = await db.Jobs
            .AsNoTracking()
            .Where(job => job.Status == JobStatus.Completed)
            .Select(job => job.CompletedAt)
            .ToListAsync(cancellationToken);

        return new DashboardOverview(
            IsApplicationOnline: true,
            ActiveJobs: activeJobs.Count,
            QueuedJobs: activeJobs.Count(job => QueueStatuses.Contains(job.Status)),
            CompletedToday: completedAtValues.Count(completedAt =>
                completedAt >= new DateTimeOffset(today, TimeSpan.Zero)
                && completedAt < new DateTimeOffset(tomorrow, TimeSpan.Zero)),
            FailedJobs: await db.Jobs.CountAsync(job => job.Status == JobStatus.Failed, cancellationToken),
            StalledJobs: activeJobs.Count(job =>
                !QueueStatuses.Contains(job.Status)
                && (IsActiveBatch(job.AzureBatchId, job.AzureBatchStatus)
                    ? job.AzureBatchLastProgressAt ?? job.LastHeartbeatAt ?? job.LastActivityAt ?? job.UpdatedAt
                    : job.LastHeartbeatAt ?? job.LastActivityAt ?? job.UpdatedAt) <= now - stalledThreshold),
            Providers: providers,
            RecentJobs: recentJobs);
    }

    public async Task<DashboardJobPage> GetJobsAsync(
        string? search,
        JobStatus? status,
        int page,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Jobs.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(job => job.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchTerm = search.Trim();
            query = query.Where(job => job.SourcePath.Contains(searchTerm));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        var jobs = await query
            .OrderByDescending(job => job.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(job => new DashboardJobRow(
                job.Id,
                job.SourcePath,
                job.Status,
                job.TotalChunks,
                job.CompletedChunks,
                job.FailedChunks,
                job.CurrentProvider,
                job.CurrentModel,
                job.CreatedAt,
                job.StartedAt,
                job.CompletedAt,
                job.EstimatedCost,
                job.AzureBatchId))
            .ToListAsync(cancellationToken);

        return new DashboardJobPage(jobs, totalCount, page, totalPages, search, status);
    }

    public Task<TranslationJob?> GetJobAsync(int id, CancellationToken cancellationToken = default) =>
        db.Jobs
            .AsNoTracking()
            .Include(job => job.Pipeline)
            .Include(job => job.PromptVersion)
                .ThenInclude(version => version!.PromptTemplate)
            .Include(job => job.Chunks)
                .ThenInclude(chunk => chunk.Attempts)
            .SingleOrDefaultAsync(job => job.Id == id, cancellationToken);

    private sealed record StalledJobCandidate(
        JobStatus Status,
        DateTimeOffset? LastHeartbeatAt,
        DateTimeOffset? LastActivityAt,
        DateTimeOffset UpdatedAt,
        string? AzureBatchId,
        string? AzureBatchStatus,
        DateTimeOffset? AzureBatchLastProgressAt);

    private static bool IsActiveBatch(string? batchId, string? batchStatus) =>
        !string.IsNullOrWhiteSpace(batchId)
        && batchStatus is not null
        && !batchStatus.Equals("completed", StringComparison.OrdinalIgnoreCase)
        && !batchStatus.Equals("failed", StringComparison.OrdinalIgnoreCase)
        && !batchStatus.Equals("expired", StringComparison.OrdinalIgnoreCase)
        && !batchStatus.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
        && !batchStatus.Equals("canceled", StringComparison.OrdinalIgnoreCase);
}

public sealed record DashboardOverview(
    bool IsApplicationOnline,
    int ActiveJobs,
    int QueuedJobs,
    int CompletedToday,
    int FailedJobs,
    int StalledJobs,
    IReadOnlyList<DashboardProvider> Providers,
    IReadOnlyList<DashboardJobRow> RecentJobs);

public sealed record DashboardProvider(
    int Id,
    string Name,
    SubtitleTranslator.Domain.Enums.ProviderType Type,
    bool Enabled,
    int EnabledModelCount,
    ProviderHealthState HealthState,
    string HealthMessage,
    DateTimeOffset? HealthCheckedAt)
{
    public string HealthBadgeClass => HealthState switch
    {
        ProviderHealthState.Online => "text-bg-success",
        ProviderHealthState.Offline => "text-bg-danger",
        ProviderHealthState.Degraded => "text-bg-warning",
        _ => "text-bg-secondary"
    };
}

public sealed record DashboardJobRow(
    int Id,
    string SourcePath,
    JobStatus Status,
    int TotalChunks,
    int CompletedChunks,
    int FailedChunks,
    string? CurrentProvider,
    string? CurrentModel,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    decimal EstimatedCost,
    string? AzureBatchId)
{
    public int ProgressPercent => TotalChunks <= 0
        ? Status == JobStatus.Completed ? 100 : 0
        : Math.Clamp((int)Math.Round(CompletedChunks * 100d / TotalChunks), 0, 100);
}

public sealed record DashboardJobPage(
    IReadOnlyList<DashboardJobRow> Jobs,
    int TotalCount,
    int Page,
    int TotalPages,
    string? Search,
    JobStatus? Status);
