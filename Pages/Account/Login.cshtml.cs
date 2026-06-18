using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SuperTrigger.Web.Services;

namespace SuperTrigger.Web.Pages.Account;

public class LoginModel(AuthService authService) : PageModel
{
    public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; set; }

    public void OnGet(string? returnUrl = null, string? error = null)
    {
        ReturnUrl = returnUrl ?? "/";
        ErrorMessage = error switch
        {
            "unauthorized" => "You are not authorized to access this application.",
            "no_identity" => "Windows authentication failed.",
            _ => null
        };
    }

    public async Task<IActionResult> OnPostAsync(string username, string password, string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? "/";

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            ErrorMessage = "Username and password are required.";
            return Page();
        }

        // Try local admin first
        System.Security.Claims.ClaimsPrincipal? principal = null;
        if (await authService.ValidateLocalAdminAsync(username, password))
        {
            principal = await authService.CreateLocalAdminPrincipalAsync(username);
        }
        else
        {
            // Fall back to AD credentials validation
            principal = await authService.ValidateAdUserAsync(username, password);
        }

        if (principal == null)
        {
            ErrorMessage = "Invalid username or password, or you are not authorized.";
            return Page();
        }

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });

        return LocalRedirect(ReturnUrl);
    }
}
