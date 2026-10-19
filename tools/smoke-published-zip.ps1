$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishScript = Join-Path $PSScriptRoot 'publish-windows.ps1'
$projectPath = Join-Path $repoRoot 'MakroWerkstatt.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode) { throw 'In MakroWerkstatt.csproj fehlt die Projektversion.' }
$version = $versionNode.InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Die Projektversion muss dem Format x.y.z entsprechen.' }

$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$workRoot = Join-Path $tempBase ("MakroWerkstatt-ZipSmoke-$([guid]::NewGuid().ToString('N'))")
$workRoot = [System.IO.Path]::GetFullPath($workRoot)
$tempBasePrefix = $tempBase.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $workRoot.StartsWith($tempBasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Der temporäre Smoke-Test-Pfad liegt außerhalb des Temp-Verzeichnisses: $workRoot"
}
if (Test-Path -LiteralPath $workRoot) { throw "Der temporäre Smoke-Test-Pfad existiert bereits: $workRoot" }

$publishDirectory = Join-Path $workRoot 'publish'
$extractDirectory = Join-Path $workRoot 'extracted'
$dataDirectory = Join-Path $workRoot 'isolated-data'
$emptyDotnetRoot = Join-Path $workRoot 'no-dotnet'
$process = $null
$originalEnvironment = @{
    MAKROWERKSTATT_DATA_DIR = $env:MAKROWERKSTATT_DATA_DIR
    DOTNET_ROOT = $env:DOTNET_ROOT
    DOTNET_ROOT_X64 = $env:DOTNET_ROOT_X64
    DOTNET_MULTILEVEL_LOOKUP = $env:DOTNET_MULTILEVEL_LOOKUP
    PATH = $env:PATH
}

try {
    New-Item -ItemType Directory -Path $workRoot, $publishDirectory, $extractDirectory, $dataDirectory, $emptyDotnetRoot | Out-Null

    & $publishScript -NoShortcut -OutputDirectory $publishDirectory
    $zipPath = Join-Path $publishDirectory "MakroWerkstatt-$version-win-x64.zip"
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw "Die Veröffentlichung enthält kein ZIP: $zipPath" }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $appEntry = $archive.Entries | Where-Object { $_.FullName -ceq 'MakroWerkstatt.exe' } | Select-Object -First 1
        if ($null -eq $appEntry) { throw 'Das veröffentlichte ZIP enthält keine MakroWerkstatt.exe.' }
        $runtimeEntries = @($archive.Entries | Where-Object { $_.FullName -match '(?i)(^|/)(hostfxr|hostpolicy|coreclr|clrjit)\.dll$' })
        if ($runtimeEntries.Count -gt 0) { throw 'Das ZIP enthält separate .NET-Laufzeitdateien; die Veröffentlichung ist nicht single-file.' }
    }
    finally {
        $archive.Dispose()
    }

    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDirectory
    $exePath = Join-Path $extractDirectory 'MakroWerkstatt.exe'
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Die EXE fehlt nach dem Entpacken des veröffentlichten ZIPs.' }

    $env:MAKROWERKSTATT_DATA_DIR = $dataDirectory
    $env:DOTNET_ROOT = $emptyDotnetRoot
    $env:DOTNET_ROOT_X64 = $emptyDotnetRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:PATH = ($originalEnvironment.PATH -split [System.IO.Path]::PathSeparator |
        Where-Object { $_ -and $_ -notmatch '(?i)[\\/]dotnet(?:[\\/]|$)' }) -join [System.IO.Path]::PathSeparator

    $process = Start-Process -FilePath $exePath -WorkingDirectory $extractDirectory -PassThru
    $logDirectory = Join-Path $dataDirectory 'logs'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $startupVerified = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw "MakroWerkstatt wurde beim Start mit Exitcode $($process.ExitCode) beendet." }

        $logFile = Get-ChildItem -LiteralPath $logDirectory -Filter 'makrowerkstatt-*.log' -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $logFile) {
            $logText = Get-Content -LiteralPath $logFile.FullName -Raw -ErrorAction SilentlyContinue
            $process.Refresh()
            if ($logText -match 'App\.OnStartup.*Application started' -and $process.MainWindowHandle -ne [IntPtr]::Zero) {
                $startupVerified = $true
                break
            }
        }
        Start-Sleep -Milliseconds 250
    }

    if (-not $startupVerified) { throw 'Die Anwendung hat in 30 Sekunden kein Hauptfenster und keinen Start-Logeintrag erzeugt.' }
    Write-Host 'Published ZIP smoke test passed: ZIP extracted and WPF main window started with .NET lookup paths isolated.'
}
finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(10000)) {
                $process.Kill()
                $process.WaitForExit()
            }
        }
        $process.Dispose()
    }

    foreach ($name in @('MAKROWERKSTATT_DATA_DIR', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_MULTILEVEL_LOOKUP', 'PATH')) {
        $value = $originalEnvironment[$name]
        if ($null -eq $value) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item -LiteralPath "Env:$name" -Value $value }
    }

    $resolvedWorkRoot = [System.IO.Path]::GetFullPath($workRoot)
    if ($resolvedWorkRoot.StartsWith($tempBasePrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedWorkRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
    }
}
