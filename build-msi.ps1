[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Platform = "x86",
    [string]$WixProjPath,
    [string]$DistDir
)

$ErrorActionPreference = "Stop"

$ScriptRoot = if ($PSScriptRoot) {
    $PSScriptRoot
} elseif ($MyInvocation.MyCommand.Path) {
    Split-Path -Parent $MyInvocation.MyCommand.Path
} else {
    (Get-Location).Path
}

if (-not $WixProjPath) { $WixProjPath = Join-Path $ScriptRoot "CreateMSI\CreateMSI.wixproj" }
# The repo-root dist\ folder, one level above SuperTrigger.Web, is where the finished MSI is collected.
if (-not $DistDir) { $DistDir = Join-Path (Split-Path -Parent $ScriptRoot) "dist" }

function Get-NextAppVersion {
    param([string]$WixProjDir)

    # Persists the last version this script produced, so every build gets a strictly higher
    # ProductVersion than whatever is already installed -- required for WiX's upgrade detection
    # (Product.wxs blocks same-version and downgrade reinstalls outright).
    $versionFile = Join-Path $WixProjDir "ProductVersion.txt"

    $current = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { "26.8.0" }

    if ($current -notmatch '^\d+\.\d+\.\d+$') {
        throw "Invalid version '$current' in $versionFile. Expected Major.Minor.Build (e.g. 26.8.8)."
    }

    $parts = $current.Split('.')
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $build = [int]$parts[2] + 1

    if ($build -gt 65535) {
        throw "Build number overflowed 65535 in $versionFile - bump Major/Minor manually and reset Build to 0."
    }

    $next = "$major.$minor.$build"
    Set-Content -Path $versionFile -Value $next -NoNewline
    return $next
}

function Find-MSBuild {
    # WiX v3 targets (Wix.targets) use MSBuild tasks from Microsoft.Build.Tasks.v4.0, which only the
    # legacy .NET Framework MSBuild ships. Neither `dotnet msbuild` nor the modern VS-installed MSBuild
    # can load that assembly, so the classic Framework64 MSBuild must be preferred when present.
    $legacyMsBuild = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
    if (Test-Path $legacyMsBuild) { return $legacyMsBuild }

    $legacyMsBuild32 = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
    if (Test-Path $legacyMsBuild32) { return $legacyMsBuild32 }

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vsPath = & $vswhere -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
        if ($vsPath) { return $vsPath }
    }

    $msbuildCmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($msbuildCmd) { return $msbuildCmd.Source }

    throw "MSBuild.exe not found (checked legacy .NET Framework MSBuild, vswhere, and PATH). Note: WiX v3 projects need the legacy Framework MSBuild, not 'dotnet msbuild' or VS Build Tools' MSBuild - those fail with MSB4062 loading Microsoft.Build.Tasks.v4.0."
}

if (-not (Test-Path $WixProjPath)) {
    throw "WiX project not found: $WixProjPath"
}

$msbuild = Find-MSBuild
Write-Host "Using MSBuild: $msbuild"

$wixProjDir = Split-Path -Parent $WixProjPath
$appVersion = Get-NextAppVersion -WixProjDir $wixProjDir
Write-Host "Bumped MSI ProductVersion to $appVersion"

Write-Host "Building MSI ($Configuration|$Platform, v$appVersion) from $WixProjPath ..."
& $msbuild $WixProjPath "/p:Configuration=$Configuration" "/p:Platform=$Platform" "/p:AppVersion=$appVersion" "/t:Rebuild" "/nologo" "/verbosity:minimal"

if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed with exit code $LASTEXITCODE"
}

$outputDir = Join-Path $wixProjDir "bin\$Configuration"
$msiFile = Get-ChildItem -Path $outputDir -Filter "*.msi" -ErrorAction Stop | Select-Object -First 1

if (-not $msiFile) {
    throw "No MSI file found in $outputDir after build."
}

if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
}

$destination = Join-Path $DistDir $msiFile.Name
Copy-Item -Path $msiFile.FullName -Destination $destination -Force

Write-Host "MSI ready: $destination"
