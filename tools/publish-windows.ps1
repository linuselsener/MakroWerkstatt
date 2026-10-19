[CmdletBinding()]
param(
    [switch]$NoShortcut,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'MakroWerkstatt.csproj'
$defaultPublishFolder = Join-Path $repoRoot 'dist'
$defaultExe = Join-Path $defaultPublishFolder 'MakroWerkstatt.exe'
$publishFolder = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $runningFromDefaultFolder = Get-Process -Name 'MakroWerkstatt' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.Equals($defaultExe, [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ($runningFromDefaultFolder) {
        Join-Path $repoRoot ("dist-update-$(Get-Date -Format 'yyyyMMdd-HHmmss')")
    } else {
        $defaultPublishFolder
    }
} else {
    [System.IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
}

[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode) { throw "In MakroWerkstatt.csproj fehlt die Projektversion." }
$version = $versionNode.InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Die Projektversion muss dem Format x.y.z entsprechen." }

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

$zipPath = Join-Path $publishFolder "MakroWerkstatt-$version-win-x64.zip"
$checksumPath = Join-Path $publishFolder 'SHA256SUMS.txt'
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
if (Test-Path -LiteralPath $checksumPath) { Remove-Item -LiteralPath $checksumPath -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $publishFiles = Get-ChildItem -LiteralPath $publishFolder -File -Recurse | Where-Object {
        $_.Name -ne 'SHA256SUMS.txt' -and $_.Name -notmatch '^MakroWerkstatt-\d+\.\d+\.\d+-win-x64\.zip$'
    }
    if (-not $publishFiles) { throw "Die Veröffentlichung enthält keine Dateien zum Verpacken." }

    foreach ($file in $publishFiles) {
        $entryName = [System.IO.Path]::GetRelativePath($publishFolder, $file.FullName).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
catch {
    if ($null -ne $archive) {
        $archive.Dispose()
        $archive = $null
    }
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    throw
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
}

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$exeHash = (Get-FileHash -LiteralPath $appExe -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumLines = @(
    "$zipHash  $(Split-Path -Leaf $zipPath)"
    "$exeHash  $(Split-Path -Leaf $appExe)"
)
[System.IO.File]::WriteAllLines($checksumPath, [string[]]$checksumLines, [System.Text.UTF8Encoding]::new($false))

if (-not $NoShortcut) {
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
    Write-Host "Desktop-Verknüpfung: $shortcutPath"
} else {
    Write-Host 'Desktop-Verknüpfung wurde übersprungen (-NoShortcut).'
}

Write-Host "Veröffentlichung: $appExe"
Write-Host "ZIP: $zipPath"
Write-Host "SHA-256-Prüfsummen: $checksumPath"
