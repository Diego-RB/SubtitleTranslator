using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubtitleTranslator.Infrastructure.Security;

namespace SubtitleTranslator.Pages;

public class LoginModel(IConfiguration configuration, IHostEnvironment environment) : PageModel
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    [BindProperty]
    public string AccessToken { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToLocal();
        }

        if (environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuration["Dashboard:AccessToken"]))
        {
            return RedirectToPage("/Index");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var configuredToken = configuration["Dashboard:AccessToken"];
        if (environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuredToken))
        {
            return RedirectToPage("/Index");
        }

        if (!DashboardAccessTokenValidator.IsValid(AccessToken, configuredToken))
        {
            ModelState.AddModelError(string.Empty, "The access token is invalid.");
            return Page();
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "operator")],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(SessionLifetime)
            });

        return RedirectToLocal();
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }

    private IActionResult RedirectToLocal() =>
        !string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? LocalRedirect(ReturnUrl)
            : RedirectToPage("/Index");
}
