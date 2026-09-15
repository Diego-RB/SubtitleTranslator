using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Pages.Models;

public class IndexModel(ModelConfigurationService service) : PageModel
{
    public IReadOnlyList<ModelDefinition> Models { get; private set; } = [];
    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }
    public async Task OnGetAsync() => Models = await service.ListAsync();
    public async Task<IActionResult> OnPostToggleAsync(int id) { try { await service.ToggleAsync(id); Message = "Model status updated."; } catch (Exception ex) { ErrorMessage = ex.Message; } return RedirectToPage(); }
    public async Task<IActionResult> OnPostDeleteAsync(int id) { try { await service.DeleteAsync(id); Message = "Model deleted."; } catch (Exception ex) { ErrorMessage = ex.Message; } return RedirectToPage(); }
}
