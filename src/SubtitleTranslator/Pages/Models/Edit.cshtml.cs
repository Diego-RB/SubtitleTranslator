using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Pages.Models;

public class EditModel(ModelConfigurationService service) : PageModel
{
    [BindProperty] public CreateModel.InputModel Input { get; set; } = new();
    public IReadOnlyList<Provider> Providers { get; private set; } = [];
    public async Task<IActionResult> OnGetAsync(int id)
    {
        Providers = await service.GetProvidersAsync();
        var model = await service.GetAsync(id);
        if (model is null) return NotFound();
        Input = new CreateModel.InputModel { Id = model.Id, ProviderId = model.ProviderId, Name = model.Name, ModelId = model.ModelId, DeploymentName = model.DeploymentName, Enabled = model.Enabled, Priority = model.Priority, SupportsBatch = model.SupportsBatch, SupportsStreaming = model.SupportsStreaming, SupportsTranslation = model.SupportsTranslation, ContextWindow = model.ContextWindow, MaxOutputTokens = model.MaxOutputTokens, Temperature = model.Temperature, TopP = model.TopP, InputCostPer1M = model.InputCostPer1M, OutputCostPer1M = model.OutputCostPer1M, CachedInputCostPer1M = model.CachedInputCostPer1M, ChunkStrategy = model.ChunkStrategy, MaxChunkTokens = model.MaxChunkTokens, MaxChunkSubtitles = model.MaxChunkSubtitles, ConfigurationJson = model.ConfigurationJson };
        return Page();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        Providers = await service.GetProvidersAsync();
        if (!ModelState.IsValid) return Page();
        try { await service.UpdateAsync(Input.ToEntity()); return RedirectToPage("Index"); }
        catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); return Page(); }
    }
}
