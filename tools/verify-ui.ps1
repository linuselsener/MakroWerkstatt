$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$trueCondition = [System.Windows.Automation.Condition]::TrueCondition

function Find-AppWindow {
    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $trueCondition)
    foreach ($window in $windows) {
        if ($window.Current.Name -eq 'MakroWerkstatt') { return $window }
    }
    return $null
}

function Find-ButtonLike($window, [string]$text) {
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button))
    foreach ($button in $buttons) {
        if ($button.Current.Name.Contains($text)) { return $button }
    }
    return $null
}

function Click-ButtonLike($window, [string]$text) {
    $button = Find-ButtonLike $window $text
    if ($null -eq $button) { throw "Schaltfläche nicht gefunden: $text" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
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

$window = Find-AppWindow
if ($null -eq $window) { throw 'MakroWerkstatt muss geöffnet sein, bevor dieser UI-Smoke-Test läuft.' }
$baselineMacroCount = Get-MacroCount $window
$macroFolder = Join-Path $env:LOCALAPPDATA 'MakroWerkstatt\Makros'
$createdTestFiles = [System.Collections.Generic.List[string]]::new()

try {
    Click-ButtonLike $window 'Neue Aufnahme'
    Start-Sleep -Milliseconds 400
    $dialog = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $window = Find-AppWindow
        $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Text))
        foreach ($textElement in $texts) {
            if ($textElement.Current.Name.Contains('Texteingaben können Passwörter')) { $dialog = $window; break }
        }
        if ($null -ne $dialog) { break }
        Start-Sleep -Milliseconds 150
    }
    if ($null -eq $dialog) { throw 'Der Datenschutz-Hinweis vor der Aufnahme wurde nicht angezeigt.' }
    $ok = Find-ButtonLike $dialog 'OK'
    if ($null -eq $ok) { throw 'Die Startbestätigung wurde nicht gefunden.' }
    $ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
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
    $window = Wait-TextLike '*endgültig löschen*'
    Click-ButtonLike $window 'Ja'
    Start-Sleep -Milliseconds 300
    if ([System.IO.File]::Exists($testPath)) { throw 'Das gespeicherte Testmakro wurde nicht gelöscht.' }
    $window = Find-AppWindow
    $currentMacroCount = Get-MacroCount $window
    if ($currentMacroCount -ne $baselineMacroCount) { throw 'Die Makrobibliothek zeigte nach dem Test nicht wieder die ursprüngliche Anzahl.' }

    Write-Host 'OK: Datenschutz-Hinweis, Aufnahme, Pause/Fortsetzen, Verwerfen, lokales JSON öffnen, Countdown, Wiedergabestopp und Makro löschen.'
}
finally {
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
            Click-ButtonLike $window '↻'
        }
    }
}
