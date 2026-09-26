#Requires -Version 5.1
<#
.SYNOPSIS
    Build gsplat-convert-ffi native plugins for Windows (x86_64) and/or Android (arm64-v8a),
    and copy the resulting binaries into the Unity package's Plugins directories.

.DESCRIPTION
    Android is cross-compiled without cargo-ndk: the NDK's clang wrapper is pointed to
    directly via CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER (and CC_/AR_ env vars), so no
    NDK path is ever committed to a .cargo/config.toml.

.PARAMETER Target
    Which platform(s) to build: windows, android, or all (default).

.PARAMETER NdkPath
    Explicit path to the Android NDK root. If omitted, the script searches, in order:
      1. This parameter
      2. $env:ANDROID_NDK_ROOT
      3. <Unity install>/Editor/Data/PlaybackEngines/AndroidPlayer/NDK, trying
         $env:UNITY_EDITOR_ROOT\<version> (for Editors installed outside the Hub
         default, e.g. D:\Unity) and C:\Program Files\Unity\Hub\Editor\<version>,
         where <version> is read from ProjectSettings/ProjectVersion.txt.

.EXAMPLE
    pwsh -File tools/build-native.ps1 -Target all
#>
[CmdletBinding()]
param(
    [ValidateSet("windows", "android", "all")]
    [string]$Target = "all",

    [string]$NdkPath
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$CrateDir = Join-Path $RepoRoot "tools/splat-pipeline"
$TargetDir = Join-Path $CrateDir "target"
$PluginsRoot = Join-Path $RepoRoot "Packages/com.gsplat.lod/Runtime/Plugins"

function Write-Step($msg) {
    Write-Host ""
    Write-Host "==> $msg" -ForegroundColor Cyan
}

function Write-Ok($msg) {
    Write-Host "    OK: $msg" -ForegroundColor Green
}

function Copy-Artifact($srcPath, $destPath) {
    if (-not (Test-Path $srcPath)) {
        throw "Build artifact not found: $srcPath"
    }
    $destDir = Split-Path -Parent $destPath
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
    try {
        Copy-Item -Path $srcPath -Destination $destPath -Force
    } catch {
        $msg = "Failed to copy '$srcPath' -> '$destPath': $($_.Exception.Message)"
        # A sharing-violation IOException here almost always means Unity
        # Editor still has the previous build of this native plugin loaded
        # (Editor never unloads native plugins without a full restart) —
        # call that out explicitly instead of leaving the caller to guess.
        if ($_.Exception -is [System.IO.IOException]) {
            $msg += "`nThis looks like a file-lock error: Unity Editor may still have the native plugin loaded. Close the Editor and re-run this script."
        }
        throw $msg
    }
    $size = (Get-Item $destPath).Length
    Write-Ok ("copied -> {0} ({1:N0} bytes)" -f $destPath, $size)
}

function Find-NdkPath {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path $Explicit) { return $Explicit }
        throw "NdkPath '$Explicit' does not exist."
    }

    if ($env:ANDROID_NDK_ROOT) {
        if (Test-Path $env:ANDROID_NDK_ROOT) {
            return $env:ANDROID_NDK_ROOT
        } else {
            Write-Warning "ANDROID_NDK_ROOT is set to '$($env:ANDROID_NDK_ROOT)' but it does not exist; continuing search."
        }
    }

    $versionFile = Join-Path $RepoRoot "ProjectSettings/ProjectVersion.txt"
    if (-not (Test-Path $versionFile)) {
        throw "Cannot locate NDK: ProjectSettings/ProjectVersion.txt not found and no -NdkPath/ANDROID_NDK_ROOT given."
    }

    $versionLine = Get-Content $versionFile | Where-Object { $_ -match '^m_EditorVersion:\s*(\S+)' } | Select-Object -First 1
    if (-not $versionLine) {
        throw "Cannot parse Unity version from $versionFile."
    }
    $unityVersion = $Matches[1]

    $ndkSuffix = "Editor\Data\PlaybackEngines\AndroidPlayer\NDK"
    $candidates = @()
    if ($env:UNITY_EDITOR_ROOT) {
        $candidates += Join-Path $env:UNITY_EDITOR_ROOT "$unityVersion\$ndkSuffix"
    }
    $candidates += "C:\Program Files\Unity\Hub\Editor\$unityVersion\$ndkSuffix"

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "Could not find Android NDK. Tried: ANDROID_NDK_ROOT, $($candidates -join ', '). Pass -NdkPath explicitly."
}

