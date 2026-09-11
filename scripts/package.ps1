param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.1',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
function New-PortableZip([string]$Directory, [string]$ZipPath) {
    $sourceRoot = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    $zip = [IO.Compression.ZipFile]::Open($ZipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $Directory -File -Recurse) {
            if ($file.Extension -eq '.pdb') { continue }
            $entryName = $file.FullName.Substring($sourceRoot.Length).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repoRoot
try {
    if ($Dotnet -eq 'dotnet') {
        foreach ($candidate in @('.tools\dotnet\dotnet.exe', '..\raw-material-sticker\.tools\dotnet\dotnet.exe')) {
            $candidatePath = [IO.Path]::GetFullPath((Join-Path $repoRoot $candidate))
            if (Test-Path -LiteralPath $candidatePath -PathType Leaf) { $Dotnet = $candidatePath; break }
        }
    }
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.local\dotnet'
    $env:NUGET_PACKAGES = Join-Path $repoRoot '.local\nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $outputRoot = Join-Path $repoRoot 'artifacts'
    $packageName = "Oeps.KicadProductionFiles-$Version-win-x64.zip"
    $packagePath = Join-Path $outputRoot $packageName
    $installerName = "Oeps.KicadProductionFiles-$Version-setup-win-x64.msi"
    $installerPath = Join-Path $outputRoot $installerName
    foreach ($artifactPath in @($packagePath, ($packagePath + '.sha256'), $installerPath, ($installerPath + '.sha256'))) {
        if (Test-Path -LiteralPath $artifactPath) { throw "Artifact already exists: $artifactPath. Use a new version or archive the existing artifact first." }
    }
    $stage = Join-Path $outputRoot ('package-' + [Guid]::NewGuid().ToString('N'))
    $appStage = Join-Path $stage 'app-package\app'
    $fullStage = Join-Path $stage 'full-installer'
    New-Item -ItemType Directory -Force -Path $appStage, $fullStage | Out-Null
    & $Dotnet build Oeps.KicadProductionFiles.sln -c Release -p:Version=$Version --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $Dotnet run --project tests/Oeps.KicadProductionFiles.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
    & $Dotnet publish src/Oeps.KicadProductionFiles.App -c Release -r win-x64 --self-contained false -p:Version=$Version -p:CopyOutputSymbolsToPublishDirectory=false -o $appStage --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    & $Dotnet publish src/Oeps.KicadProductionFiles.Launcher -c Release -r win-x64 --self-contained false -p:Version=$Version -p:CopyOutputSymbolsToPublishDirectory=false -p:AppHostRelativeDotNet=../runtime -p:AppHostDotNetSearch=AppRelative -o (Join-Path $fullStage 'launcher') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed.' }
    $manifest = @{ version=$Version; runtimeMajor=10; architecture='x64'; executable='Oeps.KicadProductionFiles.App.exe' } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $appStage 'update-manifest.json'), $manifest, [Text.UTF8Encoding]::new($false))
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-PortableZip (Split-Path -Parent $appStage) $packagePath
    $checksum = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $packageName
    [IO.File]::WriteAllText(($packagePath + '.sha256'), ($checksum + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $packagePath, ($packagePath + '.sha256') -Destination $fullStage
    & (Join-Path $PSScriptRoot 'Build-Msi.ps1') -Stage $fullStage -Version $Version -Output $installerPath -Dotnet $Dotnet
    $installerChecksum = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $installerName
    [IO.File]::WriteAllText(($installerPath + '.sha256'), ($installerChecksum + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Write-Output "App package: $packagePath"
    Write-Output "Initial installer: $installerPath"
    Write-Output "Build staging retained for inspection: $stage"
}
finally { Pop-Location }
