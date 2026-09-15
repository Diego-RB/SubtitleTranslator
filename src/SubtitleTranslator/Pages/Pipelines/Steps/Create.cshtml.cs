using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Pages.Pipelines.Steps;

public class CreateModel(PipelineStepConfigurationService service) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public Pipeline? Pipeline { get; private set; }
    public IReadOnlyList<Domain.Entities.Provider> Providers { get; private set; } = [];
    public IReadOnlyList<ModelDefinition> Models { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int pipelineId)
    {
        Input.PipelineId = pipelineId;
        return await LoadAsync(pipelineId);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync(Input.PipelineId);
        if (!ModelState.IsValid) return Page();
        try { await service.CreateAsync(Input.ToEntity(), Input.ProviderId); return RedirectToPage("Index", new { pipelineId = Input.PipelineId }); }
        catch (ArgumentException exception) { ModelState.AddModelError(string.Empty, exception.Message); return Page(); }
    }

    private async Task<IActionResult> LoadAsync(int pipelineId)
    {
        Pipeline = await service.GetPipelineAsync(pipelineId);
        if (Pipeline is null) return NotFound();
        Providers = await service.GetProvidersAsync();
        Models = await service.GetModelsAsync();
        return Page();
    }

    public sealed class InputModel
    {
        public int PipelineId { get; set; }
        [Required] public int ProviderId { get; set; }
        [Required] public int ModelDefinitionId { get; set; }
        [Range(1, int.MaxValue)] public int Order { get; set; } = 1;
        public bool Enabled { get; set; } = true;
        public PipelineStep ToEntity() => new() { PipelineId = PipelineId, ModelDefinitionId = ModelDefinitionId, Order = Order, Enabled = Enabled };
    }
}
