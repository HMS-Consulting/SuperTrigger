using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SuperTrigger.Web.Services;
using SuperTrigger.Web.Services.Auth;

namespace SuperTrigger.Web.Pages.Account;

[Authorize]
public class MailOAuthCallbackModel(
    MailOAuthService oauthService,
    AppSettingsService settingsService,
    ILogger<MailOAuthCallbackModel> logger) : PageModel
{
    public async Task<IActionResult> OnGetAsync(
        string? code, string? state, string? error, string? error_description)
    {
        if (!string.IsNullOrEmpty(error))
        {
            logger.LogWarning("OAuth2 callback error: {Error} — {Description}", error, error_description);

            // Clear tokens so the indicator shows "Not authenticated"
            if (int.TryParse(state, out var errorTriggerId))
            {
                if (errorTriggerId == 0)
                    await oauthService.ClearGlobalTokensAsync();
                else
                    await oauthService.ClearTriggerTokensAsync(errorTriggerId);
            }

            return Redirect("/mail-triggers?auth_error=" + Uri.EscapeDataString(error_description ?? error));
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            logger.LogWarning("OAuth2 callback missing code or state");
            return Redirect("/mail-triggers?auth_error=missing_code");
        }

        if (!int.TryParse(state, out var triggerId))
        {
            logger.LogWarning("OAuth2 callback: invalid state value '{State}'", state);
            return Redirect("/mail-triggers?auth_error=invalid_state");
        }

        try
        {
            var settings = await settingsService.GetAsync();
            var redirectUri = BuildRedirectUri(settings.PublicBaseUrl);

            if (triggerId == 0)
            {
                // Global Settings auth
                await oauthService.HandleGlobalCallbackAsync(code, redirectUri);
                return Redirect("/settings?auth_success=1");
            }
            else
            {
                // Per-trigger auth — return to edit dialog
                await oauthService.HandleCallbackAsync(code, state, redirectUri);
                return Redirect($"/mail-triggers?edit_trigger_id={triggerId}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OAuth2 callback processing failed");

            // Clear tokens so the indicator shows "Not authenticated"
            if (triggerId == 0)
                await oauthService.ClearGlobalTokensAsync();
            else
                await oauthService.ClearTriggerTokensAsync(triggerId);

            var returnUrl = triggerId == 0 ? "/settings" : "/mail-triggers";
            return Redirect($"{returnUrl}?auth_error=" + Uri.EscapeDataString(ex.Message));
        }
    }

    private string BuildRedirectUri(string? publicBaseUrl)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(publicBaseUrl)
            ? publicBaseUrl.TrimEnd('/')
            : $"{Request.Scheme}://{Request.Host}";
        return $"{baseUrl}/account/mail-oauth-callback";
    }
}
