$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class VerifyUiNativeMethods
{
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@

$root = [System.Windows.Automation.AutomationElement]::RootElement
$trueCondition = [System.Windows.Automation.Condition]::TrueCondition

function Find-AppWindow {
    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $trueCondition)
    foreach ($window in $windows) {
        if ($window.Current.Name -eq 'MakroWerkstatt' -and $window.Current.ProcessId -eq $appProcess.Id) { return $window }
    }
    return $null
}

function Wait-AppDialog([string]$title) {
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $trueCondition)
        foreach ($window in $windows) {
            if ($window.Current.ProcessId -eq $appProcess.Id -and $window.Current.Name -eq $title -and
                $window.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) {
                return $window
            }
        }
        Start-Sleep -Milliseconds 150
    }
    $visibleWindows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $trueCondition)
    foreach ($window in $visibleWindows) {
        Write-Host "UI window: PID=$($window.Current.ProcessId) Type=$($window.Current.ControlType.ProgrammaticName) Name=$($window.Current.Name)"
    }
    throw "Dialog wurde nicht angezeigt: $title"
}

function Find-ButtonLike($window, [string]$text) {
    $elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $trueCondition)
    $firstNamed = $null
    foreach ($element in $elements) {
        if (-not $element.Current.Name.Contains($text)) { continue }
        if ($null -eq $firstNamed) { $firstNamed = $element }
        try {
            $null = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            return $element
        }
        catch {
            try {
                $null = $element.GetCurrentPattern([System.Windows.Automation.LegacyIAccessiblePattern]::Pattern)
                return $element
            }
            catch {
                continue
            }
        }
    }
    return $firstNamed
}

function Click-ButtonLike($window, [string]$text) {
    $button = Find-ButtonLike $window $text
    if ($null -eq $button) { throw "Schaltfläche nicht gefunden: $text" }
    try {
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    catch {
        try {
            $button.GetCurrentPattern([System.Windows.Automation.LegacyIAccessiblePattern]::Pattern).DoDefaultAction()
        }
        catch {
            $commandId = if ($button.Current.Name -eq 'Ja') { 6 } else { 1 }
            $dialogHandle = [IntPtr]$window.Current.NativeWindowHandle
            if ($dialogHandle -eq [IntPtr]::Zero) { throw 'Der Windows-Dialog besitzt kein zugängliches Fensterhandle.' }
            $null = [VerifyUiNativeMethods]::SendMessage($dialogHandle, 0x0111, [IntPtr]$commandId, [IntPtr]::Zero)
        }
    }
}

function Wait-Status([string]$status) {
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $window = Find-AppWindow
        if ($null -ne $window) {
            $textElements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Text))
            foreach ($element in $textElements) {
                if ($element.Current.Name -eq $status) { return $window }
            }
        }
        Start-Sleep -Milliseconds 150
    }
    throw "Statuswechsel nicht sichtbar: $status"
}

function Wait-TextLike([string]$pattern) {
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $window = Find-AppWindow
        if ($null -ne $window) {
            $elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Text))
            foreach ($element in $elements) {
                if ($element.Current.Name -like $pattern) { return $window }
            }
        }
        Start-Sleep -Milliseconds 150
    }
    throw "Text wurde nicht sichtbar: $pattern"
}

function Get-MacroCount($window) {
    $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text))
    foreach ($element in $texts) {
        if ($element.Current.Name -match '^([\d\., ]+) Makros?$') {
            $count = $matches[1] -replace '\D', ''
            return [int]::Parse($count, [System.Globalization.CultureInfo]::InvariantCulture)
        }
    }
    throw 'Die Anzahl der lokalen Makros wurde in der Bibliothek nicht angezeigt.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$appExe = Join-Path $repoRoot 'bin\Release\net8.0-windows\MakroWerkstatt.exe'
if (-not (Test-Path -LiteralPath $appExe)) {
    throw 'Die Release-App fehlt. Bitte zuerst dotnet build .\MakroWerkstatt.csproj -c Release ausführen.'
}
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('MakroWerkstatt-verify-ui-' + [guid]::NewGuid().ToString('N'))
$macroFolder = Join-Path $testRoot 'Makros'
$previousDataDirectory = $env:MAKROWERKSTATT_DATA_DIR
$appProcess = $null
$env:MAKROWERKSTATT_DATA_DIR = $testRoot
try {
    $appProcess = Start-Process -FilePath $appExe -PassThru
}
catch {
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTestRoot).StartsWith('MakroWerkstatt-verify-ui-', [System.StringComparison]::Ordinal) -and
        [System.IO.Directory]::Exists($resolvedTestRoot)) {
        [System.IO.Directory]::Delete($resolvedTestRoot, $true)
    }
    throw
}
finally {
    $env:MAKROWERKSTATT_DATA_DIR = $previousDataDirectory
}
$createdTestFiles = [System.Collections.Generic.List[string]]::new()

