using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Pages.Providers;

public class IndexModel(ProviderConfigurationService service) : PageModel
{
    public IReadOnlyList<Provider> Providers { get; private set; } = [];
    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => Providers = await service.ListAsync();

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        try { await service.ToggleAsync(id); Message = "Provider status updated."; }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        try { await service.DeleteAsync(id); Message = "Provider deleted."; }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        return RedirectToPage();
    }
}
