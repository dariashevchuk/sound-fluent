$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $projectRoot 'artifacts\startup'
$executable = Join-Path $publishDirectory 'SoundFluent.exe'
$startupDirectory = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupDirectory 'SoundFluent.lnk'

# Publish to a stable location so Windows does not depend on a terminal or
# the build output under bin/, which can be removed by dotnet clean.
dotnet publish (Join-Path $projectRoot 'SoundFluent.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    --output $publishDirectory

if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $executable)) {
    throw 'Publishing SoundFluent failed; the startup shortcut was not changed.'
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executable
$shortcut.WorkingDirectory = $publishDirectory
$shortcut.Description = 'Start SoundFluent in the tray when you sign in'
$shortcut.Save()

Write-Host "SoundFluent will start when you sign in: $shortcutPath"
Write-Host "Executable: $executable"
