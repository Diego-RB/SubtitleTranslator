using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;

namespace SubtitleTranslator.Pages.Providers;

public class CreateModel(ProviderConfigurationService service) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public IEnumerable<ProviderType> ProviderTypes => Enum.GetValues<ProviderType>();
    public IEnumerable<AuthenticationType> AuthenticationTypes => Enum.GetValues<AuthenticationType>();

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        try
        {
            await service.CreateAsync(Input.ToEntity());
            return RedirectToPage("Index");
        }
        catch (ArgumentException exception) { ModelState.AddModelError(string.Empty, exception.Message); return Page(); }
    }

    public sealed class InputModel
    {
        [BindProperty, System.ComponentModel.DataAnnotations.Required] public string Name { get; set; } = string.Empty;
        [BindProperty] public ProviderType Type { get; set; }
        [BindProperty] public string? BaseUrl { get; set; }
        [BindProperty] public AuthenticationType AuthenticationType { get; set; }
        [BindProperty] public string? CredentialId { get; set; }
        [BindProperty] public string? ApiVersion { get; set; }
        [BindProperty] public bool Enabled { get; set; } = true;
        public Provider ToEntity() => new() { Name = Name, Type = Type, BaseUrl = BaseUrl, AuthenticationType = AuthenticationType, CredentialId = CredentialId, ApiVersion = ApiVersion, Enabled = Enabled };
    }
}
