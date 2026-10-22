using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;

namespace SubtitleTranslator.Pages.Providers;

public class EditModel(ProviderConfigurationService service) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public bool HasStoredApiKey { get; private set; }
    public IEnumerable<ProviderType> ProviderTypes => Enum.GetValues<ProviderType>();
    public IEnumerable<AuthenticationType> AuthenticationTypes => Input.Type == ProviderType.AzureOpenAi
        ? [AuthenticationType.ApiKey, AuthenticationType.AzureKeyCredential]
        : [AuthenticationType.None, AuthenticationType.ApiKey];

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var provider = await service.GetAsync(id);
        if (provider is null) return NotFound();
        Input = InputModel.FromEntity(provider);
        HasStoredApiKey = !string.IsNullOrWhiteSpace(provider.ProtectedApiKey);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await PrepareRedisplayAsync();
            return Page();
        }

        if (Input.RemoveStoredApiKey && !string.IsNullOrWhiteSpace(Input.ApiKey))
        {
            ModelState.AddModelError(string.Empty, "Enter a replacement credential or select removal, not both.");
            await PrepareRedisplayAsync();
            return Page();
        }

        try
        {
            await service.UpdateAsync(Input.ToEntity(), Input.ApiKey, Input.RemoveStoredApiKey);
            return RedirectToPage("Index");
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await PrepareRedisplayAsync();
            return Page();
        }
    }

    private async Task PrepareRedisplayAsync()
    {
        Input.ApiKey = null;
        ModelState.Remove("Input.ApiKey");
        HasStoredApiKey = await service.HasStoredApiKeyAsync(Input.Id);
    }

    public sealed class InputModel
    {
        public int Id { get; set; }
        [System.ComponentModel.DataAnnotations.Required] public string Name { get; set; } = string.Empty;
        public ProviderType Type { get; set; }
        public string? BaseUrl { get; set; }
        public AuthenticationType AuthenticationType { get; set; }
        public string? CredentialId { get; set; }
        [System.ComponentModel.DataAnnotations.StringLength(8192)] public string? ApiKey { get; set; }
        public bool RemoveStoredApiKey { get; set; }
        public string? ApiVersion { get; set; }
        public bool Enabled { get; set; }
        public Provider ToEntity() => new() { Id = Id, Name = Name, Type = Type, BaseUrl = BaseUrl, AuthenticationType = AuthenticationType, CredentialId = CredentialId, ApiVersion = ApiVersion, Enabled = Enabled };
        public static InputModel FromEntity(Provider provider) => new() { Id = provider.Id, Name = provider.Name, Type = provider.Type, BaseUrl = provider.BaseUrl, AuthenticationType = provider.AuthenticationType, ApiVersion = provider.ApiVersion, Enabled = provider.Enabled };
    }
}
