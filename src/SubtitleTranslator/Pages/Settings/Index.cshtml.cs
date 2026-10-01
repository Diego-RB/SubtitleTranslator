using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Pages.Settings;

public class IndexModel(OrchestratorSettingsService service) : PageModel
{
    [BindProperty]
    public SettingsInput Input { get; set; } = new();

    public IReadOnlyList<Pipeline> Pipelines { get; private set; } = [];
    public IReadOnlyList<PromptVersion> PromptVersions { get; private set; } = [];

    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        var settings = await service.GetAsync();
        Input = new SettingsInput
        {
            DefaultPipelineId = settings.DefaultPipelineId,
            DefaultPromptVersionId = settings.DefaultPromptVersionId,
        };

        Pipelines = await service.ListPipelinesAsync();
        PromptVersions = await service.ListPromptVersionsAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            await service.UpdateAsync(new OrchestratorSettings
            {
                Id = 1,
                DefaultPipelineId = Input.DefaultPipelineId,
                DefaultPromptVersionId = Input.DefaultPromptVersionId,
            });

            Message = "Default settings saved.";
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            var settings = await service.GetAsync();
            Input = new SettingsInput
            {
                DefaultPipelineId = settings.DefaultPipelineId,
                DefaultPromptVersionId = settings.DefaultPromptVersionId,
            };
            Pipelines = await service.ListPipelinesAsync();
            PromptVersions = await service.ListPromptVersionsAsync();
            return Page();
        }
    }

    public class SettingsInput
    {
        public int? DefaultPipelineId { get; set; }
        public int? DefaultPromptVersionId { get; set; }
    }
}
