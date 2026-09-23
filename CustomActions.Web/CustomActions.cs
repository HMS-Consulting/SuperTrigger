using Microsoft.Deployment.WindowsInstaller;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web.Script.Serialization;

namespace CustomActionsWeb
{
    public class CustomActions
    {
        // Fixed identifier netsh uses to tag the sslcert <-> app binding. Not a secret, just needs
        // to stay constant across installs/upgrades of this product.
        private const string NetshAppId = "{DB4B36CD-22F9-4317-B074-94CDF2ACD02A}";

        private const string HostingBundleUrl = "https://aka.ms/dotnet/8.0/dotnet-hosting-win.exe";

        // The Hosting Bundle ships Microsoft.NETCore.App + Microsoft.AspNetCore.App + the ANCM, but NOT
        // Microsoft.WindowsDesktop.App -- which SuperTrigger.Web.runtimeconfig.json still asks for,
        // because the Mail/HmsTeam.Shared project references are built with UseWpf. Without it the app
        // never starts: the apphost exits with 0x80008096 (FrameworkMissingFailure) and every install
        // step that runs the exe (SetupDatabase) fails with a misleading "database" error.
        private const string DesktopRuntimeUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";

        // Major version of the shared frameworks this build of the app is bound to. Keep in sync with
        // SuperTrigger.Web.csproj's TargetFramework and with the two download URLs above.
        private const int RequiredRuntimeMajor = 8;

        private static readonly string[] RequiredSharedFrameworks =
        {
            "Microsoft.NETCore.App",
            "Microsoft.AspNetCore.App",
            "Microsoft.WindowsDesktop.App"
        };

        // Immediate, run from the UI sequence: the authored ANCMV2PATH file search only proves an
        // ASP.NET Core Module is present, which an older Hosting Bundle (6.0/7.0) satisfies just as
        // well as the 8.0 one. Set a property the wizard can use to decide whether PrerequisitesDlg
        // has to be shown, based on the shared frameworks that are actually installed.
        [CustomAction]
        public static ActionResult DetectPrerequisites(Session session)
        {
            try
            {
                var missing = RequiredSharedFrameworks.Where(f => !IsSharedFrameworkInstalled(f)).ToArray();
                session["RUNTIMESMISSING"] = missing.Length == 0 ? "" : string.Join(",", missing);
                session.Log("DetectPrerequisites: RUNTIMESMISSING='{0}'", session["RUNTIMESMISSING"]);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                // Never block the wizard on a detection failure: worst case PrerequisitesDlg is skipped
                // and the deferred EnsurePrerequisites (which re-checks live state) reports the problem.
                session.Log("DetectPrerequisites failed: {0}", ex);
                return ActionResult.Success;
            }
        }

        [CustomAction]
        public static ActionResult EnsurePrerequisites(Session session)
        {
            try
            {
                var data = session.CustomActionData;
                bool installIis = data["INSTALLIIS"] == "1";
                bool downloadHostingBundle = data["HOSTINGBUNDLECHOICE"] == "Download";

                if (!IsIisInstalled())
                {
                    if (!installIis)
                    {
                        return Fail(session,
                            "IIS (the Web Server role) is not installed. Enable it manually, or re-run this " +
                            "installer and choose to enable it automatically.");
                    }

                    session.Log("IIS not detected -- enabling via DISM (this can take a while)...");
                    bool rebootRequired;
                    int exitCode = RunDism(session, BuildEnableIisArguments(), out rebootRequired);

                    if (rebootRequired)
                    {
                        return Fail(session,
                            "IIS was enabled, but Windows must restart before continuing. Restart the computer " +
                            "and run this installer again.");
                    }
                    if (exitCode != 0 || !IsIisInstalled())
                    {
                        return Fail(session, string.Format(
                            "Failed to enable IIS automatically (DISM exit code {0}). Enable the " +
                            "Web Server (IIS) role manually and re-run this installer.", exitCode));
                    }
                }

                // The Hosting Bundle covers three things at once: the ANCM that IIS needs to host the
                // app, and the Microsoft.NETCore.App / Microsoft.AspNetCore.App shared frameworks the
                // app runs on. Check all three, not just the ANCM -- an older bundle (6.0/7.0) leaves
                // aspnetcorev2.dll on disk while the 8.0 frameworks are nowhere to be found.
                if (!IsAncmInstalled() ||
                    !IsSharedFrameworkInstalled("Microsoft.NETCore.App") ||
                    !IsSharedFrameworkInstalled("Microsoft.AspNetCore.App"))
                {
                    if (!downloadHostingBundle)
                    {
                        return Fail(session,
                            "The ASP.NET Core 8 Hosting Bundle is not installed. Install it from " +
                            "https://dotnet.microsoft.com/download/dotnet/8.0 and re-run this installer, or " +
                            "re-run and choose to download it automatically.");
                    }

                    session.Log("ASP.NET Core 8 hosting prerequisites incomplete -- downloading and installing the Hosting Bundle...");
                    string reason;
                    if (!DownloadAndInstallRuntime(session, HostingBundleUrl, "dotnet-hosting-8-win", "Hosting Bundle", out reason))
                    {
                        return Fail(session, string.Format(
                            "Failed to download or install the ASP.NET Core Hosting Bundle automatically ({0}). " +
                            "Check internet connectivity, or install it manually and re-run this installer.", reason));
                    }

                    if (!IsAncmInstalled())
                    {
                        return Fail(session,
                            "The ASP.NET Core Hosting Bundle was installed but the ASP.NET Core Module still " +
                            "wasn't detected. Restart the computer and re-run this installer.");
                    }
                    if (!IsSharedFrameworkInstalled("Microsoft.NETCore.App") ||
                        !IsSharedFrameworkInstalled("Microsoft.AspNetCore.App"))
                    {
                        return Fail(session,
                            "The ASP.NET Core Hosting Bundle was installed but the .NET 8 runtimes still weren't " +
                            "detected. Restart the computer and re-run this installer.");
                    }
                }

                // Separate download: the Hosting Bundle does not include the Desktop Runtime, and the app
                // still carries a Microsoft.WindowsDesktop.App framework reference (see DesktopRuntimeUrl).
                if (!IsSharedFrameworkInstalled("Microsoft.WindowsDesktop.App"))
                {
                    if (!downloadHostingBundle)
                    {
                        return Fail(session,
                            "The .NET 8 Desktop Runtime is not installed. Install it from " +
                            "https://dotnet.microsoft.com/download/dotnet/8.0 (\"Desktop Runtime\", x64) and " +
                            "re-run this installer, or re-run and choose to download the prerequisites " +
                            "automatically.");
                    }

                    session.Log(".NET 8 Desktop Runtime not detected -- downloading and installing it...");
                    string reason;
                    if (!DownloadAndInstallRuntime(session, DesktopRuntimeUrl, "windowsdesktop-runtime-8-win", "Desktop Runtime", out reason))
                    {
                        return Fail(session, string.Format(
                            "Failed to download or install the .NET 8 Desktop Runtime automatically ({0}). " +
                            "Check internet connectivity, or install it manually and re-run this installer.", reason));
                    }

                    if (!IsSharedFrameworkInstalled("Microsoft.WindowsDesktop.App"))
                    {
                        return Fail(session,
                            "The .NET 8 Desktop Runtime was installed but still wasn't detected. Restart the " +
                            "computer and re-run this installer.");
                    }
                }

                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("EnsurePrerequisites failed: {0}\r\n{1}", ex.Message, ex.StackTrace);
                return ActionResult.Failure;
            }
        }

