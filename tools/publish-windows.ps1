$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'MakroWerkstatt.csproj'
$defaultPublishFolder = Join-Path $repoRoot 'dist'
$defaultExe = Join-Path $defaultPublishFolder 'MakroWerkstatt.exe'
$runningFromDefaultFolder = Get-Process -Name 'MakroWerkstatt' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.Equals($defaultExe, [StringComparison]::OrdinalIgnoreCase) } |
    Select-Object -First 1
$publishFolder = if ($runningFromDefaultFolder) {
    Join-Path $repoRoot ("dist-update-$(Get-Date -Format 'yyyyMMdd-HHmmss')")
} else {
    $defaultPublishFolder
}

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    --source 'https://api.nuget.org/v3/index.json' `
    -o $publishFolder
if ($LASTEXITCODE -ne 0) { throw "dotnet publish wurde mit Code $LASTEXITCODE beendet." }

$appExe = Join-Path $publishFolder 'MakroWerkstatt.exe'
if (-not (Test-Path -LiteralPath $appExe)) { throw "Die Veröffentlichung enthält keine EXE: $appExe" }

$desktopFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$shortcutPath = Join-Path $desktopFolder 'MakroWerkstatt.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $appExe
$shortcut.WorkingDirectory = $publishFolder
$shortcut.Description = 'MakroWerkstatt – lokale Maus- und Tastaturmakros'
$shortcut.IconLocation = "$appExe,0"
$shortcut.WindowStyle = 1
$shortcut.Save()

Write-Host "Veröffentlichung: $appExe"
Write-Host "Desktop-Verknüpfung: $shortcutPath"
