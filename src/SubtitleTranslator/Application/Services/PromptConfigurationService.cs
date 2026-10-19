using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Application.Services;

public sealed class PromptConfigurationService(SubtitleTranslatorDbContext db)
{
    public const string DefaultTemplateName = "Tradução Padrão";
    public const string DefaultPromptStaticContent = """
        Translate the provided English subtitle lines into natural Brazilian Portuguese (pt-BR).
        Rules:
        - Preserve the original meaning, tone, and dialog intent.
        - Do not summarize, merge, split, or cut lines.
        - Keep proper names unless a well-established Brazilian Portuguese localization is provided in the glossary.
        - Do not include timestamps; return only a JSON array where each element has "index" (matching the input index) and "text" (the translated subtitle text).
        """;

    public async Task<PromptVersion> EnsureDefaultPromptAsync(CancellationToken cancellationToken = default)
    {
        var existingVersion = await db.PromptVersions
            .Include(version => version.PromptTemplate)
            .OrderBy(version => version.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existingVersion is not null)
        {
            return existingVersion;
        }

        var template = await db.PromptTemplates
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (template is null)
        {
            template = new PromptTemplate
            {
                Name = DefaultTemplateName,
                Description = "Template padrão de tradução de legendas EN -> pt-BR."
            };
            db.PromptTemplates.Add(template);
            await db.SaveChangesAsync(cancellationToken);
        }

        var version = new PromptVersion
        {
            PromptTemplateId = template.Id,
            PromptTemplate = template,
            Name = "Inicial",
            Version = 1,
            StaticContent = DefaultPromptStaticContent,
            Enabled = true,
            SupportsPromptCache = false
        };
        db.PromptVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
        return version;
    }

    public Task<List<PromptTemplate>> ListTemplatesAsync(CancellationToken cancellationToken = default) =>
        db.PromptTemplates
            .Include(template => template.Versions.OrderByDescending(version => version.Version))
            .OrderBy(template => template.Name)
            .ToListAsync(cancellationToken);

    public Task<PromptTemplate?> GetTemplateAsync(int id, CancellationToken cancellationToken = default) =>
        db.PromptTemplates
            .Include(template => template.Versions.OrderByDescending(version => version.Version))
            .SingleOrDefaultAsync(template => template.Id == id, cancellationToken);

    public Task<PromptVersion?> GetVersionAsync(int id, CancellationToken cancellationToken = default) =>
        db.PromptVersions
            .Include(version => version.PromptTemplate)
            .SingleOrDefaultAsync(version => version.Id == id, cancellationToken);

    public async Task<PromptTemplate> CreateTemplateAsync(
        PromptTemplate template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (string.IsNullOrWhiteSpace(template.Name))
        {
            throw new ArgumentException("Prompt template name is required.", nameof(template));
        }

        template.Name = template.Name.Trim();
        db.PromptTemplates.Add(template);
        await db.SaveChangesAsync(cancellationToken);
        return template;
    }

    public async Task UpdateTemplateAsync(
        PromptTemplate input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ArgumentException("Prompt template name is required.", nameof(input));
        }

        var template = await db.PromptTemplates
            .SingleOrDefaultAsync(item => item.Id == input.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"Prompt template {input.Id} was not found.");

        template.Name = input.Name.Trim();
        template.Description = input.Description;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteTemplateAsync(int id, CancellationToken cancellationToken = default)
    {
        var template = await db.PromptTemplates
            .Include(item => item.Versions)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Prompt template {id} was not found.");

        var versionIds = template.Versions.Select(version => version.Id).ToArray();
        if (versionIds.Length > 0)
        {
            var referencedByJob = await db.Jobs.AnyAsync(
                job => job.PromptVersionId != null && versionIds.Contains(job.PromptVersionId.Value),
                cancellationToken);
            var referencedBySettings = await db.OrchestratorSettings.AnyAsync(
                settings => settings.DefaultPromptVersionId != null && versionIds.Contains(settings.DefaultPromptVersionId.Value),
                cancellationToken);
            if (referencedByJob || referencedBySettings)
            {
                throw new InvalidOperationException(
                    "Prompt template cannot be deleted while one of its versions is referenced by jobs or default settings.");
            }
        }

        db.PromptTemplates.Remove(template);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PromptVersion> CreateVersionAsync(
        PromptVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        await ValidateVersionAsync(version, cancellationToken);

        var maxVersion = await db.PromptVersions
            .Where(item => item.PromptTemplateId == version.PromptTemplateId)
            .Select(item => (int?)item.Version)
            .MaxAsync(cancellationToken) ?? 0;

        if (version.Version <= 0 || await db.PromptVersions.AnyAsync(
                item => item.PromptTemplateId == version.PromptTemplateId && item.Version == version.Version,
                cancellationToken))
        {
            version.Version = maxVersion + 1;
        }

        db.PromptVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
        return version;
    }

    public async Task UpdateVersionAsync(
        PromptVersion input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var version = await db.PromptVersions
            .SingleOrDefaultAsync(item => item.Id == input.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"Prompt version {input.Id} was not found.");

        input.PromptTemplateId = version.PromptTemplateId;
        await ValidateVersionAsync(input, cancellationToken);

        version.Name = input.Name?.Trim() ?? string.Empty;
        version.StaticContent = input.StaticContent;
        version.GlossaryJson = input.GlossaryJson;
        version.Enabled = input.Enabled;
        version.SupportsPromptCache = input.SupportsPromptCache;
        version.PromptCacheKey = string.IsNullOrWhiteSpace(input.PromptCacheKey) ? null : input.PromptCacheKey.Trim();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ToggleVersionAsync(int id, CancellationToken cancellationToken = default)
    {
        var version = await db.PromptVersions
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Prompt version {id} was not found.");
        version.Enabled = !version.Enabled;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ValidateVersionAsync(PromptVersion version, CancellationToken cancellationToken)
    {
        if (version.PromptTemplateId <= 0
            || !await db.PromptTemplates.AnyAsync(template => template.Id == version.PromptTemplateId, cancellationToken))
        {
            throw new ArgumentException("A valid prompt template is required.", nameof(version));
        }

        if (string.IsNullOrWhiteSpace(version.StaticContent))
        {
            throw new ArgumentException("Static prompt content is required.", nameof(version));
        }
    }
}
