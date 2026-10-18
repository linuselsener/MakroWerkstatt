# MakroWerkstatt

**Makro-Recorder für Windows** zur ausdrücklichen Aufnahme, Prüfung, Speicherung und Wiedergabe von Maus- und Tastatureingaben.

[![CI](https://github.com/linuselsener/MakroWerkstatt/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/linuselsener/MakroWerkstatt/actions/workflows/ci.yml)
[![Neueste Version](https://img.shields.io/github/v/release/linuselsener/MakroWerkstatt?label=Neueste%20Version)](https://github.com/linuselsener/MakroWerkstatt/releases/latest)
[![Lizenz: MIT](https://img.shields.io/badge/Lizenz-MIT-green.svg)](LICENSE)
[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4)](#systemvoraussetzungen)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](#aus-dem-quellcode-bauen)

## English summary

MakroWerkstatt is a macro recorder for Windows.
It records mouse and keyboard input only after the user starts a recording.
The default data directory is obtained through Windows' LocalApplicationData API; `MAKROWERKSTATT_DATA_DIR` overrides it.
The app does not read the `LOCALAPPDATA` environment variable directly and has no built-in network client, updater, or telemetry client.
Encrypted exports can be moved between Windows accounts with a passphrase.
The app is built with C#/.NET 8 and WPF.
See [SECURITY.md](SECURITY.md) for security reporting guidance.

## Download

**[Zur neuesten Release-Seite und zum ZIP-Download](https://github.com/linuselsener/MakroWerkstatt/releases/latest)**

ZIP herunterladen, entpacken und `MakroWerkstatt.exe` starten. Die veröffentlichte x64-Datei ist selbstständig; eine separate .NET-Runtime ist zum Start nicht erforderlich.

### Systemvoraussetzungen

- Windows 10 oder Windows 11 (64 Bit)
- Angemeldete interaktive Windows-Sitzung

### SmartScreen und Prüfsumme

Die EXE ist nicht digital signiert. Windows SmartScreen kann deshalb beim ersten Start eine Warnung anzeigen. Prüfe, ob die ZIP von der oben verlinkten Release-Seite stammt, und vergleiche ihre Prüfsumme mit `SHA256SUMS.txt`:

```powershell
Get-FileHash .\MakroWerkstatt-1.1.0-win-x64.zip -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

Vergleiche den SHA-256-Wert der ZIP-Datei mit der Zeile für genau diesen ZIP-Dateinamen. Die zweite Prüfsumme in der Datei gehört zur EXE.

## Funktionen

- Maus- und Tastaturaufnahme nach ausdrücklichem Start, mit Pause und Fortsetzen
- Speicherung standardmäßig unter dem Windows-Benutzerprofil und Wiedergabe gespeicherter Makros; der Datenpfad ist konfigurierbar
- Wiedergabe mit einstellbarem Timing, Wiederholungen und optionaler zufälliger Zusatzverzögerung
- Globale, konfigurierbare Aufnahme- und Wiedergabekürzel
- Aktionseditor mit Mehrfachauswahl, Drag & Drop, Undo und Redo
- Standardmäßig DPAPI-verschlüsselte Makrospeicherung sowie passphrasegeschützter Export und Import

## Screenshots

Noch keine Screenshots veröffentlicht. Hinweise zu den gewünschten Bildern und ihrem Format stehen in [docs/screenshots/README.md](docs/screenshots/README.md).

<!-- Sobald die Dateien vorhanden und geprüft sind, können die Bildverweise aktiviert werden.
![Hauptfenster](docs/screenshots/hauptfenster.png)
![Aktions-Editor](docs/screenshots/aktions-editor.png)
![Einstellungen](docs/screenshots/einstellungen.png)
-->

## Schnellstart

1. Lade das ZIP von der [Release-Seite](https://github.com/linuselsener/MakroWerkstatt/releases/latest) herunter und prüfe die Prüfsumme.
2. Entpacke das ZIP und starte `MakroWerkstatt.exe`.
3. Starte eine Aufnahme über die Schaltfläche **Neue Aufnahme** oder das globale Kürzel **F9** und bestätige den Hinweis zu vertraulichen Eingaben.
4. Stoppe die Aufnahme, prüfe die Aktionen und speichere das Makro.
5. Wähle das Makro aus und starte die Wiedergabe über die Schaltfläche oder **F10**. Ein sichtbarer Countdown läuft vor dem Start.

## Datenschutz und Sicherheit

### Was die App mit Eingaben macht

Beim Laden des Hauptfensters installiert MakroWerkstatt globale Windows-Hooks für Tastatur und Maus. Sie verarbeiten Eingabeereignisse auch außerhalb einer Aufnahme, unter anderem um die konfigurierten Tastenkürzel zu erkennen. Vor dem Start einer Aufnahme fragt die App ausdrücklich nach einer Datenschutzbestätigung. Nur während einer aktiven Aufnahme werden Ereignisse in die Makro-Aktionsliste übernommen. Außerhalb einer Aufnahme werden sie nicht als Makro gespeichert. Hook-Ereignisse werden vor dieser Prüfung kurzzeitig im Prozess und gegebenenfalls in der UI-Dispatcher-Warteschlange verarbeitet. Die geprüften Logger-Aufrufstellen übergeben weder Eingabeinhalte noch Makro-Aktionen an den Logger.

Aufgezeichnete Makros können alles enthalten, was während der Aufnahme getippt wird, einschließlich Passwörtern. Entschlüsselte Makros und der Aktionseditor-Verlauf liegen während der Verwendung im Arbeitsspeicher. Der Verlauf kann bis zu 100 frühere Zustände plus den aktuellen Zustand enthalten. Einzelne Byte-Puffer und Editor-Aktionen werden an bestimmten Stellen überschrieben; der verwaltete Speicher und alle Kopien werden dadurch nicht garantiert vollständig gelöscht.

### Wo Daten liegen und wie sie geschützt sind

Der Standard-Datenpfad wird über die Windows-API `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` ermittelt; darunter verwendet die App den Ordner `MakroWerkstatt`. Die Umgebungsvariable `LOCALAPPDATA` wird nicht direkt ausgewertet. Mit `MAKROWERKSTATT_DATA_DIR` lässt sich der Stammordner überschreiben: Makros und Einstellungen liegen darunter, Logs im Unterordner `logs`. Die App prüft nicht, ob der gewählte Pfad auf einem lokalen Laufwerk liegt. Verwende nach Möglichkeit ein lokales, nicht synchronisiertes Laufwerk. Bei einem Netzlaufwerk oder einem synchronisierten Cloud-Ordner können Dateien den Rechner verlassen. Einstellungen und Logs sind nicht durch DPAPI verschlüsselt.

Makros werden standardmäßig mit Windows DPAPI für das aktuelle Benutzerkonto verschlüsselt. Die Einstellung **Makros verschlüsseln** kann neue Speichervorgänge stattdessen im Klartext ablegen; ältere Makrodateien der Versionen 1 und 2 bleiben bis zu ihrer ausdrücklich gestarteten Migration im Klartext lesbar. DPAPI schützt gespeicherte Dateien, aber nicht vor Schadsoftware im selben Windows-Benutzerkonto. Nach einem Wechsel des Benutzerprofils oder Geräts ist die Entschlüsselung nicht zugesichert. Teile verschlüsselter Makrodateien oder Exporte nicht unbedacht.

Der Export ist mit einer Passphrase geschützt: PBKDF2-SHA256 mit standardmäßig 600.000 Iterationen und AES-256-GCM. Die Passphrase muss mindestens 12 Zeichen lang sein, wird nicht gespeichert und kann nicht wiederhergestellt werden. Bei Verlust der Passphrase ist der Export nicht mehr lesbar.

Logs liegen standardmäßig im Ordner `logs` unter dem Datenpfad. Sie enthalten Diagnoseangaben wie Zeitstempel, Schweregrad, Quelle, Meldung und bei Fehlern Typ, Fehlercodes und Stacktrace. Die geprüften Logger-Aufrufstellen übergeben keine Makro-Aktionswerte oder Tasteninhalte. Beim Start versucht die App, Logs zu löschen, die älter als 14 Tage sind; Fehler bei dieser Bereinigung werden abgefangen, daher ist die Löschung nicht garantiert.

In der geprüften Anwendungscodebasis wurden keine Netzwerk-Clients, Updateprüfungen oder Telemetrie-Clients gefunden. Das ist keine Sperre gegen Netzwerkzugriffe des Betriebssystems: Ein konfigurierter Datenpfad auf einem Netzlaufwerk oder in einem synchronisierten Ordner kann Netzwerkverkehr verursachen.

Die EXE ist nicht digital signiert; SmartScreen kann deshalb beim ersten Start warnen. Vergleiche den SHA-256-Wert der ZIP-Datei mit dem passenden Eintrag in `SHA256SUMS.txt`, wie im Abschnitt [SmartScreen und Prüfsumme](#smartscreen-und-prüfsumme) beschrieben. Eine passende Prüfsumme bestätigt nur den Vergleich mit der bereitgestellten Prüfsummendatei und ersetzt keine digitale Signatur. Nach der öffentlichen Freigabe des Repositorys kann der Quellcode auf GitHub eingesehen werden.

Weitere Hinweise stehen in [SECURITY.md](SECURITY.md) und [docs/FORMAT-UND-EXPORT.md](docs/FORMAT-UND-EXPORT.md).

## Bekannte Einschränkungen

- Die Wiedergabe sendet synthetische Eingaben über Windows `SendInput`. Programme mit Anti-Cheat- oder Raw-Input-Beschränkungen können sie blockieren; eine Kompatibilität wird nicht garantiert.
- Die relative Mausaufnahme benötigt eine erfolgreiche Raw-Input-Registrierung. Bei relativer Wiedergabe werden vorhandene relative Mausaktionen verwendet; wenn keine vorhanden sind, bildet die App Deltas aus aufeinanderfolgenden absoluten Positionen. Monitoranordnung, Auflösung und Skalierung können das Ergebnis beeinflussen.
- Windows kann Eingaben auf dem sicheren Desktop, an UAC-Abfragen, am Anmeldebildschirm und in geschützten Anwendungen blockieren. Die App umgeht diese Grenzen nicht.
- Prüfe vor dem Einsatz selbst die Nutzungsbedingungen der jeweiligen Spiele und Programme.

## Aus dem Quellcode bauen

Voraussetzung ist das .NET 8 SDK mit Windows-Desktop-Unterstützung auf Windows. Im Repository-Root:

```powershell
dotnet restore .\MakroWerkstatt.sln
dotnet build .\MakroWerkstatt.sln -c Release -warnaserror
dotnet test
dotnet run --project .\tools\SmokeTests\SmokeTests.csproj -c Release
pwsh -ExecutionPolicy Bypass -File .\tools\publish-windows.ps1 -NoShortcut
```

Das Publish-Skript erzeugt die selbstständige x64-Veröffentlichung, ein versioniertes ZIP und `SHA256SUMS.txt`. `-NoShortcut` überspringt das Anlegen oder Aktualisieren der Desktop-Verknüpfung. Der vollständige Ablauf zum Erstellen eines Release-Entwurfs steht in [docs/RELEASE.md](docs/RELEASE.md).

### Projektstruktur

- `MakroWerkstatt.csproj`, `MakroWerkstatt.sln` – WPF-App und Solution
- `MacroModels.cs`, `MacroStorage.cs`, `MacroExportService.cs` – Makromodell, lokale Speicherung, Export und Import
- `MainViewModel.cs`, `MainWindow.xaml` – Bedienlogik und Hauptfenster
- `tests/MakroWerkstatt.Tests/` – xUnit-Tests
- `tools/SmokeTests/` – Konsolen-Smoke-Test für Format- und Timinglogik
- `tools/publish-windows.ps1` – selbstständige Windows-x64-Veröffentlichung
- `docs/` – Formatbeispiele, Exportdetails und Releaseablauf

## Mitwirkung, Lizenz und Änderungen

Fehlerberichte und Vorschläge sind willkommen. Bitte beachte [CONTRIBUTING.md](CONTRIBUTING.md): Issues sind willkommen; Pull Requests werden nicht angenommen. Die [MIT-Lizenz](LICENSE) erlaubt Forks. Änderungen stehen im [CHANGELOG.md](CHANGELOG.md).
