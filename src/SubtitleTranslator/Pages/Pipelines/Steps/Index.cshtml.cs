using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Infrastructure.Persistence;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Pages.Pipelines.Steps;

public class IndexModel(SubtitleTranslatorDbContext db, PipelineStepConfigurationService service) : PageModel
{
    public Pipeline? Pipeline { get; private set; }
    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }
    public async Task<IActionResult> OnGetAsync(int pipelineId)
    {
        Pipeline = await db.Pipelines.Include(item => item.Steps).ThenInclude(step => step.ModelDefinition).ThenInclude(model => model!.Provider).SingleOrDefaultAsync(item => item.Id == pipelineId);
        return Pipeline is null ? NotFound() : Page();
    }
    public async Task<IActionResult> OnPostToggleAsync(int pipelineId, int id) { try { await service.ToggleAsync(pipelineId, id); Message = "Step status updated."; } catch (Exception ex) { ErrorMessage = ex.Message; } return RedirectToPage(new { pipelineId }); }
    public async Task<IActionResult> OnPostDeleteAsync(int pipelineId, int id) { try { await service.DeleteAsync(pipelineId, id); Message = "Step removed."; } catch (Exception ex) { ErrorMessage = ex.Message; } return RedirectToPage(new { pipelineId }); }
}
