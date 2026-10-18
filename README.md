# MakroWerkstatt

**Makro-Recorder für Windows** zur ausdrücklichen Aufnahme, Prüfung, Speicherung und Wiedergabe von Maus- und Tastatureingaben.

[![CI](https://github.com/<GITHUB_USERNAME>/MakroWerkstatt/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/<GITHUB_USERNAME>/MakroWerkstatt/actions/workflows/ci.yml)
[![Projektversion 1.1.0](https://img.shields.io/badge/Projektversion-1.1.0-blue)](CHANGELOG.md)
[![Lizenz: MIT](https://img.shields.io/badge/Lizenz-MIT-green.svg)](LICENSE)
[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4)](#systemvoraussetzungen)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](#aus-dem-quellcode-bauen)

## English summary

MakroWerkstatt is a macro recorder for Windows.
It records mouse and keyboard input only after the user starts a recording.
Macros are stored locally and protected with Windows DPAPI by default.
Encrypted exports can be moved between Windows accounts with a passphrase.
The app is built with C#/.NET 8 and WPF.
See [SECURITY.md](SECURITY.md) for security reporting guidance.

## Download

**[Zur neuesten Release-Seite und zum ZIP-Download](https://github.com/<GITHUB_USERNAME>/MakroWerkstatt/releases/latest)**

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
- Lokales Speichern und Wiedergabe gespeicherter Makros
- Wiedergabe mit einstellbarem Timing, Wiederholungen und optionaler zufälliger Zusatzverzögerung
- Globale, konfigurierbare Aufnahme- und Wiedergabekürzel
- Aktionseditor mit Mehrfachauswahl, Drag & Drop, Undo und Redo
- Verschlüsselte lokale Speicherung sowie passphrasegeschützter Export und Import

## Screenshots

Noch keine Screenshots veröffentlicht. Hinweise zu den gewünschten Bildern und ihrem Format stehen in [docs/screenshots/README.md](docs/screenshots/README.md).

<!-- Sobald die Dateien vorhanden und geprüft sind, können die Bildverweise aktiviert werden.
![Hauptfenster](docs/screenshots/hauptfenster.png)
![Aktions-Editor](docs/screenshots/aktions-editor.png)
![Einstellungen](docs/screenshots/einstellungen.png)
-->

## Schnellstart

1. Lade das ZIP von der [Release-Seite](https://github.com/<GITHUB_USERNAME>/MakroWerkstatt/releases/latest) herunter und prüfe die Prüfsumme.
2. Entpacke das ZIP und starte `MakroWerkstatt.exe`.
3. Starte eine Aufnahme über die Schaltfläche **Neue Aufnahme** oder das globale Kürzel **F9** und bestätige den Hinweis zu vertraulichen Eingaben.
4. Stoppe die Aufnahme, prüfe die Aktionen und speichere das Makro.
5. Wähle das Makro aus und starte die Wiedergabe über die Schaltfläche oder **F10**. Ein sichtbarer Countdown läuft vor dem Start.

## Datenschutz und Sicherheit

Makros und Einstellungen bleiben lokal im Windows-Benutzerprofil. Die App baut keine Netzwerkverbindung auf und sendet keine Makros, Eingaben oder Nutzungsdaten. Für die lokale Speicherung werden Makros standardmäßig mit Windows DPAPI für das aktuelle Benutzerkonto verschlüsselt. Einstellungen wie Kürzel und Wiedergabe-Timing sind davon ausgenommen.

Der verschlüsselte Export verwendet eine Passphrase, die nicht gespeichert wird und nicht wiederhergestellt werden kann. Eine vergessene Passphrase macht den Export unlesbar. Makros können Passwörter oder andere vertrauliche Eingaben enthalten, auch wenn sie verschlüsselt gespeichert werden. Behandle Makros und Exportdateien entsprechend vertraulich.

Weitere Hinweise stehen in [SECURITY.md](SECURITY.md) und [docs/FORMAT-UND-EXPORT.md](docs/FORMAT-UND-EXPORT.md).

## Bekannte Einschränkungen

- Anwendungen mit Anti-Cheat-Systemen oder ausschließlichem Raw-Input können synthetische Eingaben blockieren oder relative Mausbewegungen ignorieren.
- Relative Mausbewegung für die Wiedergabe setzt passende relative Eingabedaten voraus. Bei geänderter Monitoranordnung, Auflösung oder Skalierung können gespeicherte Bildschirmpositionen abweichen.
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