try {
    $window = $null
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $window = Find-AppWindow
        if ($null -ne $window) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($null -eq $window) { throw 'Die isolierte MakroWerkstatt-Testinstanz wurde nicht geöffnet.' }
    $baselineMacroCount = Get-MacroCount $window
    $encryptIdCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'EncryptMacrosCheckBox')
    $encryptCheckBox = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $encryptIdCondition)
    if ($null -eq $encryptCheckBox -or
        $encryptCheckBox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne
            [System.Windows.Automation.ToggleState]::On) {
        throw 'Die standardmäßig aktivierte Makroverschlüsselung wurde nicht angezeigt.'
    }

    Click-ButtonLike $window 'Neue Aufnahme'
    $dialog = Wait-AppDialog 'Aufnahme starten · Datenschutz'
    $ok = Find-ButtonLike $dialog 'OK'
    if ($null -eq $ok) { throw 'Die Startbestätigung wurde nicht gefunden.' }
    Click-ButtonLike $dialog 'OK'
    $window = Wait-Status 'AUFNAHME'

    Click-ButtonLike $window 'Pause'
    $window = Wait-Status 'PAUSIERT'
    Click-ButtonLike $window 'Fortsetzen'
    $window = Wait-Status 'AUFNAHME'
    Click-ButtonLike $window 'Aufnahme löschen'
    $window = Wait-Status 'BEREIT'

    $id = [guid]::NewGuid()
    $testName = 'UI-Smoke-' + $id.ToString('N').Substring(0, 8)
    $now = [DateTimeOffset]::UtcNow.ToString('o')
    $document = @{
        formatVersion = 1
        application = 'MakroWerkstatt'
        id = $id.ToString()
        name = $testName
        createdUtc = $now
        updatedUtc = $now
        actions = @(
            @{ atMs = 0; type = 'mouseMove'; x = 0; y = 0; keyCode = 0; scanCode = 0; rawFlags = 0; wheelDelta = 0; button = '' },
            @{ atMs = 60000; type = 'mouseMove'; x = 0; y = 0; keyCode = 0; scanCode = 0; rawFlags = 0; wheelDelta = 0; button = '' }
        )
    } | ConvertTo-Json -Depth 6
    $testPath = Join-Path $macroFolder ($id.ToString('N') + '.json')
    [System.IO.File]::WriteAllText($testPath, $document, [System.Text.UTF8Encoding]::new($false))
    $createdTestFiles.Add([System.IO.Path]::GetFullPath($testPath))

    $window = Find-AppWindow
    Click-ButtonLike $window '↻'
    $window = Wait-TextLike "$testName"
    $macroText = Find-ButtonLike $window $testName
    if ($null -eq $macroText) {
        $elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, $testName))
        if ($elements.Count -eq 0) { throw 'Das lokale Testmakro wurde in der Bibliothek nicht angezeigt.' }
        $listItem = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($elements[0])
        $listItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
    else {
        $listItem = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($macroText)
        $listItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }

    Start-Sleep -Milliseconds 250
    $window = Find-AppWindow
    Click-ButtonLike $window 'Wiedergabe starten'
    $window = Wait-TextLike 'Wiedergabe startet in*'
    Click-ButtonLike $window 'Wiedergabe stoppen'
    $window = Wait-Status 'BEREIT'

    Click-ButtonLike $window 'Makro löschen'
    $dialog = Wait-AppDialog 'Makro löschen'
    Click-ButtonLike $dialog 'Ja'
    Start-Sleep -Milliseconds 300
    if ([System.IO.File]::Exists($testPath)) { throw 'Das gespeicherte Testmakro wurde nicht gelöscht.' }
    $window = Find-AppWindow
    $currentMacroCount = Get-MacroCount $window
    if ($currentMacroCount -ne $baselineMacroCount) { throw 'Die Makrobibliothek zeigte nach dem Test nicht wieder die ursprüngliche Anzahl.' }

    Write-Host 'OK: isolierter Datenspeicher, standardmäßige Verschlüsselung, Datenschutz-Hinweis, Aufnahme, Pause/Fortsetzen, Verwerfen, lokales JSON öffnen, Countdown, Wiedergabestopp und Makro löschen.'
}
finally {
    try {
        $window = Find-AppWindow
        if ($null -ne $window) {
            $stop = Find-ButtonLike $window 'Wiedergabe stoppen'
            if ($null -ne $stop -and $stop.Current.IsEnabled) {
                $stop.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 350
            }
            $discard = Find-ButtonLike $window 'Aufnahme löschen'
            if ($null -ne $discard -and $discard.Current.IsEnabled) {
                $discard.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            }
            foreach ($testFile in $createdTestFiles) {
                $resolvedFolder = [System.IO.Path]::GetFullPath($macroFolder) + [System.IO.Path]::DirectorySeparatorChar
                if ($testFile.StartsWith($resolvedFolder, [System.StringComparison]::OrdinalIgnoreCase) -and [System.IO.File]::Exists($testFile)) {
                    [System.IO.File]::Delete($testFile)
                }
            }
            if ($createdTestFiles.Count -gt 0) {
                $window = Find-AppWindow
                if ($null -ne $window) { Click-ButtonLike $window '↻' }
            }
        }
        if ($null -ne $appProcess -and -not $appProcess.HasExited) {
            $appProcess.CloseMainWindow() | Out-Null
            if (-not $appProcess.WaitForExit(5000)) {
                Stop-Process -Id $appProcess.Id -Force
                if (-not $appProcess.WaitForExit(5000)) {
                    throw 'Die isolierte MakroWerkstatt-Testinstanz wurde nicht beendet.'
                }
            }
        }
    }
    finally {
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
        if ($resolvedTestRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolvedTestRoot).StartsWith('MakroWerkstatt-verify-ui-', [System.StringComparison]::Ordinal) -and
            [System.IO.Directory]::Exists($resolvedTestRoot)) {
            [System.IO.Directory]::Delete($resolvedTestRoot, $true)
            if ([System.IO.Directory]::Exists($resolvedTestRoot)) {
                throw 'Der temporäre UI-Testordner konnte nicht vollständig entfernt werden.'
            }
        }
    }
}
