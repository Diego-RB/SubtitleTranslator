using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;

namespace SubtitleTranslator.Pages.Models;

public class CreateModel(ModelConfigurationService service) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public IReadOnlyList<Provider> Providers { get; private set; } = [];
    public IEnumerable<ChunkStrategy> ChunkStrategies => Enum.GetValues<ChunkStrategy>();
    public async Task OnGetAsync() => Providers = await service.GetProvidersAsync();
    public async Task<IActionResult> OnPostAsync()
    {
        Providers = await service.GetProvidersAsync();
        if (!ModelState.IsValid) return Page();
        try { await service.CreateAsync(Input.ToEntity()); return RedirectToPage("Index"); }
        catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); return Page(); }
    }
    public sealed class InputModel
    {
        public int Id { get; set; }
        [Required] public int ProviderId { get; set; }
        [Required] public string Name { get; set; } = string.Empty;
        [Required] public string ModelId { get; set; } = string.Empty;
        public string? DeploymentName { get; set; }
        public bool Enabled { get; set; } = true;
        public int Priority { get; set; }
        public bool SupportsBatch { get; set; }
        public bool SupportsStreaming { get; set; }
        public bool SupportsTranslation { get; set; } = true;
        public int? ContextWindow { get; set; }
        [Range(0, int.MaxValue)] public int? MaxOutputTokens { get; set; }
        public double? Temperature { get; set; }
        public double? TopP { get; set; }
        public decimal InputCostPer1M { get; set; }
        public decimal OutputCostPer1M { get; set; }
        public decimal? CachedInputCostPer1M { get; set; }
        public ChunkStrategy ChunkStrategy { get; set; } = ChunkStrategy.MaxTokens;
        [Range(0, int.MaxValue)] public int? MaxChunkTokens { get; set; }
        [Range(0, int.MaxValue)] public int? MaxChunkSubtitles { get; set; }
        public string? ConfigurationJson { get; set; }
        public ModelDefinition ToEntity() => new() { Id = Id, ProviderId = ProviderId, Name = Name, ModelId = ModelId, DeploymentName = DeploymentName, Enabled = Enabled, Priority = Priority, SupportsBatch = SupportsBatch, SupportsStreaming = SupportsStreaming, SupportsTranslation = SupportsTranslation, ContextWindow = ContextWindow, MaxOutputTokens = MaxOutputTokens, Temperature = Temperature, TopP = TopP, InputCostPer1M = InputCostPer1M, OutputCostPer1M = OutputCostPer1M, CachedInputCostPer1M = CachedInputCostPer1M, ChunkStrategy = ChunkStrategy, MaxChunkTokens = MaxChunkTokens, MaxChunkSubtitles = MaxChunkSubtitles, ConfigurationJson = ConfigurationJson };
    }
}
