using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Pages.Prompts;

public class CreateVersionModel(PromptConfigurationService service) : PageModel
{
    public PromptTemplate? Template { get; private set; }
    [BindProperty] public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int templateId)
    {
        Template = await service.GetTemplateAsync(templateId);
        if (Template is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            PromptTemplateId = templateId,
            StaticContent = PromptConfigurationService.DefaultPromptStaticContent,
            Enabled = true
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Template = await service.GetTemplateAsync(Input.PromptTemplateId);
        if (Template is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await service.CreateVersionAsync(Input.ToEntity());
            return RedirectToPage("Index");
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public sealed class InputModel
    {
        public int Id { get; set; }
        [Required] public int PromptTemplateId { get; set; }
        public string Name { get; set; } = string.Empty;
        [Required] public string StaticContent { get; set; } = string.Empty;
        public string? GlossaryJson { get; set; }
        public bool Enabled { get; set; } = true;
        public bool SupportsPromptCache { get; set; }
        public string? PromptCacheKey { get; set; }

        public PromptVersion ToEntity() => new()
        {
            Id = Id,
            PromptTemplateId = PromptTemplateId,
            Name = Name,
            StaticContent = StaticContent,
            GlossaryJson = GlossaryJson,
            Enabled = Enabled,
            SupportsPromptCache = SupportsPromptCache,
            PromptCacheKey = PromptCacheKey
        };
    }
}