        private static ActionResult Fail(Session session, string message)
        {
            session.Log("Custom action failed: {0}", message);
            var record = new Record { FormatString = message };
            session.Message(InstallMessage.Error, record);
            return ActionResult.Failure;
        }

        private static bool IsIisInstalled()
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\InetStp"))
            {
                return key != null && key.GetValue("MajorVersion") != null;
            }
        }

        private static bool IsAncmInstalled()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "IIS", "Asp.Net Core Module", "V2", "aspnetcorev2.dll");
            return File.Exists(path);
        }

        // The 64-bit dotnet root. This DLL is built for x86 (custom actions run in a 32-bit msiexec
        // host), so SpecialFolder.ProgramFiles would hand back "Program Files (x86)" -- where the x86
        // runtimes live, not the x64 ones the app's own apphost resolves against.
        private static string DotnetRoot
        {
            get
            {
                string programFiles = Environment.GetEnvironmentVariable("ProgramW6432");
                if (string.IsNullOrEmpty(programFiles))
                    programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(programFiles, "dotnet");
            }
        }

        // Same rule the .NET apphost applies: a framework reference of "8.0.0" is satisfied by any
        // installed 8.x version (roll-forward on patch/minor), so match on the major version only.
        private static bool IsSharedFrameworkInstalled(string frameworkName)
        {
            return GetInstalledFrameworkVersions(frameworkName).Length > 0;
        }

        private static string[] GetInstalledFrameworkVersions(string frameworkName)
        {
            string sharedDir = Path.Combine(DotnetRoot, "shared", frameworkName);
            if (!Directory.Exists(sharedDir)) return new string[0];

            return Directory.GetDirectories(sharedDir)
                .Select(Path.GetFileName)
                .Where(name =>
                {
                    int dot = name.IndexOf('.');
                    int major;
                    return dot > 0
                        && int.TryParse(name.Substring(0, dot), out major)
                        && major == RequiredRuntimeMajor;
                })
                .ToArray();
        }

        private static string BuildEnableIisArguments()
        {
            string[] features =
            {
                "IIS-WebServerRole", "IIS-WebServer", "IIS-CommonHttpFeatures", "IIS-HttpErrors", "IIS-HttpRedirect",
                "IIS-ApplicationDevelopment", "IIS-Security", "IIS-RequestFiltering", "IIS-NetFxExtensibility45",
                "IIS-HealthAndDiagnostics", "IIS-HttpLogging", "IIS-Performance", "IIS-HttpCompressionStatic",
                "IIS-WebServerManagementTools", "IIS-ManagementConsole", "IIS-StaticContent", "IIS-DefaultDocument",
                "IIS-DirectoryBrowsing", "IIS-ASPNET45", "IIS-ISAPIExtensions", "IIS-ISAPIFilter", "IIS-WindowsAuthentication",
                // Required for the site's preloadEnabled setting (see BuildConfigureScript) to have any
                // effect -- without this feature IIS accepts the setting and silently ignores it, so the
                // app pool would still wait for a first HTTP request before starting.
                "IIS-ApplicationInit"
            };
            return "/Online /Enable-Feature " + string.Join(" ", features.Select(f => "/FeatureName:" + f)) + " /All /NoRestart";
        }

        private static int RunDism(Session session, string arguments, out bool rebootRequired)
        {
            var psi = new ProcessStartInfo
            {
                // Same WOW64 concern as PowerShell above -- DISM must be run from the native 64-bit
                // System32 view, not the 32-bit SysWOW64 copy this x86 CA would otherwise resolve to.
                FileName = GetSysnativePath("dism.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            string stdout, stderr;
            int exitCode;
            using (var process = Process.Start(psi))
            {
                stdout = process.StandardOutput.ReadToEnd();
                stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                exitCode = process.ExitCode;
            }

            session.Log("DISM exit code: {0}", exitCode);
            session.Log("DISM stdout: {0}", stdout);
            if (!string.IsNullOrEmpty(stderr)) session.Log("DISM stderr: {0}", stderr);

            rebootRequired = exitCode == 3010 ||
                stdout.IndexOf("Restart Needed: Yes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                stdout.IndexOf("Reboot required=yes", StringComparison.OrdinalIgnoreCase) >= 0;

            return exitCode;
        }

        private static bool DownloadAndInstallRuntime(Session session, string url, string fileNamePrefix,
            string displayName, out string reason)
        {
            string installerPath = Path.Combine(Path.GetTempPath(), fileNamePrefix + "_" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                using (var client = new WebClient())
                {
                    // The aka.ms links redirect to HTTPS endpoints that refuse anything below TLS 1.2,
                    // and .NET Framework 4.x on an untouched machine still defaults to SSL3/TLS 1.0.
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    client.DownloadFile(url, installerPath);
                }

                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/install /quiet /norestart",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                int exitCode;
                using (var process = Process.Start(psi))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                    session.Log("{0} installer stdout: {1}", displayName, stdout);
                }

                session.Log("{0} installer exit code: {1}", displayName, exitCode);
                // 1638 = "another version of this product is already installed": for these runtime
                // installers that means the payload is already present, which is exactly what we want.
                if (exitCode != 0 && exitCode != 1638)
                {
                    reason = string.Format("installer exit code {0}", exitCode);
                    return false;
                }

                reason = "";
                return true;
            }
            catch (Exception ex)
            {
                session.Log("DownloadAndInstallRuntime ({0}) failed: {1}", displayName, ex);
                reason = ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { /* best effort cleanup */ }
            }
        }

        [CustomAction]
        public static ActionResult EnumerateCertificates(Session session)
        {
            try
            {
                session.Log("Begin EnumerateCertificates");

                using (var view = session.Database.OpenView("SELECT `Property`, `Order`, `Value`, `Text` FROM `ComboBox`"))
                {
                    view.Execute();

                    using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                    {
                        store.Open(OpenFlags.ReadOnly);
                        int order = 1;
                        var certs = store.Certificates
                            .Cast<X509Certificate2>()
                            .Where(c => c.HasPrivateKey && c.NotAfter > DateTime.Now)
                            .OrderByDescending(c => c.NotAfter);

                        foreach (var cert in certs)
                        {
                            string text = string.Format("{0}  (expires {1:d})  [{2}]",
                                cert.GetNameInfo(X509NameType.SimpleName, false), cert.NotAfter, cert.Thumbprint);

                            using (var record = new Record(4))
                            {
                                record[1] = "SSLTHUMBPRINT";
                                record[2] = order++;
                                record[3] = cert.Thumbprint;
                                record[4] = text;
                                view.Modify(ViewModifyMode.InsertTemporary, record);
                            }
                        }
                        store.Close();
                    }
                }

                session.Log("End EnumerateCertificates");
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("EnumerateCertificates failed: {0}\r\n{1}", ex.Message, ex.StackTrace);
                return ActionResult.Failure;
            }
        }

        [CustomAction]
        public static ActionResult GenerateSelfSignedCertificate(Session session)
        {
            try
            {
                session.Log("Begin GenerateSelfSignedCertificate");

                string subjectName = "CN=" + Environment.MachineName;
                using (var rsa = RSA.Create(2048))
                {
                    var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    request.CertificateExtensions.Add(new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
                    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // Server Authentication
                    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

                    var sanBuilder = new SubjectAlternativeNameBuilder();
                    sanBuilder.AddDnsName(Environment.MachineName);
                    request.CertificateExtensions.Add(sanBuilder.Build());

                    DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
                    DateTimeOffset notAfter = notBefore.AddYears(5);

                    using (var ephemeralCert = request.CreateSelfSigned(notBefore, notAfter))
                    using (var cert = new X509Certificate2(ephemeralCert.Export(X509ContentType.Pfx), (string)null,
                        X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable))
                    {
                        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                        {
                            store.Open(OpenFlags.ReadWrite);
                            store.Add(cert);
                            store.Close();
                        }

                        session["SSLTHUMBPRINT"] = cert.Thumbprint;
                        session.Log("Generated self-signed certificate, thumbprint={0}", cert.Thumbprint);
                    }
                }

                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("GenerateSelfSignedCertificate failed: {0}\r\n{1}", ex.Message, ex.StackTrace);
                return ActionResult.Failure;
            }
        }

        // ------------------------------------------------------------------ database

        // Where the installer keeps its own database working files: the copy of the previous
        // install's appsettings.json (taken before the old files are removed) and the setup request
        // handed to the app. Same folder the app's default SQLite database lives in, so it exists on
        // every machine this product has ever been installed on.
        private static string DbWorkingDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "HMS", "SuperTriggerWeb");
            }
        }

        private const string PreviousConfigFileName = "prev-appsettings.json";
        private const string DbSetupRequestFileName = "dbsetup.json";
        private const string DefaultSqliteDbPath = @"C:\ProgramData\HMS\SuperTriggerWeb\supertrigger.db";

        // Immediate. Two jobs, both of which have to happen while the *previous* install's files are
        // still on disk (RemoveExistingProducts deletes them):
        //   1. Copy its appsettings.json aside, so the app can read the old database's location (and
        //      credentials) if the admin asks to bring the data across to a new database.
        //   2. Prefill the database dialogs with the settings that install is currently using, so
        //      clicking through an upgrade keeps the same database instead of reverting to defaults.
        // Prefill only touches properties still at their authored default, so values passed on the
        // command line for a silent install always win. A no-op (and never fails the install) when
        // there's nothing to read: fresh install, or an upgrade from a build that predates this.
        [CustomAction]
        public static ActionResult ReadExistingDbConfig(Session session)
        {
            try
            {
                session.Log("Begin ReadExistingDbConfig");
                string installFolder = session["INSTALLFOLDER"];
                if (string.IsNullOrEmpty(installFolder)) return ActionResult.Success;

                string appSettingsPath = Path.Combine(installFolder, "appsettings.json");
                if (!File.Exists(appSettingsPath))
                {
                    session.Log("ReadExistingDbConfig: no appsettings.json at '{0}', keeping the authored defaults", appSettingsPath);
                    return ActionResult.Success;
                }

                Directory.CreateDirectory(DbWorkingDirectory);
                string previousConfigCopy = Path.Combine(DbWorkingDirectory, PreviousConfigFileName);
                File.Copy(appSettingsPath, previousConfigCopy, true);
                session["PREVDBCONFIGFILE"] = previousConfigCopy;
                session.Log("ReadExistingDbConfig: copied the existing configuration to '{0}'", previousConfigCopy);

                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(appSettingsPath));
                var database = GetSection(root, "Database");
                if (database == null)
                {
                    session.Log("ReadExistingDbConfig: no Database section, keeping the authored defaults");
                    return ActionResult.Success;
                }
                var sqlServer = GetSection(database, "SqlServer");

                // A previous install with no Provider key predates SQL Server support, so it's SQLite.
                string provider = GetValue(database, "Provider");
                if (string.IsNullOrEmpty(provider)) provider = "Sqlite";

                // Marks "there was a previous install" and is what the DataMigrationDlg condition
                // compares against to decide whether the database is actually moving.
                session["PREVDBPROVIDER"] = provider;
                session["PREVDBPATH"] = GetValue(database, "Path");
                session["PREVSQLSERVER"] = GetValue(sqlServer, "Server");
                session["PREVSQLDATABASE"] = GetValue(sqlServer, "Database");

                if (session["DBCONFIGREAD"] == "1")
                {
                    session.Log("ReadExistingDbConfig: dialogs were already prefilled, only refreshed the config copy");
                    return ActionResult.Success;
                }

                PrefillIfDefault(session, "DBPROVIDER", "Sqlite", provider);
                PrefillIfDefault(session, "DBPATH", DefaultSqliteDbPath, GetValue(database, "Path"));
                PrefillIfDefault(session, "SQLSERVER", "", GetValue(sqlServer, "Server"));
                PrefillIfDefault(session, "SQLPORT", "", GetValue(sqlServer, "Port"));
                PrefillIfDefault(session, "SQLINSTANCE", "", GetValue(sqlServer, "Instance"));
                PrefillIfDefault(session, "SQLDATABASE", "SuperTriggerWeb", GetValue(sqlServer, "Database"));
                PrefillIfDefault(session, "SQLAUTHMODE", "Windows", GetValue(sqlServer, "AuthMode"));
                PrefillIfDefault(session, "SQLUSER", "", GetValue(sqlServer, "Username"));
                // Still encrypted: the app accepts the stored form as-is, so an upgrade that leaves
                // the password field alone keeps working without ever handling it in the clear.
                PrefillIfDefault(session, "SQLPASSWORD", "", GetValue(sqlServer, "Password"));

                session["DBCONFIGREAD"] = "1";
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                // Never block the install over this -- worst case the dialogs show the authored
                // defaults instead of the previous values.
                session.Log("ReadExistingDbConfig failed (non-fatal): {0}", ex.Message);
                return ActionResult.Success;
            }
        }

        private static void PrefillIfDefault(Session session, string property, string authoredDefault, string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            string current = session[property] ?? "";
            if (current.Length > 0 && !current.Equals(authoredDefault, StringComparison.OrdinalIgnoreCase))
            {
                session.Log("ReadExistingDbConfig: {0} was set explicitly ('{1}'), not overwriting it", property, property == "SQLPASSWORD" ? "***" : current);
                return;
            }

            session[property] = value;
        }

        // Immediate: writes the setup instructions for the deferred SetupDatabase action below, and
        // hands it the file path through CustomActionData. Has to be a separate immediate action
        // because a deferred one can't read installer properties, and the SQL password must not
        // travel on a command line -- it goes into this file DPAPI-encrypted instead, and
        // SetupDatabase deletes the file once the app has consumed it.
        [CustomAction]
        public static ActionResult PrepareDbSetupRequest(Session session)
        {
            try
            {
                session.Log("Begin PrepareDbSetupRequest");

                string provider = session["DBPROVIDER"] == "SqlServer" ? "SqlServer" : "Sqlite";
                string sqlitePath = session["DBPATH"];
                if (string.IsNullOrEmpty(sqlitePath)) sqlitePath = DefaultSqliteDbPath;

                var request = new Dictionary<string, object>
                {
                    { "Provider", provider },
                    { "SqlitePath", sqlitePath },
                    { "Server", session["SQLSERVER"] ?? "" },
                    { "Port", session["SQLPORT"] ?? "" },
                    { "Instance", session["SQLINSTANCE"] ?? "" },
                    { "Database", session["SQLDATABASE"] ?? "" },
                    { "AuthMode", session["SQLAUTHMODE"] == "Sql" ? "Sql" : "Windows" },
                    { "Username", session["SQLUSER"] ?? "" },
                    { "Password", ProtectSecret(session["SQLPASSWORD"]) },
                    { "Encrypt", true },
                    { "TrustServerCertificate", session["SQLTRUSTCERT"] != "0" },
                    { "DataAction", session["DBDATAACTION"] == "Fresh" ? "Fresh" : "Copy" },
                    { "PreviousConfigFile", session["PREVDBCONFIGFILE"] ?? "" }
                };

                Directory.CreateDirectory(DbWorkingDirectory);
                string requestPath = Path.Combine(DbWorkingDirectory, DbSetupRequestFileName);
                File.WriteAllText(requestPath, new JavaScriptSerializer().Serialize(request));

                string exePath = Path.Combine(session["INSTALLFOLDER"], "SuperTrigger.Web.exe");
                // MsiLogFileLocation is only readable from an immediate action, so hand it across to the
                // deferred step here -- an error that says "check the installer log" is not much use
                // without the log's path in it.
                session["SetupDatabase"] = "EXE=" + exePath + ";REQ=" + requestPath +
                    ";LOG=" + (session["MsiLogFileLocation"] ?? "");

                session.Log("PrepareDbSetupRequest: provider={0}, request='{1}'", provider, requestPath);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("PrepareDbSetupRequest failed: {0}\r\n{1}", ex.Message, ex.StackTrace);
                return Fail(session, "Could not prepare the database setup step: " + ex.Message);
            }
        }

        // Deferred: runs the freshly-installed app's own --setup-db CLI mode, which creates or
        // upgrades the chosen database (SQLite file or SQL Server database), optionally copies the
        // previous database's contents into it, and records the choice in appsettings.json -- so the
        // app never has to do any of that at startup.
        [CustomAction]
        public static ActionResult SetupDatabase(Session session)
        {
            string requestPath = null;
            try
            {
                session.Log("Begin SetupDatabase");
                var data = session.CustomActionData;
                string exePath = data["EXE"];
                requestPath = data["REQ"];
                string logPath;
                if (!data.TryGetValue("LOG", out logPath)) logPath = "";

                // The exe is about to be launched for the first time on this machine. If a shared
                // framework it needs is missing, the apphost dies before Main() with a bare exit code
                // and nothing on stdout/stderr -- so check the runtimeconfig here and report the actual
                // problem instead of a "database" error nobody can act on.
                string missingFramework = FindMissingAppFramework(session, exePath);
                if (missingFramework != null)
                {
                    return Fail(session, string.Format(
                        "The .NET runtime \"{0}\" (version {1}.x, x64) required by SuperTrigger.Web.exe is not " +
                        "installed, so the database could not be set up. Install it from " +
                        "https://dotnet.microsoft.com/download/dotnet/{1}.0 and repair this installation.",
                        missingFramework, RequiredRuntimeMajor));
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--setup-db \"" + requestPath + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                string stdout, stderr;
                int exitCode;
                using (var process = Process.Start(psi))
                {
                    stdout = process.StandardOutput.ReadToEnd();
                    stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                }

                session.Log("SetupDatabase exit code: {0}", exitCode);
                if (!string.IsNullOrEmpty(stdout)) session.Log("SetupDatabase stdout: {0}", stdout);
                if (!string.IsNullOrEmpty(stderr)) session.Log("SetupDatabase stderr: {0}", stderr);

                if (exitCode != 0)
                {
                    string hostError = DescribeApphostExitCode(exitCode);
                    if (hostError != null)
                    {
                        return Fail(session, string.Format(
                            "SuperTrigger.Web.exe could not start at all ({0}, exit code 0x{1:X8}), so the " +
                            "database was not set up. This is a .NET runtime problem on this machine, not a " +
                            "database one. Install the .NET {2} Hosting Bundle and Desktop Runtime (x64) from " +
                            "https://dotnet.microsoft.com/download/dotnet/{2}.0 and repair this installation.",
                            hostError, exitCode, RequiredRuntimeMajor));
                    }

                    return Fail(session, string.Format(
                        "Failed to set up the database (exit code {0}). {1} for the details reported by " +
                        "SuperTrigger.Web.exe -- the server may be unreachable, the credentials may be wrong, " +
                        "or the account may not be allowed to create the database.",
                        exitCode,
                        string.IsNullOrEmpty(logPath)
                            ? "Re-run this installer with \"msiexec /i <package>.msi /l*v install.log\" and check the log"
                            : "Check the installer log at " + logPath));
                }

                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("SetupDatabase failed: {0}\r\n{1}", ex.Message, ex.StackTrace);
                return ActionResult.Failure;
            }
            finally
            {
                // Both files hold (encrypted) credentials and are only needed for this run.
                TryDelete(requestPath);
                TryDelete(Path.Combine(DbWorkingDirectory, PreviousConfigFileName));
            }
        }

        // Returns the name of the first shared framework the app asks for but the machine doesn't have,
        // or null when everything is present (or the runtimeconfig can't be read -- in that case let the
        // exe run and report for itself rather than blocking the install on a parsing problem).
        private static string FindMissingAppFramework(Session session, string exePath)
        {
            try
            {
                string configPath = Path.ChangeExtension(exePath, null) + ".runtimeconfig.json";
                if (!File.Exists(configPath))
                {
                    session.Log("FindMissingAppFramework: '{0}' not found, skipping the check", configPath);
                    return null;
                }

                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath));
                var options = GetSection(root, "runtimeOptions");
                if (options == null) return null;

                // Single-framework apps use "framework"; multi-framework ones (this app) use "frameworks".
                var declared = new List<Dictionary<string, object>>();
                var single = GetSection(options, "framework");
                if (single != null) declared.Add(single);
                object many;
                if (options.TryGetValue("frameworks", out many))
                {
                    var list = many as System.Collections.IEnumerable;
                    if (list != null)
                        declared.AddRange(list.OfType<Dictionary<string, object>>());
                }

                foreach (var framework in declared)
                {
                    string name = GetValue(framework, "name");
                    if (string.IsNullOrEmpty(name)) continue;
                    if (IsSharedFrameworkInstalled(name)) continue;

                    session.Log("FindMissingAppFramework: '{0}' {1}.x is not installed under '{2}'",
                        name, RequiredRuntimeMajor, DotnetRoot);
                    return name;
                }

                return null;
            }
            catch (Exception ex)
            {
                session.Log("FindMissingAppFramework failed (continuing anyway): {0}", ex.Message);
                return null;
            }
        }

        // .NET apphost/hostfxr failure codes (dotnet/runtime error_codes.h). These come out of the host
        // before any managed code runs, so the process produces no output of its own to log.
        private static string DescribeApphostExitCode(int exitCode)
        {
            switch (unchecked((uint)exitCode))
            {
                case 0x80008096: return "the .NET runtime it requires is missing";
                case 0x8000809c: return "the installed .NET runtime is not compatible with the version it requires";
                case 0x80008093: return "its runtimeconfig.json is invalid";
                case 0x80008095: return "the executable is not bound to a managed application";
                case 0x8000808b:
                case 0x8000808c: return "the .NET runtime could not be resolved";
                case 0x8000808a: return "the .NET runtime failed to start";
                default: return null;
            }
        }

        // Immediate, driven by the "Test Connection" button on SqlServerDlg. Always returns Success:
        // the result is reported in a message box, and a failed test must not abort the install (the
        // admin may be about to fix the details, or may know better than the test does).
        [CustomAction]
        public static ActionResult TestSqlConnection(Session session)
        {
            string connectionString = null;
            try
            {
                connectionString = BuildSqlConnectionString(session);
                session.Log("TestSqlConnection: connecting to {0}", new SqlConnectionStringBuilder(connectionString).DataSource);

                // Connect to master, so a database that doesn't exist yet (the installer creates it)
                // still produces a useful "the server and credentials are fine" answer.
                var masterBuilder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };

                string serverVersion;
                bool databaseExists;
                using (var connection = new SqlConnection(masterBuilder.ConnectionString))
                {
                    connection.Open();
                    serverVersion = connection.ServerVersion;

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END";
                        command.Parameters.AddWithValue("@name", session["SQLDATABASE"] ?? "");
                        databaseExists = Convert.ToInt32(command.ExecuteScalar()) == 1;
                    }
                }

                string message = "Connected successfully (SQL Server " + serverVersion + ").\r\n\r\n" +
                    (databaseExists
                        ? "The database \"" + session["SQLDATABASE"] + "\" already exists and will be upgraded to this version."
                        : "The database \"" + session["SQLDATABASE"] + "\" does not exist yet and will be created during installation.");

                session["SQLTESTRESULT"] = "OK";
                Report(session, message, MessageIcon.Information);
            }
            catch (Exception ex)
            {
                session.Log("TestSqlConnection failed: {0}", ex.Message);
                session["SQLTESTRESULT"] = "FAILED";
                Report(session, "Could not connect:\r\n\r\n" + ex.Message, MessageIcon.Warning);
            }

            return ActionResult.Success;
        }

        // Mirrors SuperTrigger.Web's DbConfig.BuildConnectionString -- the two must agree, or the
        // test would validate something other than what the app ends up using.
        private static string BuildSqlConnectionString(Session session)
        {
            string dataSource = (session["SQLSERVER"] ?? "").Trim();
            if (dataSource.Length == 0) throw new InvalidOperationException("Enter the SQL Server host name first.");

            string instance = (session["SQLINSTANCE"] ?? "").Trim();
            if (instance.Length > 0) dataSource += "\\" + instance;
            string port = (session["SQLPORT"] ?? "").Trim();
            if (port.Length > 0) dataSource += "," + port;

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = dataSource,
                InitialCatalog = (session["SQLDATABASE"] ?? "").Trim(),
                Encrypt = true,
                TrustServerCertificate = session["SQLTRUSTCERT"] != "0",
                ConnectTimeout = 15,
                ApplicationName = "SuperTrigger.Web Installer"
            };

            if (session["SQLAUTHMODE"] == "Sql")
            {
                builder.IntegratedSecurity = false;
                builder.UserID = session["SQLUSER"] ?? "";
                builder.Password = UnprotectSecret(session["SQLPASSWORD"]);
            }
            else
            {
                builder.IntegratedSecurity = true;
            }

            return builder.ConnectionString;
        }

        private static void Report(Session session, string message, MessageIcon icon)
        {
            // Square brackets in a message (common in SQL Server errors) would be re-read as
            // property references by the installer's formatter.
            string safe = message.Replace('[', '(').Replace(']', ')');
            var record = new Record { FormatString = safe };
            session.Message(InstallMessage.User | (InstallMessage)MessageButtons.OK | (InstallMessage)icon, record);
        }

        // DPAPI, LocalMachine scope, with the same prefix and entropy as the app's SecretProtector:
        // the installer encrypts the password here and the app (running as the IIS application pool
        // identity) decrypts it there. Changing either side means changing both.
        private const string SecretPrefix = "DPAPI:";
        private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("SuperTrigger.Web.DbConfig");

        private static string ProtectSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            // Already encrypted: prefilled from the previous install's appsettings.json untouched.
            if (value.StartsWith(SecretPrefix, StringComparison.Ordinal)) return value;

            byte[] encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), SecretEntropy, DataProtectionScope.LocalMachine);
            return SecretPrefix + Convert.ToBase64String(encrypted);
        }

        private static string UnprotectSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (!value.StartsWith(SecretPrefix, StringComparison.Ordinal)) return value;

            byte[] decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(value.Substring(SecretPrefix.Length)),
                SecretEntropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(decrypted);
        }

        private static Dictionary<string, object> GetSection(Dictionary<string, object> parent, string name)
        {
            object value;
            if (parent != null && parent.TryGetValue(name, out value))
                return value as Dictionary<string, object>;
            return null;
        }

        private static string GetValue(Dictionary<string, object> section, string name)
        {
            object value;
            if (section != null && section.TryGetValue(name, out value) && value != null)
                return value.ToString();
            return "";
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { /* best effort cleanup */ }
        }

        [CustomAction]
        public static ActionResult ConfigureIisSite(Session session)
        {
            try
            {
                session.Log("Begin ConfigureIisSite");
                var data = session.CustomActionData;

                string script = BuildConfigureScript(
                    siteName: data["SITE"],
                    appPoolName: data["POOL"],
                    physicalPath: data["DIR"],
                    httpsPort: data["HTTPSPORT"],
                    identityChoice: data["IDENT"],
                    appPoolUser: data["POOLUSER"],
                    appPoolPassword: data["POOLPASS"],
                    thumbprint: data["THUMB"]);

                return RunPowerShellScript(session, script, requireOkMarker: true);
            }
            catch (Exception ex)
            {
                session.Log("ConfigureIisSite failed: {0}\r\n{1}", ex.Message, ex.StackTrace);
                return ActionResult.Failure;
            }
        }

        [CustomAction]
        public static ActionResult RollbackIisSite(Session session)
        {
            try
            {
                session.Log("Begin RollbackIisSite");
                var data = session.CustomActionData;
                string siteName = data["SITE"];
                string appPoolName = data["POOL"];
                string httpsPort = ToPort(data["HTTPSPORT"], 443);

                var sb = new StringBuilder();
                sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
                sb.AppendLine("Import-Module WebAdministration");
                sb.AppendLine(string.Format("if (Test-Path 'IIS:\\Sites\\{0}') {{ Remove-Website -Name '{0}' }}", Escape(siteName)));
                sb.AppendLine(string.Format("if (Test-Path 'IIS:\\AppPools\\{0}') {{ Remove-WebAppPool -Name '{0}' }}", Escape(appPoolName)));
                sb.AppendLine(string.Format("& netsh http delete sslcert ipport=0.0.0.0:{0} | Out-Null", httpsPort));

                // Best effort: never fail the rollback itself.
                RunPowerShellScript(session, sb.ToString(), requireOkMarker: false);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("RollbackIisSite encountered an error (ignored): {0}", ex.Message);
                return ActionResult.Success;
            }
        }

        private static string BuildConfigureScript(string siteName, string appPoolName, string physicalPath,
            string httpsPort, string identityChoice, string appPoolUser, string appPoolPassword,
            string thumbprint)
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine("Import-Module WebAdministration -ErrorAction Stop");
            sb.AppendLine();
            sb.AppendFormat("$siteName = '{0}'\r\n", Escape(siteName));
            sb.AppendFormat("$appPoolName = '{0}'\r\n", Escape(appPoolName));
            sb.AppendFormat("$physicalPath = '{0}'\r\n", Escape(physicalPath));
            sb.AppendFormat("$httpsPort = {0}\r\n", ToPort(httpsPort, 443));
            sb.AppendFormat("$identityChoice = '{0}'\r\n", Escape(identityChoice));
            sb.AppendFormat("$appPoolUser = '{0}'\r\n", Escape(appPoolUser));
            sb.AppendFormat("$appPoolPass = '{0}'\r\n", Escape(appPoolPassword));
            sb.AppendFormat("$thumbprint = '{0}'\r\n", Escape(thumbprint));
            sb.AppendFormat("$dataDir = '{0}'\r\n", Escape(@"C:\ProgramData\HMS\SuperTriggerWeb"));
            sb.AppendFormat("$netshAppId = '{0}'\r\n", NetshAppId);
            sb.AppendLine(@"
# The ASP.NET Core SDK's generated web.config declares <security><authentication>, but IIS locks
# that section server-wide by default -- any site whose web.config even mentions it (regardless of
# value) fails to start with 500.19 until the section is unlocked here, once, machine-wide.
& ""$env:windir\System32\inetsrv\appcmd.exe"" unlock config /section:anonymousAuthentication | Out-Null
& ""$env:windir\System32\inetsrv\appcmd.exe"" unlock config /section:windowsAuthentication | Out-Null

if (Test-Path ""IIS:\AppPools\$appPoolName"") {
    # Already configured by a previous install (upgrade path) -- leave its identity/credentials
    # untouched rather than resetting to whatever was chosen/defaulted this time around.
    Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name managedRuntimeVersion -Value ''
    $existingIdentityType = (Get-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name processModel.identityType).Value
    $identityForAcl = if ($existingIdentityType -eq 'SpecificUser') {
        (Get-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name processModel.userName).Value
    } else {
        ""IIS AppPool\$appPoolName""
    }
} else {
    New-WebAppPool -Name $appPoolName | Out-Null
    Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name managedRuntimeVersion -Value ''

    if ($identityChoice -eq 'SpecificUser') {
        Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name processModel.identityType -Value SpecificUser
        Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name processModel.userName -Value $appPoolUser
        Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name processModel.password -Value $appPoolPass
        $identityForAcl = $appPoolUser
    } else {
        Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name processModel.identityType -Value ApplicationPoolIdentity
        $identityForAcl = ""IIS AppPool\$appPoolName""
    }
}

# Keep-alive settings, applied on both the fresh-install and upgrade paths so existing installs get
# corrected too. SuperTrigger.Web is not a request-driven web app -- FileWatcherService,
# MailPollingService, GraphSubscriptionService and RetentionCleanupService all live inside the worker
# process, so anything that stops the worker stops the triggers. With IIS defaults that happens twice
# over: idleTimeout kills the process 20 minutes after the last HTTP request (a Blazor circuit holds
# it open only while a browser tab is actually connected), and periodicRestart recycles it every 29
# hours. Neither one brings the process back on its own -- IIS waits for the next request, which over
# a weekend can be days away, during which no mail is polled and Graph subscriptions silently expire.
Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name recycling.periodicRestart.requests -Value 0
# Wrapped: with $ErrorActionPreference = 'Stop' a provider-level failure here would terminate the
# whole custom action and roll back the install, over a property that is empty by default anyway.
try { Clear-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name recycling.periodicRestart.schedule -ErrorAction Stop } catch { }

# AlwaysRunning starts the worker with WAS (i.e. at boot) instead of on first request. Paired with
# preloadEnabled on the site below -- AlwaysRunning alone only starts the process, preload is what
# drives the request through the pipeline that actually builds the host and its hosted services.
Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name startMode -Value AlwaysRunning

# Rapid-fail protection disables the entire app pool after 5 process crashes in 5 minutes, and it
# stays disabled until someone starts it by hand. For an unattended background-trigger service that
# turns a transient startup failure (locked DB file, expired credentials) into an indefinite outage.
# Let WAS keep restarting instead; a genuinely broken install shows up in the logs either way.
Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name failure.rapidFailProtection -Value $false

# Health monitoring: WAS pings the worker every 30s and recycles it if it stops responding within 90s.
# This is the one automatic recovery path for a hung (as opposed to crashed) process -- keep it on.
Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name processModel.pingingEnabled -Value $true

if (Test-Path ""IIS:\Sites\$siteName"") {
    # Already configured -- just repoint path/app pool in case they changed; leave the https
    # binding and bound certificate alone (avoids swapping in a brand-new self-signed cert on
    # every upgrade, and avoids re-prompting for identity/cert choices during upgrades).
    Set-ItemProperty ""IIS:\Sites\$siteName"" -Name physicalPath -Value $physicalPath
    Set-ItemProperty ""IIS:\Sites\$siteName"" -Name applicationPool -Value $appPoolName
} else {
    New-Website -Name $siteName -PhysicalPath $physicalPath -ApplicationPool $appPoolName -Port $httpsPort -Ssl -Force | Out-Null
}

# Preload: makes IIS issue a synthetic startup request so the host (and its hosted services) is built
# at boot rather than on the first real visitor. Set on the root application explicitly as well as on
# the site defaults -- applicationDefaults only covers applications created after it is set, so on the
# upgrade path the already-existing root application would otherwise keep preload off.
Set-ItemProperty ""IIS:\Sites\$siteName"" -Name applicationDefaults.preloadEnabled -Value $true
$rootAppFilter = ""system.applicationHost/sites/site[@name='$siteName']/application[@path='/']""
Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $rootAppFilter -Name preloadEnabled -Value $true

# Verify the https binding actually has a certificate attached -- covers both the fresh-install case
# above (no binding exists yet) and a subtler one: the site/app pool already existed (so the branch
# above intentionally left the binding untouched) but a *previous* run somehow lost the binding
# itself (observed in practice: a repeat same-version install regenerates a new self-signed cert
# without ever re-binding it, since only the fresh-install branch binds anything). Without this check
# that leaves the site running with no certificate at all -- IIS accepts the TCP connection but the
# TLS handshake has nothing to present, so browsers see it as a reset connection, not a clear error.
$ipport = ""0.0.0.0:$httpsPort""
$existingBinding = netsh http show sslcert ipport=$ipport 2>&1
if ($existingBinding -match 'cannot find the file specified') {
    $bindThumbprint = $thumbprint
    if ([string]::IsNullOrWhiteSpace($bindThumbprint)) {
        # No thumbprint was supplied this run (e.g. an upgrade, which skips cert selection/generation
        # entirely) -- fall back to the newest still-valid certificate already on this machine.
        $fallbackCert = Get-ChildItem Cert:\LocalMachine\My |
            Where-Object { $_.Subject -eq ""CN=$env:COMPUTERNAME"" -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
            Sort-Object NotAfter -Descending | Select-Object -First 1
        if ($fallbackCert) { $bindThumbprint = $fallbackCert.Thumbprint }
    }
    if (-not [string]::IsNullOrWhiteSpace($bindThumbprint)) {
        & netsh http delete sslcert ipport=$ipport 2>$null | Out-Null
        & netsh http add sslcert ipport=$ipport certhash=$bindThumbprint appid=$netshAppId certstorename=MY | Out-Null
    }
}

# Fail loudly rather than silently: a site with no certificate bound is not a working install,
# even though every step up to here can complete without throwing.
$finalCertCheck = netsh http show sslcert ipport=$ipport 2>&1
if ($finalCertCheck -match 'cannot find the file specified') {
    throw ""No SSL certificate could be bound to $ipport -- the site will not be reachable over HTTPS.""
}

# SuperTrigger.Web authenticates via its own ASP.NET Core Negotiate middleware, not IIS's Windows
# Authentication module -- IIS must stay anonymous or it intercepts the 401 challenge first.
Set-WebConfigurationProperty -Filter /system.webServer/security/authentication/anonymousAuthentication -Name enabled -Value true -PSPath IIS:\ -Location $siteName
Set-WebConfigurationProperty -Filter /system.webServer/security/authentication/windowsAuthentication -Name enabled -Value false -PSPath IIS:\ -Location $siteName

foreach ($path in @($physicalPath, $dataDir)) {
    if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    $acl = Get-Acl $path
    $rights = if ($path -eq $dataDir) { 'Modify' } else { 'ReadAndExecute' }
    $alreadyGranted = $acl.Access | Where-Object {
        $_.IdentityReference.Value -eq $identityForAcl -and $_.FileSystemRights.HasFlag([System.Security.AccessControl.FileSystemRights]::$rights)
    }
    if (-not $alreadyGranted) {
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($identityForAcl, $rights, 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
        Set-Acl $path $acl
    }
}

Write-Output 'IIS_CONFIG_OK'
");
            return sb.ToString();
        }

        private static string ToPort(string value, int fallback)
        {
            int port;
            return int.TryParse(value, out port) && port > 0 && port <= 65535 ? port.ToString() : fallback.ToString();
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private static string GetSysnativePath(string relativePath)
        {
            string windir = Environment.GetEnvironmentVariable("windir") ?? @"C:\Windows";
            string systemFolder = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
            return Path.Combine(windir, systemFolder, relativePath);
        }

        private static string GetNativePowerShellPath()
        {
            return GetSysnativePath(Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"));
        }

        private static ActionResult RunPowerShellScript(Session session, string script, bool requireOkMarker)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "SuperTriggerWeb_" + Guid.NewGuid().ToString("N") + ".ps1");
            try
            {
                File.WriteAllText(scriptPath, script, Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    // This CA is built x86, so plain "powershell.exe" resolves through WOW64 to the
                    // 32-bit SysWOW64 copy, which can't load IIS's 64-bit-only WebAdministration COM
                    // provider (fails with REGDB_E_CLASSNOTREG on Test-Path "IIS:\..."). Sysnative
                    // bypasses the WOW64 file-system redirector to reach the real 64-bit PowerShell.
                    FileName = GetNativePowerShellPath(),
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                string stdout, stderr;
                int exitCode;
                using (var process = Process.Start(psi))
                {
                    stdout = process.StandardOutput.ReadToEnd();
                    stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                }

                session.Log("PowerShell exit code: {0}", exitCode);
                if (!string.IsNullOrEmpty(stdout)) session.Log("PowerShell stdout: {0}", stdout);
                if (!string.IsNullOrEmpty(stderr)) session.Log("PowerShell stderr: {0}", stderr);

                if (exitCode != 0) return ActionResult.Failure;
                if (requireOkMarker && !stdout.Contains("IIS_CONFIG_OK")) return ActionResult.Failure;

                return ActionResult.Success;
            }
            finally
            {
                try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { /* best effort cleanup */ }
            }
        }
    }
}
