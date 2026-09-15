using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Pages.Pipelines;

public class CreateModel(PipelineConfigurationService service) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public void OnGet() { }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        try { await service.CreateAsync(Input.ToEntity()); return RedirectToPage("Index"); }
        catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); return Page(); }
    }
    public sealed class InputModel
    {
        [Required] public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool Enabled { get; set; } = true;
        public Pipeline ToEntity() => new() { Name = Name, Description = Description, Enabled = Enabled };
    }
}
