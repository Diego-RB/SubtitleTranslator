using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Pages.Prompts;

public class EditVersionModel(PromptConfigurationService service) : PageModel
{
    public PromptVersion? Version { get; private set; }
    [BindProperty] public CreateVersionModel.InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Version = await service.GetVersionAsync(id);
        if (Version is null)
        {
            return NotFound();
        }

        Input = new CreateVersionModel.InputModel
        {
            Id = Version.Id,
            PromptTemplateId = Version.PromptTemplateId,
            Name = Version.Name,
            StaticContent = Version.StaticContent,
            GlossaryJson = Version.GlossaryJson,
            Enabled = Version.Enabled,
            SupportsPromptCache = Version.SupportsPromptCache,
            PromptCacheKey = Version.PromptCacheKey
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Version = await service.GetVersionAsync(Input.Id);
        if (Version is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await service.UpdateVersionAsync(Input.ToEntity());
            return RedirectToPage("Index");
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }
}
