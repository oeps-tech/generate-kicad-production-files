param([string]$InstallPackage)

# Windows PowerShell is available before .NET 10 is installed. This script remains the shortcut target.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.IO.Compression.FileSystem
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$oepsDataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OEPS\KicadProductionFiles'
$oepsRuntimeRoot = Join-Path $oepsDataRoot 'runtime'

function Test-DesktopRuntime([string]$DotnetPath) {
    if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) { return $false }
    $runtimeLines = & $DotnetPath --list-runtimes 2>$null
    return ($LASTEXITCODE -eq 0 -and ($runtimeLines -match '^Microsoft.WindowsDesktop.App 10\.0\.\d+ ') -and ($runtimeLines -match '^Microsoft.NETCore.App 10\.0\.\d+ '))
}

function Find-DesktopRuntime {
    $candidates = @((Join-Path $oepsRuntimeRoot 'dotnet.exe'))
    if ($env:ProgramW6432) { $candidates += Join-Path $env:ProgramW6432 'dotnet\dotnet.exe' }
    elseif ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
    if ($env:DOTNET_ROOT_X64) { $candidates += Join-Path $env:DOTNET_ROOT_X64 'dotnet.exe' }
    foreach ($candidate in $candidates) { if (Test-DesktopRuntime $candidate) { return $candidate } }
    return $null
}

