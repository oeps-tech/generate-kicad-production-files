param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
try {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'This release requires 64-bit Windows.' }
    $launcherSource = Join-Path $PSScriptRoot 'launcher'
    if (-not (Test-Path -LiteralPath (Join-Path $launcherSource 'Oeps.KicadProductionFiles.Launcher.dll'))) { throw 'Extract the entire full installer ZIP before running Install.cmd.' }
    $bundles = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*-win-x64.zip' -File)
    if ($bundles.Count -ne 1) { throw 'The installer must contain exactly one versioned app package ZIP.' }
    $bundle = $bundles[0]
    $checksumPath = $bundle.FullName + '.sha256'
    if (-not (Test-Path -LiteralPath $checksumPath)) { throw 'The bundled app checksum is missing.' }
    $checksumText = (Get-Content -LiteralPath $checksumPath -Raw).Trim().TrimStart([char]0xFEFF)
    if ($checksumText -notmatch '^([A-Fa-f0-9]{64})(?:\s+\*?([^\r\n]+))?$') { throw 'The bundled checksum is invalid.' }
    $expectedHash = $Matches[1]
    if ($Matches.ContainsKey(2) -and $Matches[2] -ne $bundle.Name) { throw 'The checksum does not name the bundled app.' }
    if ((Get-FileHash -LiteralPath $bundle.FullName -Algorithm SHA256).Hash -ne $expectedHash) { throw 'The bundled app checksum does not match. Download the installer again.' }

    # Each full installer gets an immutable bootstrap folder so an existing launcher is never overwritten.
    $oepsDataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OEPS\KicadProductionFiles'
    $bootstrapPath = Join-Path $oepsDataRoot ('bootstrap\' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($bootstrapPath) | Out-Null
    Copy-Item -LiteralPath $launcherSource -Destination (Join-Path $bootstrapPath 'launcher') -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-OEPS.ps1') -Destination $bootstrapPath
    Copy-Item -LiteralPath $bundle.FullName -Destination $bootstrapPath
    Copy-Item -LiteralPath $checksumPath -Destination $bootstrapPath

    $scriptPath = Join-Path $bootstrapPath 'Start-OEPS.ps1'
    $powershellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $shortcutArguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $scriptPath + '"'
    $shell = New-Object -ComObject WScript.Shell
    foreach ($shortcutDirectory in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
        $shortcut = $shell.CreateShortcut((Join-Path $shortcutDirectory 'OEPS KiCad Production Files.lnk'))
        $shortcut.TargetPath = $powershellPath
        $shortcut.Arguments = $shortcutArguments
        $shortcut.WorkingDirectory = $bootstrapPath
        $shortcut.Description = 'Check and export KiCad production files'
        $shortcut.IconLocation = (Join-Path $bootstrapPath 'launcher\Oeps.KicadProductionFiles.Launcher.exe') + ',0'
        $shortcut.Save()
    }
    if (-not $NoLaunch) { & $scriptPath }
    else { Write-Output ('Installed shortcuts and bootstrap in ' + $bootstrapPath + '. The runtime is checked on first launch.') }
}
catch {
    [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'OEPS installation failed', [Windows.Forms.MessageBoxButtons]::OK, [Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    exit 1
}
