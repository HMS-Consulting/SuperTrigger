using System.IO;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Web;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Services;
using SuperTrigger.Web.Services.Auth;
using SuperTrigger.Web.Services.Background;
using SuperTrigger.Web.Services.Database;
using SuperTrigger.Web.Services.Graph;

var logger = LogManager.Setup().LoadConfigurationFromFile(Path.Combine(AppContext.BaseDirectory, "nlog.config")).GetCurrentClassLogger();
logger.Info("SuperTrigger.Web starting");

try
{
    // Standalone DB setup mode, run manually by developers or by the MSI installer's
    // SetupDatabase custom action — not run automatically on normal app startup.
    //   --setup-db <request.json>   full setup: provider, schema, optional data migration from the
    //                              previous database, and recording it all in appsettings.json
    //   --migrate-db <sqlite path> the older SQLite-only form of the same thing
    if (args.Length >= 2 && args[0] is "--setup-db" or "--migrate-db")
    {
        DbSetupRequest? request = null;
        try
        {
            request = args[0] == "--setup-db"
                ? DbSetupRequest.FromJsonFile(args[1])
                : new DbSetupRequest
                {
                    Target = new DbConfig { Provider = DbProviderKind.Sqlite, SqlitePath = args[1] },
                    CopyExistingData = false
                };

            await DatabaseMigrator.SetupAsync(request, Console.Out);
            logger.Info($"Database setup completed successfully ({request.Target.Describe()})");
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Database setup failed ({request?.Target.Describe() ?? args[1]})");
            // The installer captures stdout/stderr of this process into the MSI log; include the
            // whole chain, since the useful detail (SQL error, permission denied) is usually inner.
            Console.Error.WriteLine("Database setup failed: " + ex);
            return 1;
        }
    }

    var builder = WebApplication.CreateBuilder(args);

    // NLog
    builder.Logging.ClearProviders();
    builder.Host.UseNLog();

    // SQLite file or SQL Server, per the Database section of appsettings.json. The database itself
    // is created/migrated by the MSI installer (or by running `SuperTrigger.Web.exe --setup-db
    // <request.json>` manually), never by this app at startup.
    DbConfig dbConfig;
    try
    {
        dbConfig = DbConfig.FromConfiguration(builder.Configuration);
        logger.Info($"Database: {dbConfig.Describe()}");
    }
    catch (Exception ex)
    {
        logger.Error(ex, "The Database section of appsettings.json is not usable");
        return 1;
    }

    builder.Services.AddSingleton(dbConfig);
    // Retries transient SQL Server/network failures; a no-op for SQLite.
    builder.Services.AddDbContextFactory<AppDbContext>(options =>
        dbConfig.Configure(options, enableRetryOnFailure: true));

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
        // Behind IIS with a valid HTTPS binding, ForwardedHeaders below makes the app see the real
        // scheme, so this rejects the cookie (and therefore the session) on any non-HTTPS request.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    })
    .AddNegotiate();

    builder.Services.AddAuthorization();

    // Razor Pages (for login page)
    builder.Services.AddRazorPages();

    // Blazor Server + MudBlazor
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    builder.Services.AddMudServices();

    // Keep the SignalR circuit alive through brief network blips / backgrounded tabs
    // (defaults are 3 min retention + 15s keep-alive, too short behind idle-closing proxies/NAT).
    builder.Services.Configure<CircuitOptions>(options =>
    {
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(15);
    });
    builder.Services.Configure<HubOptions>(options =>
    {
        options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    });

    // HTTP client for Orchestrator API calls
    builder.Services.AddHttpClient("Orchestrator");

    builder.Services.AddMemoryCache();

    // App services
    builder.Services.AddSingleton<TriggerReloadChannel>();
    builder.Services.AddSingleton<AppSettingsService>();
    builder.Services.AddSingleton<AuditLogService>();
    builder.Services.AddScoped<AuthService>();
    builder.Services.AddScoped<AdSearchService>();
    builder.Services.AddSingleton<OrchestratorService>();

    // Graph + OAuth services
    builder.Services.AddSingleton<GraphMailService>();
    builder.Services.AddSingleton<GraphTriggerAuthService>();
    builder.Services.AddSingleton<GraphSubscriptionService>();
    builder.Services.AddScoped<MailOAuthService>();

    // Background services
    builder.Services.AddSingleton<FileWatcherService>();
    builder.Services.AddSingleton<MailPollingService>();
    builder.Services.AddSingleton<RetentionCleanupService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<FileWatcherService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MailPollingService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<GraphSubscriptionService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionCleanupService>());

    // Since .NET 6 the default is StopHost: one unhandled exception escaping any hosted service's
    // ExecuteAsync tears down the whole application -- a mail-polling hiccup would take the web UI
    // and every other trigger with it, and IIS only brings the process back on the next request.
    // The services guard their own loops, so this is the backstop for anything that slips through.
    builder.Services.Configure<HostOptions>(o =>
        o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

    var app = builder.Build();

    // Fail fast if the DB hasn't been set up yet, instead of the app self-migrating at startup —
    // schema setup/upgrades are now the MSI installer's job (or run manually via `--setup-db`).
    var setupHint = dbConfig.IsSqlServer
        ? "Run the installer, or run 'SuperTrigger.Web.exe --setup-db <request.json>' to set it up."
        : $"Run the installer, or run 'SuperTrigger.Web.exe --migrate-db \"{dbConfig.SqlitePath}\"' to set it up.";

    if (!dbConfig.IsSqlServer && !File.Exists(dbConfig.SqlitePath))
    {
        logger.Error($"Database not found at '{dbConfig.SqlitePath}'. {setupHint}");
        return 1;
    }
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContext();
        try
        {
            await db.AuditLogs.AnyAsync();
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Could not read the database ({dbConfig.Describe()}) — it is unreachable, " +
                             $"or its schema is out of date. {setupHint}");
            return 1;
        }
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    // Trust the scheme/IP that IIS (ANCM) forwards, so the app sees the real HTTPS scheme
    // instead of the plain-HTTP connection between IIS and Kestrel.
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });
    app.UseHttpsRedirection();

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
    return 0;
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