function Build-Windows {
    Write-Step "Building Windows (x86_64-pc-windows-msvc) release"
    Push-Location $CrateDir
    try {
        cargo build -p gsplat-convert-ffi --release
        if ($LASTEXITCODE -ne 0) { throw "cargo build (windows) failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
    Write-Ok "cargo build (windows) succeeded"

    $src = Join-Path $TargetDir "release/gsplat_convert.dll"
    $dest = Join-Path $PluginsRoot "x86_64/gsplat_convert.dll"
    Copy-Artifact $src $dest
}

function Build-Android {
    param([string]$NdkPath)

    Write-Step "Locating Android NDK"
    $ndk = Find-NdkPath -Explicit $NdkPath
    Write-Ok "NDK path: $ndk"

    $sourceProps = Join-Path $ndk "source.properties"
    if (Test-Path $sourceProps) {
        $revLine = Get-Content $sourceProps | Where-Object { $_ -match '^Pkg\.Revision\s*=\s*(.+)$' } | Select-Object -First 1
        if ($revLine) {
            Write-Ok "NDK Pkg.Revision: $($Matches[1].Trim())"
        } else {
            Write-Warning "source.properties found but Pkg.Revision line not present."
        }
    } else {
        Write-Warning "source.properties not found at $sourceProps; cannot record NDK revision."
    }

    $binDir = Join-Path $ndk "toolchains/llvm/prebuilt/windows-x86_64/bin"
    $linker = Join-Path $binDir "aarch64-linux-android32-clang.cmd"
    $cc = $linker
    $ar = Join-Path $binDir "llvm-ar.exe"

    if (-not (Test-Path $linker)) {
        throw "Android linker not found: $linker`nExpected NDK toolchain bin dir: $binDir. minSdk must match the '32' in this filename (AndroidMinSdkVersion: 32)."
    }
    if (-not (Test-Path $ar)) {
        throw "llvm-ar not found: $ar"
    }
    Write-Ok "linker/cc: $linker"
    Write-Ok "ar: $ar"

    Write-Step "Building Android (aarch64-linux-android) release"
    Push-Location $CrateDir
    try {
        $env:CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER = $linker
        $env:CC_aarch64_linux_android = $cc
        $env:AR_aarch64_linux_android = $ar
        try {
            cargo build -p gsplat-convert-ffi --release --target aarch64-linux-android
            if ($LASTEXITCODE -ne 0) { throw "cargo build (android) failed with exit code $LASTEXITCODE" }
        } finally {
            Remove-Item Env:\CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER -ErrorAction SilentlyContinue
            Remove-Item Env:\CC_aarch64_linux_android -ErrorAction SilentlyContinue
            Remove-Item Env:\AR_aarch64_linux_android -ErrorAction SilentlyContinue
        }
    } finally {
        Pop-Location
    }
    Write-Ok "cargo build (android) succeeded"

    $src = Join-Path $TargetDir "aarch64-linux-android/release/libgsplat_convert.so"
    $dest = Join-Path $PluginsRoot "Android/arm64-v8a/libgsplat_convert.so"
    Copy-Artifact $src $dest
}

# Strip local absolute paths (user name in the cargo registry path, repo location)
# from panic messages embedded in the shipped binaries.
$CargoHome = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $HOME ".cargo" }
$env:RUSTFLAGS = "--remap-path-prefix=$CargoHome=/cargo --remap-path-prefix=$CrateDir=/splat-pipeline"

try {
    if ($Target -eq "windows" -or $Target -eq "all") {
        Build-Windows
    }
    if ($Target -eq "android" -or $Target -eq "all") {
        Build-Android -NdkPath $NdkPath
    }
    Write-Host ""
    Write-Host "All requested builds completed successfully." -ForegroundColor Green
    exit 0
} catch {
    Write-Host ""
    Write-Host "BUILD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
