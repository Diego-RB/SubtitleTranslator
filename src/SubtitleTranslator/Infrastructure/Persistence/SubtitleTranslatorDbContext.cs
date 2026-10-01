using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Infrastructure.Persistence;

public class SubtitleTranslatorDbContext(DbContextOptions<SubtitleTranslatorDbContext> options)
    : DbContext(options)
{
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ModelDefinition> Models => Set<ModelDefinition>();
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<PipelineStep> PipelineSteps => Set<PipelineStep>();
    public DbSet<TranslationJob> Jobs => Set<TranslationJob>();
    public DbSet<TranslationChunk> Chunks => Set<TranslationChunk>();
    public DbSet<TranslationAttempt> Attempts => Set<TranslationAttempt>();
    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();
    public DbSet<PromptVersion> PromptVersions => Set<PromptVersion>();
    public DbSet<OrchestratorSettings> OrchestratorSettings => Set<OrchestratorSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Provider>(entity =>
        {
            entity.HasIndex(p => p.Name).IsUnique();
            entity.Property(p => p.Type).HasConversion<string>();
            entity.Property(p => p.AuthenticationType).HasConversion<string>();
        });

        modelBuilder.Entity<ModelDefinition>(entity =>
        {
            entity.Property(m => m.ChunkStrategy).HasConversion<string>();
            entity.Property(m => m.InputCostPer1M).HasColumnType("decimal(18,6)");
            entity.Property(m => m.OutputCostPer1M).HasColumnType("decimal(18,6)");
            entity.Property(m => m.CachedInputCostPer1M).HasColumnType("decimal(18,6)");
            entity.HasOne(m => m.Provider)
                .WithMany(p => p.Models)
                .HasForeignKey(m => m.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PipelineStep>(entity =>
        {
            entity.HasIndex(s => new { s.PipelineId, s.Order });

            entity.HasOne(s => s.Pipeline)
                .WithMany(p => p.Steps)
                .HasForeignKey(s => s.PipelineId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.ModelDefinition)
                .WithMany()
                .HasForeignKey(s => s.ModelDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TranslationJob>(entity =>
        {
            entity.Property(j => j.Status).HasConversion<string>();
            entity.Property(j => j.EstimatedCost).HasColumnType("decimal(18,6)");
            entity.HasIndex(j => j.SourcePath)
                .IsUnique()
                .HasFilter("\"Status\" NOT IN ('Completed', 'Failed', 'Cancelled')");

            entity.HasOne(j => j.Pipeline)
                .WithMany()
                .HasForeignKey(j => j.PipelineId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(j => j.PromptVersion)
                .WithMany()
                .HasForeignKey(j => j.PromptVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TranslationChunk>(entity =>
        {
            entity.Property(c => c.Status).HasConversion<string>();
            entity.HasOne(c => c.Job)
                .WithMany(j => j.Chunks)
                .HasForeignKey(c => c.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TranslationAttempt>(entity =>
        {
            entity.Property(a => a.EstimatedCost).HasColumnType("decimal(18,6)");
            entity.HasOne(a => a.Chunk)
                .WithMany(c => c.Attempts)
                .HasForeignKey(a => a.ChunkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PromptVersion>(entity =>
        {
            entity.HasOne(v => v.PromptTemplate)
                .WithMany(t => t.Versions)
                .HasForeignKey(v => v.PromptTemplateId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrchestratorSettings>(entity =>
        {
            entity.HasOne(settings => settings.DefaultPipeline)
                .WithMany()
                .HasForeignKey(settings => settings.DefaultPipelineId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(settings => settings.DefaultPromptVersion)
                .WithMany()
                .HasForeignKey(settings => settings.DefaultPromptVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
