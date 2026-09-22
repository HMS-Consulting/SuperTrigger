<#
.SYNOPSIS
    Builds / publishes SuperTrigger.Web.

.DESCRIPTION
    Framework-dependent publish, matching exactly what CreateMSI\CreateMSI.wixproj's PublishWebApp target
    runs ("dotnet publish -c <cfg> -o <dir>"), so the output of this script is what the MSI would
    have harvested. Use build-msi.ps1 in this folder to produce the installer itself.

.EXAMPLE
    .\build-app.ps1
    .\build-app.ps1 -Configuration Debug -Clean
    .\build-app.ps1 -Output C:\inetpub\SuperTrigger.Web -AppVersion 26.8.32
    .\build-app.ps1 -BuildOnly
#>
[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    # Publish target directory. Default: <project>\publish
    [string]$Output,

    # Overrides <Version> in SuperTrigger.Web.csproj (shown on the About page).
    [string]$AppVersion,

    # Delete bin\, obj\ and the output folder before building.
    [switch]$Clean,

    # Compile only (dotnet build) - no publish output.
    [switch]$BuildOnly,

    # Print compiler/NuGet warnings. Off by default: the Mail project reference emits several hundred
    # CA1416/NU1504 warnings that bury the actual result.
    [switch]$ShowWarnings,

    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$ScriptRoot = if ($PSScriptRoot) {
    $PSScriptRoot
} elseif ($MyInvocation.MyCommand.Path) {
    Split-Path -Parent $MyInvocation.MyCommand.Path
} else {
    (Get-Location).Path
}

$ProjectPath = Join-Path $ScriptRoot "SuperTrigger.Web.csproj"
if (-not (Test-Path $ProjectPath)) {
    throw "Project not found: $ProjectPath"
}

if (-not $Output) { $Output = Join-Path $ScriptRoot "publish" }

# The project targets net8.0-windows and references Microsoft.Exchange.WebServices.dll plus
# WPF-flavoured shared libraries (Mail, HmsTeam.Shared) - it cannot be built on Linux/macOS.
if (-not $IsWindows -and $PSVersionTable.PSVersion.Major -ge 6) {
    throw "SuperTrigger.Web targets net8.0-windows and must be built on Windows."
}

$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if (-not $dotnet) { $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue }
if (-not $dotnet) {
    throw "dotnet CLI not found on PATH. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
}

# An SDK newer than 8 builds net8.0-windows fine (the 8.0 targeting pack ships with it), so accept
# any 8.x or later and only fail when nothing can target net8.0 at all.
$sdks = & $dotnet.Source --list-sdks
$sdkMajors = $sdks | ForEach-Object { if ($_ -match '^(\d+)\.') { [int]$Matches[1] } }
if (-not ($sdkMajors | Where-Object { $_ -ge 8 })) {
    throw "No .NET 8 (or newer) SDK found. Installed SDKs:`n$($sdks -join "`n")`nInstall from https://dotnet.microsoft.com/download/dotnet/8.0"
}

Write-Host "dotnet:        $($dotnet.Source)"
Write-Host "Project:       $ProjectPath"
Write-Host "Configuration: $Configuration"

if ($Clean) {
    foreach ($dir in @((Join-Path $ScriptRoot "bin"), (Join-Path $ScriptRoot "obj"), $Output)) {
        if (Test-Path $dir) {
            Write-Host "Cleaning $dir"
            Remove-Item -Path $dir -Recurse -Force
        }
    }
}

$commonArgs = @("-c", $Configuration, "--nologo", "-v", "minimal")
if (-not $ShowWarnings) { $commonArgs += "/clp:ErrorsOnly" }
if ($NoRestore) { $commonArgs += "--no-restore" }
if ($AppVersion) {
    if ($AppVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        throw "Invalid -AppVersion '$AppVersion'. Expected Major.Minor.Build[.Revision]."
    }
    $commonArgs += "/p:Version=$AppVersion"
    Write-Host "Version:       $AppVersion (overriding csproj)"
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()

if ($BuildOnly) {
    Write-Host "`nBuilding ..."
    & $dotnet.Source build $ProjectPath @commonArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

    $sw.Stop()
    Write-Host "`nBuild succeeded in $([math]::Round($sw.Elapsed.TotalSeconds, 1))s"
    return
}

Write-Host "Output:        $Output"
Write-Host "`nPublishing ..."
& $dotnet.Source publish $ProjectPath @commonArgs -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# A publish that "succeeds" but drops no entry assembly means the output folder is unusable by IIS.
$mainDll = Join-Path $Output "SuperTrigger.Web.dll"
if (-not (Test-Path $mainDll)) {
    throw "Publish completed but $mainDll is missing."
}

foreach ($required in @("web.config", "appsettings.json", "nlog.config")) {
    if (-not (Test-Path (Join-Path $Output $required))) {
        Write-Warning "Expected file not present in publish output: $required"
    }
}

$files = Get-ChildItem -Path $Output -Recurse -File
$sizeMb = [math]::Round((($files | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
$version = (Get-Item $mainDll).VersionInfo.FileVersion

$sw.Stop()
Write-Host ""
Write-Host "Publish succeeded in $([math]::Round($sw.Elapsed.TotalSeconds, 1))s"
Write-Host "  Path:    $Output"
Write-Host "  Files:   $($files.Count) ($sizeMb MB)"
Write-Host "  Version: $version"
Write-Host ""
Write-Host "Framework-dependent output - the target machine needs the ASP.NET Core 8 Hosting Bundle"
Write-Host "AND the .NET 8 Desktop Runtime (Microsoft.WindowsDesktop.App), or the app exits with 0x80008096."
