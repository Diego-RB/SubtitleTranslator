using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Application.Services;

public sealed class SubtitleFileScanner(
    SubtitleTranslatorDbContext db,
    ILogger<SubtitleFileScanner> logger)
{
    public async Task<int> ScanOnceAsync(
        IReadOnlyCollection<string> directories,
        CancellationToken cancellationToken = default)
    {
        var settings = await db.OrchestratorSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        var created = 0;

        foreach (var configuredDirectory in directories.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredDirectory));
            if (!Directory.Exists(directory))
            {
                logger.LogWarning("Subtitle scanner directory does not exist: {Directory}", directory);
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(path => Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not scan subtitle directory {Directory}", directory);
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsTranslatedOutput(file))
                {
                    continue;
                }

                var sourcePath = NormalizePath(file);
                if (await db.Jobs.AnyAsync(job => job.SourcePath == sourcePath, cancellationToken))
                {
                    continue;
                }

                var job = new TranslationJob
                {
                    SourcePath = sourcePath,
                    OutputPath = BuildOutputPath(sourcePath),
                    PipelineId = settings?.DefaultPipelineId,
                    PromptVersionId = settings?.DefaultPromptVersionId,
                    Status = JobStatus.Discovered,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    LastActivityAt = DateTimeOffset.UtcNow
                };
                db.Jobs.Add(job);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    created++;
                    logger.LogInformation("Discovered subtitle file {SourcePath} as job {JobId}", sourcePath, job.Id);
                }
                catch (DbUpdateException exception) when (IsUniquePathConflict(exception))
                {
                    db.Entry(job).State = EntityState.Detached;
                }
            }
        }

        return created;
    }

    public static bool IsTranslatedOutput(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".pt-BR.srt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pt.srt", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildOutputPath(string sourcePath) =>
        Path.Combine(Path.GetDirectoryName(sourcePath)!, $"{Path.GetFileNameWithoutExtension(sourcePath)}.pt-BR.srt");

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private static bool IsUniquePathConflict(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };
}