function Expand-RuntimeArchive([string]$ArchivePath, [string]$Destination) {
    $destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($archive.Entries.Count -gt 10000) { throw 'Runtime archive has too many entries.' }
        [long]$total = 0
        foreach ($entry in $archive.Entries) {
            $total += $entry.Length
            if ($total -gt 600MB) { throw 'Runtime archive exceeds the extraction limit.' }
            $entryName = $entry.FullName.Replace('/', '\')
            $segments = $entryName.TrimEnd('\').Split('\')
            foreach ($segment in $segments) {
                if (-not $segment -or $segment -eq '.' -or $segment -eq '..' -or $segment.Contains(':') -or $segment.EndsWith('.') -or $segment.EndsWith(' ')) {
                    throw 'Runtime archive contains an unsafe path.'
                }
            }
            if (($entry.ExternalAttributes -band 0x400) -ne 0 -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Runtime archive contains a link.' }
            $target = [IO.Path]::GetFullPath((Join-Path $Destination $entryName))
            if (-not $target.StartsWith($destinationRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime archive escapes its destination.' }
            if ($entryName.EndsWith('\')) { [IO.Directory]::CreateDirectory($target) | Out-Null; continue }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally { $archive.Dispose() }
}

function Install-DesktopRuntime {
    $answer = [Windows.Forms.MessageBox]::Show(
        'OEPS KiCad Production Files needs the Microsoft .NET 10 Desktop Runtime (x64). Download and install it for your Windows account now? An internet connection is required. No administrator access is needed.',
        'Install required runtime', [Windows.Forms.MessageBoxButtons]::YesNo, [Windows.Forms.MessageBoxIcon]::Question)
    if ($answer -ne [Windows.Forms.DialogResult]::Yes) { throw 'The required runtime was not installed. Start the shortcut again to retry.' }

    [IO.Directory]::CreateDirectory($oepsDataRoot) | Out-Null
    $stage = Join-Path $oepsDataRoot ('runtime-staging-' + [Guid]::NewGuid().ToString('N'))
    $stageRoot = Join-Path $stage 'runtime'
    [IO.Directory]::CreateDirectory($stageRoot) | Out-Null
    try {
        # Download both archives: WindowsDesktop alone does not provide the base host/runtime.
        $metadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' -TimeoutSec 45
        $release = $metadata.releases | Where-Object { $_.'release-version' -eq $metadata.'latest-release' } | Select-Object -First 1
        if (-not $release -or $release.'release-version' -notmatch '^10\.0\.\d+$') { throw 'Microsoft returned invalid stable runtime metadata.' }
        $runtimeAsset = $release.runtime.files | Where-Object { $_.name -eq 'dotnet-runtime-win-x64.zip' } | Select-Object -First 1
        $desktopAsset = $release.windowsdesktop.files | Where-Object { $_.name -eq 'windowsdesktop-runtime-win-x64.zip' } | Select-Object -First 1
        foreach ($asset in @($runtimeAsset, $desktopAsset)) {
            if (-not $asset -or $asset.hash -notmatch '^[0-9a-fA-F]{128}$') { throw 'Microsoft runtime archive metadata is incomplete.' }
            $uri = [Uri]$asset.url
            if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('builds.dotnet.microsoft.com', 'download.visualstudio.microsoft.com')) { throw 'Unexpected runtime download source.' }
            $archivePath = Join-Path $stage $asset.name
            Invoke-WebRequest -Uri $uri.AbsoluteUri -OutFile $archivePath -UseBasicParsing -TimeoutSec 180
            if ((Get-Item -LiteralPath $archivePath).Length -gt 250MB) { throw 'Runtime download exceeds the size limit.' }
            if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash -ne $asset.hash) { throw 'Runtime download checksum verification failed.' }
            Expand-RuntimeArchive $archivePath $stageRoot
        }
        if (-not (Test-DesktopRuntime (Join-Path $stageRoot 'dotnet.exe'))) { throw 'The downloaded Desktop Runtime did not pass its startup check.' }
        # Preserve an old/private runtime. Never overwrite runtime files in use.
        if (Test-Path -LiteralPath $oepsRuntimeRoot) {
            $oldRuntime = Join-Path $oepsDataRoot ('runtime-previous-' + [Guid]::NewGuid().ToString('N'))
            $resolvedRuntime = [IO.Path]::GetFullPath($oepsRuntimeRoot)
            $resolvedOld = [IO.Path]::GetFullPath($oldRuntime)
            $allowedRoot = [IO.Path]::GetFullPath($oepsDataRoot).TrimEnd('\') + '\'
            if (-not $resolvedRuntime.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -or -not $resolvedOld.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid runtime migration path.' }
            Move-Item -LiteralPath $resolvedRuntime -Destination $resolvedOld
        }
        $resolvedStageRoot = [IO.Path]::GetFullPath($stageRoot)
        $allowedStage = [IO.Path]::GetFullPath($stage).TrimEnd('\') + '\'
        $resolvedRuntimeTarget = [IO.Path]::GetFullPath($oepsRuntimeRoot)
        $allowedData = [IO.Path]::GetFullPath($oepsDataRoot).TrimEnd('\') + '\'
        if (-not $resolvedStageRoot.StartsWith($allowedStage, [StringComparison]::OrdinalIgnoreCase) -or -not $resolvedRuntimeTarget.StartsWith($allowedData, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid runtime install path.' }
        Move-Item -LiteralPath $resolvedStageRoot -Destination $resolvedRuntimeTarget
    }
    finally {
        $resolvedStage = [IO.Path]::GetFullPath($stage)
        $allowedRoot = [IO.Path]::GetFullPath($oepsDataRoot).TrimEnd('\') + '\'
        if ($resolvedStage.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolvedStage).StartsWith('runtime-staging-') -and (Test-Path -LiteralPath $resolvedStage)) {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
}

$bootstrapMutex = $null
try {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'This release requires 64-bit Windows.' }
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $suffix = ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes([Environment]::UserDomainName + '\' + [Environment]::UserName)))).Replace('-', '').Substring(0, 20) }
    finally { $hash.Dispose() }
    $created = $false
    $bootstrapMutex = New-Object Threading.Mutex($false, ('Local\OEPS.KicadProductionFiles.Bootstrap.' + $suffix), [ref]$created)
    if (-not $created) { return }
    $dotnetPath = Find-DesktopRuntime
    if (-not $dotnetPath) { Install-DesktopRuntime; $dotnetPath = Find-DesktopRuntime }
    if (-not $dotnetPath) { throw 'The .NET Desktop Runtime could not be found. Start the shortcut again to retry.' }
    $env:DOTNET_ROOT_X64 = Split-Path -Parent $dotnetPath
    $env:DOTNET_ROOT = $env:DOTNET_ROOT_X64
    $launcherDll = Join-Path $PSScriptRoot 'launcher\Oeps.KicadProductionFiles.Launcher.dll'
    if (-not (Test-Path -LiteralPath $launcherDll -PathType Leaf)) { throw 'Launcher files are missing. Run the full OEPS installer again.' }
    if (-not $InstallPackage) {
        $bundle = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*-win-x64.zip' -File | Select-Object -First 1
        if ($bundle) { $InstallPackage = $bundle.FullName }
    }
    $launcherArguments = '"' + $launcherDll + '"'
    if ($InstallPackage) { $launcherArguments += ' --install-package "' + [IO.Path]::GetFullPath($InstallPackage) + '"' }
    Start-Process -FilePath $dotnetPath -ArgumentList $launcherArguments -WorkingDirectory (Split-Path -Parent $launcherDll) -WindowStyle Hidden
}
catch {
    [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'OEPS KiCad Production Files could not start', [Windows.Forms.MessageBoxButtons]::OK, [Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    exit 1
}
finally { if ($bootstrapMutex) { $bootstrapMutex.Dispose() } }
