using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Pages.Pipelines;

public class EditModel(PipelineConfigurationService service) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public async Task<IActionResult> OnGetAsync(int id)
    {
        var pipeline = await service.GetAsync(id);
        if (pipeline is null) return NotFound();
        Input = new InputModel { Id = pipeline.Id, Name = pipeline.Name, Description = pipeline.Description, Enabled = pipeline.Enabled };
        return Page();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        try { await service.UpdateAsync(Input.ToEntity()); return RedirectToPage("Index"); }
        catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); return Page(); }
    }
    public sealed class InputModel
    {
        public int Id { get; set; }
        [System.ComponentModel.DataAnnotations.Required] public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool Enabled { get; set; }
        public Pipeline ToEntity() => new() { Id = Id, Name = Name, Description = Description, Enabled = Enabled };
    }
}
