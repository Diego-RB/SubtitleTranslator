using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Pages.Prompts;

public class IndexModel(PromptConfigurationService service) : PageModel
{
    public IReadOnlyList<PromptTemplate> Templates { get; private set; } = [];
    [BindProperty] public CreateTemplateInput NewTemplate { get; set; } = new();
    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Templates = await service.ListTemplatesAsync();
    }

    public async Task<IActionResult> OnPostCreateTemplateAsync()
    {
        if (!ModelState.IsValid)
        {
            Templates = await service.ListTemplatesAsync();
            return Page();
        }

        try
        {
            await service.CreateTemplateAsync(new PromptTemplate
            {
                Name = NewTemplate.Name,
                Description = NewTemplate.Description
            });
            Message = "Prompt template created.";
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostToggleVersionAsync(int id)
    {
        try
        {
            await service.ToggleVersionAsync(id);
            Message = "Prompt version status updated.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteTemplateAsync(int id)
    {
        try
        {
            await service.DeleteTemplateAsync(id);
            Message = "Prompt template deleted.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    public sealed class CreateTemplateInput
    {
        [Required] public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
