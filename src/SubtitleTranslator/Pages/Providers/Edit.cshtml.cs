using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;

namespace SubtitleTranslator.Pages.Providers;

public class EditModel(ProviderConfigurationService service) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public IEnumerable<ProviderType> ProviderTypes => Enum.GetValues<ProviderType>();
    public IEnumerable<AuthenticationType> AuthenticationTypes => Enum.GetValues<AuthenticationType>();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var provider = await service.GetAsync(id);
        if (provider is null) return NotFound();
        Input = InputModel.FromEntity(provider);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        try { await service.UpdateAsync(Input.ToEntity()); return RedirectToPage("Index"); }
        catch (ArgumentException exception) { ModelState.AddModelError(string.Empty, exception.Message); return Page(); }
    }

    public sealed class InputModel
    {
        public int Id { get; set; }
        [System.ComponentModel.DataAnnotations.Required] public string Name { get; set; } = string.Empty;
        public ProviderType Type { get; set; }
        public string? BaseUrl { get; set; }
        public AuthenticationType AuthenticationType { get; set; }
        public string? CredentialId { get; set; }
        public string? ApiVersion { get; set; }
        public bool Enabled { get; set; }
        public Provider ToEntity() => new() { Id = Id, Name = Name, Type = Type, BaseUrl = BaseUrl, AuthenticationType = AuthenticationType, CredentialId = CredentialId, ApiVersion = ApiVersion, Enabled = Enabled };
        public static InputModel FromEntity(Provider provider) => new() { Id = provider.Id, Name = provider.Name, Type = provider.Type, BaseUrl = provider.BaseUrl, AuthenticationType = provider.AuthenticationType, CredentialId = provider.CredentialId, ApiVersion = provider.ApiVersion, Enabled = provider.Enabled };
    }
}
