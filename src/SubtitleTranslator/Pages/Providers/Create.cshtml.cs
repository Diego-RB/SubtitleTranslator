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
    public IEnumerable<AuthenticationType> AuthenticationTypes => Input.Type == ProviderType.AzureOpenAi
        ? [AuthenticationType.ApiKey, AuthenticationType.AzureKeyCredential]
        : [AuthenticationType.None, AuthenticationType.ApiKey];

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            ClearSubmittedApiKey();
            return Page();
        }
        try
        {
            await service.CreateAsync(Input.ToEntity(), Input.ApiKey);
            return RedirectToPage("Index");
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            ClearSubmittedApiKey();
            return Page();
        }
    }

    private void ClearSubmittedApiKey()
    {
        Input.ApiKey = null;
        ModelState.Remove("Input.ApiKey");
    }

    public sealed class InputModel
    {
        [BindProperty, System.ComponentModel.DataAnnotations.Required] public string Name { get; set; } = string.Empty;
        [BindProperty] public ProviderType Type { get; set; }
        [BindProperty] public string? BaseUrl { get; set; }
        [BindProperty] public AuthenticationType AuthenticationType { get; set; }
        [BindProperty] public string? CredentialId { get; set; }
        [BindProperty, System.ComponentModel.DataAnnotations.StringLength(8192)] public string? ApiKey { get; set; }
        [BindProperty] public string? ApiVersion { get; set; }
        [BindProperty] public bool Enabled { get; set; } = true;
        public Provider ToEntity() => new() { Name = Name, Type = Type, BaseUrl = BaseUrl, AuthenticationType = AuthenticationType, CredentialId = CredentialId, ApiVersion = ApiVersion, Enabled = Enabled };
    }
}
