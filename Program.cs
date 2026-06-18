using System.IO;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Web;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Services;
using SuperTrigger.Web.Services.Auth;
using SuperTrigger.Web.Services.Background;
using SuperTrigger.Web.Services.Graph;

var logger = LogManager.Setup().LoadConfigurationFromFile(Path.Combine(AppContext.BaseDirectory, "nlog.config")).GetCurrentClassLogger();
logger.Info("SuperTrigger.Web starting");

try
{
    var builder = WebApplication.CreateBuilder(args);

    // NLog
    builder.Logging.ClearProviders();
    builder.Host.UseNLog();

    // SQLite
    var dbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HMS", "SuperTriggerWeb", "supertrigger.db");
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

    builder.Services.AddDbContextFactory<AppDbContext>(options =>
        options.UseSqlite($"Data Source={dbPath}"));

    // Auth: Cookie (default) + Negotiate (Windows/AD)
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.LoginPath = "/account/login";
        options.AccessDeniedPath = "/account/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    })
    .AddNegotiate();

    builder.Services.AddAuthorization();

    // Razor Pages (for login page)
    builder.Services.AddRazorPages();

    // Blazor Server + MudBlazor
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    builder.Services.AddMudServices();

    // HTTP client for Orchestrator API calls
    builder.Services.AddHttpClient("Orchestrator");

    builder.Services.AddMemoryCache();

    // App services
    builder.Services.AddSingleton<TriggerReloadChannel>();
    builder.Services.AddSingleton<AppSettingsService>();
    builder.Services.AddScoped<AuthService>();
    builder.Services.AddScoped<AdSearchService>();
    builder.Services.AddSingleton<OrchestratorService>();

    // Graph + OAuth services
    builder.Services.AddSingleton<GraphMailService>();
    builder.Services.AddSingleton<GraphSubscriptionService>();
    builder.Services.AddScoped<MailOAuthService>();

    // Background services
    builder.Services.AddSingleton<FileWatcherService>();
    builder.Services.AddSingleton<MailPollingService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<FileWatcherService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MailPollingService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<GraphSubscriptionService>());

    var app = builder.Build();

    // Apply DB migrations on startup
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContext();
        db.Database.Migrate();

        // Add columns that may not exist in DBs created before migrations were used
        foreach (var sql in new[]
        {
            "ALTER TABLE MailTriggers ADD COLUMN MailAuthMode TEXT NOT NULL DEFAULT 'Interactive'",
            "ALTER TABLE MailTriggers ADD COLUMN GraphClientId TEXT NOT NULL DEFAULT ''",
            "ALTER TABLE MailTriggers ADD COLUMN GraphClientSecret TEXT NOT NULL DEFAULT ''",
            "ALTER TABLE MailTriggers ADD COLUMN GraphSubscriptionId TEXT",
            "ALTER TABLE MailTriggers ADD COLUMN GraphSubscriptionExpiry TEXT",
            "ALTER TABLE MailTriggers ADD COLUMN OAuthAccessToken TEXT",
            "ALTER TABLE MailTriggers ADD COLUMN OAuthRefreshToken TEXT",
            "ALTER TABLE MailTriggers ADD COLUMN OAuthTokenExpiry TEXT",
            "ALTER TABLE MailTriggers ADD COLUMN OAuthUserUpn TEXT",
            "ALTER TABLE OrchSettings ADD COLUMN PublicBaseUrl TEXT NOT NULL DEFAULT ''",
            "ALTER TABLE OrchSettings ADD COLUMN AdUsername TEXT NOT NULL DEFAULT ''",
            "ALTER TABLE OrchSettings ADD COLUMN AdPassword TEXT NOT NULL DEFAULT ''",
            "ALTER TABLE OrchSettings ADD COLUMN GraphTenantId TEXT NOT NULL DEFAULT ''",
            "ALTER TABLE OrchSettings ADD COLUMN GraphClientId TEXT NOT NULL DEFAULT ''",
            "ALTER TABLE OrchSettings ADD COLUMN GraphClientSecret TEXT NOT NULL DEFAULT ''",
            "ALTER TABLE OrchSettings ADD COLUMN OAuthGlobalAccessToken TEXT",
            "ALTER TABLE OrchSettings ADD COLUMN OAuthGlobalRefreshToken TEXT",
            "ALTER TABLE OrchSettings ADD COLUMN OAuthGlobalTokenExpiry TEXT",
            "ALTER TABLE OrchSettings ADD COLUMN OAuthGlobalUserUpn TEXT",
            "ALTER TABLE FileTriggers ADD COLUMN WatcherUsername TEXT NOT NULL DEFAULT ''",
            "ALTER TABLE FileTriggers ADD COLUMN WatcherPassword TEXT NOT NULL DEFAULT ''",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_MailTriggers_TriggerName ON MailTriggers(TriggerName)",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_FileTriggers_TriggerName ON FileTriggers(TriggerName)"
        })
        {
            try { await db.Database.ExecuteSqlRawAsync(sql); }
            catch { /* column already exists — safe to ignore */ }
        }

        // Migrate legacy UseGraphApi=1 rows → MailAuthMode='AppIdPerTrigger'
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE MailTriggers SET MailAuthMode='AppIdPerTrigger' WHERE UseGraphApi=1 AND MailAuthMode='Interactive'");
        }
        catch { /* UseGraphApi column may not exist on fresh installs */ }

        var authService = scope.ServiceProvider.GetRequiredService<AuthService>();
        await authService.EnsureDefaultAdminExistsAsync();
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
    }

    // SDK 9.0 generates staticwebassets.endpoints.json for net8.0 targets, breaking _content/ serving
    var webRoot = app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
    var contentDir = Path.Combine(webRoot, "_content");
    if (Directory.Exists(contentDir))
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(contentDir),
            RequestPath = "/_content"
        });
    }
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    // Razor Pages routing (login/logout)
    app.MapRazorPages();

    // Windows Auth endpoint - triggers NTLM then creates cookie session
    app.MapGet("/account/windows-login", async (HttpContext ctx, AuthService authService, string? returnUrl) =>
    {
        var windowsIdentity = ctx.User.Identity?.Name;
        if (string.IsNullOrEmpty(windowsIdentity))
        {
            ctx.Response.Redirect("/account/login?error=no_identity");
            return;
        }

        var principal = await authService.CreateWindowsUserPrincipalAsync(windowsIdentity);
        if (principal == null)
        {
            ctx.Response.Redirect("/account/login?error=unauthorized");
            return;
        }

        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });

        ctx.Response.Redirect(returnUrl ?? "/");
    }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
    {
        AuthenticationSchemes = NegotiateDefaults.AuthenticationScheme
    });

    // Same as above but returns JSON for the fetch-based button (avoids browser redirect loop on failure)
    app.MapGet("/account/windows-login-probe", async (HttpContext ctx, AuthService authService, string? returnUrl) =>
    {
        var windowsIdentity = ctx.User.Identity?.Name;
        if (string.IsNullOrEmpty(windowsIdentity))
            return Results.Unauthorized();

        var principal = await authService.CreateWindowsUserPrincipalAsync(windowsIdentity);
        if (principal == null)
            return Results.Forbid();

        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });

        return Results.Ok(new { redirect = returnUrl ?? "/" });
    }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
    {
        AuthenticationSchemes = NegotiateDefaults.AuthenticationScheme
    });

    // Microsoft Graph webhook validation challenge (GET with validationToken param)
    app.MapGet("/api/graph-notifications", (string? validationToken) =>
        validationToken != null
            ? Results.Content(validationToken, "text/plain")
            : Results.BadRequest()
    ).AllowAnonymous();

    // Microsoft Graph change notification delivery (POST)
    app.MapPost("/api/graph-notifications", async (HttpContext ctx, GraphSubscriptionService graphSvc) =>
    {
        // Graph sends validation as POST with ?validationToken= and empty body
        if (ctx.Request.Query.TryGetValue("validationToken", out var token))
            return Results.Content(token.ToString(), "text/plain");

        string body;
        using (var reader = new StreamReader(ctx.Request.Body))
            body = await reader.ReadToEndAsync();

        // Respond 202 immediately — Graph requires a response within 30 seconds
        _ = Task.Run(async () =>
        {
            try
            {
                var json = JObject.Parse(body);
                if (json["value"] is JArray notifications)
                    await graphSvc.ProcessNotificationBatchAsync(notifications);
            }
            catch (Exception ex)
            {
                var svcLogger = ctx.RequestServices
                    .GetRequiredService<ILogger<SuperTrigger.Web.Components.App>>();
                svcLogger.LogError(ex, "Error processing Graph notification batch");
            }
        });

        return Results.Accepted();
    }).AllowAnonymous();

    // Blazor
    app.MapRazorComponents<SuperTrigger.Web.Components.App>()
        .AddInteractiveServerRenderMode();

    await app.RunAsync();
}
catch (Exception ex)
{
    logger.Error(ex, "SuperTrigger.Web stopped due to exception");
    throw;
}
finally
{
    LogManager.Shutdown();
}
