# MakroWerkstatt

Eine lokale Windows-Desktop-App zum ausdrücklich gestarteten Aufzeichnen, Prüfen, Speichern und Wiedergeben von Maus- und Tastaturabläufen.

## Starten

Die Desktop-Verknüpfung **MakroWerkstatt** startet die portable Windows-Veröffentlichung. Für den Start wird keine Internetverbindung und keine separate .NET-Installation benötigt.

Zum Neuveröffentlichen aus dem Quellcode:

```powershell
pwsh -ExecutionPolicy Bypass -File .\tools\publish-windows.ps1
```

Das Skript erstellt die selbstständige x64-Veröffentlichung und aktualisiert die Desktop-Verknüpfung. Falls die bisherige EXE gerade läuft, veröffentlicht es die neue Version in einen eigenen `dist-update-*`-Ordner, ohne die laufende Datei zu überschreiben. Für die erstmalige Veröffentlichung können fehlende .NET-Runtimepakete einmalig aus NuGet geladen werden; der fertige App-Start benötigt danach kein Internet. Zum Entwickeln oder Starten aus dem Quellcode genügt:

```powershell
dotnet run --project .\MakroWerkstatt.csproj
```

Dafür wird das .NET 8 SDK mit Windows-Desktop-Unterstützung benötigt. Der gewählte Stack ist **C#/.NET 8 und WPF**: native Windows-Oberfläche, globale Windows-Eingabehooks und keine Drittanbieterpakete.

## Aufnahme und Bedienung

1. **Neue Aufnahme** ausdrücklich anklicken oder das globale Kürzel **F9** drücken; beim Start muss der Hinweis zu vertraulichen Eingaben bestätigt werden.
2. Maus- und Tastaturaktionen in anderen Fenstern ausführen. Mausbewegungen werden auch erfasst, während du eine Maustaste oder Tastaturtaste gedrückt hältst. Die App nimmt sowohl Bildschirmpositionen als auch relative Maus-Deltas über Windows Raw Input auf. So bleiben normale Zeigerwege, Kamerabewegungen in Spielen und kombinierte Abläufe besser erhalten. Der rote, dauerhaft sichtbare Status zeigt eine aktive Aufnahme an. Eingaben in MakroWerkstatt selbst werden nicht aufgezeichnet.
3. **Pause** hält die Uhr an; pausierte Zeit wird nicht in die Zeitstempel übernommen. **Fortsetzen** setzt die Aufnahme fort.
4. **Aufnahme stoppen** behält die aktuelle Sequenz zur Prüfung. **Aufnahme löschen** stoppt und verwirft die ungespeicherte Sequenz sofort.
5. Über die Aktionsliste lässt sich jede Aktion prüfen und eine ausgewählte Aktion entfernen. Name eintragen und **Aufnahme speichern** wählen. Ein gespeichertes Makro lässt sich links erneut öffnen, umbenennen oder löschen.
6. In **Wiedergabe-Timing** unter **Durchläufe pro Wiedergabe** die gewünschte Anzahl von 1 bis 100 einstellen. Standardmäßig ist 1 ausgewählt. **Wiedergabe starten** oder das globale Kürzel **F10** startet nach einem sichtbaren Drei-Sekunden-Countdown. Während der Wiedergabe beendet **F10**, **Esc** oder die Stopp-Schaltfläche die gesamte Wiedergabe. Beim Stoppen gibt die App noch gehaltene Tasten und Maustasten bestmöglich frei.

Die Anzahl wird zusammen mit den übrigen Einstellungen lokal gespeichert. Die geschätzte Laufzeit berücksichtigt alle Durchläufe. Auch der Randomizer kann an der Grenze zwischen zwei Durchläufen eine Zusatzwartezeit einfügen.

Für Spiele kann **Relative Mausbewegung für Spiele** aktiviert werden. Dann spielt die App relative Deltas ab und setzt den Windows-Zeiger nicht an Bildschirmpositionen. Alte Makros ohne relative Deltas können im Spielmodus ihre aufgezeichneten Zeigerpositionen als Bewegungsweg verwenden. Die Einstellung wird lokal gespeichert. Windows-`SendInput` erzeugt normale synthetische Mausbewegungen; ein Spiel, das ausschließlich physische Raw-Input-Daten verarbeitet, kann sie ignorieren. Die App installiert keinen Treiber und verändert das Spiel nicht.

## Streckmodus und Randomizer

Der Streckmodus verteilt die gespeicherten Zeitstempel je Durchlauf über die gewählte Zieldauer. Die Reihenfolge der Aktionen bleibt erhalten. Enthält ein Ablauf mehrere Aktionen ohne messbare Zeitabstände, verteilt die App sie gleichmäßig. Eine einzelne sofortige Aktion kann keine Zeitspanne ausfüllen und wird unmittelbar ausgeführt; die Laufzeitanzeige weist darauf hin. Ohne Streckmodus verwendet die App die aufgezeichneten Abstände.

Der ausgeschaltete Randomizer verändert nichts. Ist er aktiv, fügt er für jeden Übergang zwischen zwei Aktionen eine zufällige Zusatzwartezeit aus der eingestellten Spanne von 1 bis 1000 ms ein. Er ändert weder Eingaben noch Klickpositionen. Diese Zusatzzeiten kommen zur Zielzeit hinzu; die Oberfläche zeigt Mittelwert und mögliche Spanne an. Laufzeitangaben sind Schätzungen, da Windows Eingaben und Timer nicht in Echtzeit garantiert.

## Speicherort und Dateiformat

Alle Makros und Einstellungen bleiben im Benutzerprofil auf diesem PC:

- Makros: `%LOCALAPPDATA%\MakroWerkstatt\Makros\<ID>.json`
- Einstellungen einschließlich der globalen Tastenkürzel: `%LOCALAPPDATA%\MakroWerkstatt\settings.json`

Die App baut keine Netzwerkverbindung auf und sendet keine Makros, Eingaben oder Nutzungsdaten. JSON-Dateien werden über temporäre Dateien lokal gespeichert und anschließend ersetzt. Die Anwendungsdaten lassen sich mit dem Windows-Explorer sichern oder entfernen.

Makrodateien werden im Format **MakroWerkstatt JSON, `formatVersion: 2`** gespeichert. Bestehende Makros in Formatversion 1 bleiben lesbar und werden beim nächsten Speichern auf Version 2 aktualisiert. Ein Dokument enthält eine stabile ID, Name, Erstellungs- und Änderungszeit in UTC sowie die geordnete Aktionsliste. `atMs` ist ein nicht negativer Millisekunden-Zeitstempel relativ zum aktiven Aufnahmebeginn; Pausen sind ausgenommen. Aktionsarten sind `keyDown`, `keyUp`, `mouseMove`, `mouseMoveRelative`, `mouseButtonDown`, `mouseButtonUp` und `mouseWheel`. `mouseMove` enthält Bildschirm-Pixelkoordinaten; `mouseMoveRelative` enthält Maus-Deltas (`x` = ΔX, `y` = ΔY). Tasten werden als virtuelle Windows-Taste plus Scan-Code gespeichert. Unbekannte oder neuere Formatversionen werden nicht abgespielt und beim Öffnen als Problem gemeldet. Ein vollständiges Beispiel liegt in [`docs\makro-format-v2.example.json`](docs/makro-format-v2.example.json). Die Struktur ist offen dokumentiert und kann von Hand gesichert oder geprüft werden.

## Tastenkürzel

- **F9 (Standard):** Aufnahme global starten oder stoppen. Beim Start erscheint weiterhin die ausdrückliche Datenschutzabfrage. Während einer pausierten Aufnahme beendet das Kürzel die Aufnahme.
- **F10 (Standard):** Wiedergabe des aktuellen Makros global starten oder stoppen. Beim Start bleibt der sichtbare Drei-Sekunden-Countdown bestehen; ein erneuter Druck stoppt auch den Countdown.
- Aufnahme- und Wiedergabekürzel lassen sich in der Oberfläche unabhängig festlegen. Klicke auf das jeweilige Kürzel und drücke F1 bis F12, optional zusammen mit Ctrl, Alt oder Shift. Beide Kürzel müssen verschieden sein.
- Die belegte F-Taste wird abgefangen und nicht an das aktive Zielfenster weitergegeben. Bei einem Kürzel mit Modifiern wird die Tastenkombination aus der Makroaufnahme herausgefiltert.
- **Esc während Countdown oder Wiedergabe:** globaler Not-Aus. Esc wird in diesem Moment nicht an die Zielanwendung weitergegeben.

## Betriebssystemgrenzen und Hinweise

- Benötigt Windows 10 oder 11 (64 Bit) in einer angemeldeten interaktiven Sitzung. Aufnahme und Wiedergabe sind keine Hintergrunddienste.
- Windows blockiert oder beschränkt Eingaben auf dem sicheren Desktop, an UAC-Abfragen, am Anmeldebildschirm und teils in geschützten Anwendungen. Auch Anwendungen mit höheren Rechten oder Spiele mit Anti-Cheat-/Raw-Input-System können Aufnahme oder Wiedergabe einschränken. Die App umgeht diese Grenzen nicht. Eine Erhöhung der Rechte wird nicht automatisch angefordert.
- Mausaktionen verwenden Bildschirm-Pixelkoordinaten. Bei geänderter Monitoranordnung, Auflösung oder Skalierung kann ein gespeichertes Ziel an einer anderen Stelle liegen. Prüfe ein Makro vor der Wiedergabe in der Aktionsliste und starte es nur, wenn das Ziel sicher ist.
- Absolute Mauspositionen werden zur Begrenzung der Dateigröße höchstens ungefähr alle 12 ms protokolliert; relative Eingabedaten werden in kleinen Zeitfenstern zusammengefasst. Sehr feine oder extrem schnelle Bewegungen werden daher vereinfacht.
- Bei Minecraft Java kann das Verhalten versionsabhängig sein. Minecraft Java 26.3 verwendet laut offiziellen Versionshinweisen beim Spielen immer den relativen Mausmodus; Windows-Makros können dabei Klicks senden, während die Kamera synthetische Bewegungen ignoriert. Die relative Aufnahme erfasst physische Bewegungen; sie macht Windows-Eingaben nicht zu einem physischen Maussignal.
- Esc stoppt zwischen Aktionen beziehungsweise Wartezeiten. Ein einzelner bereits laufender Windows-Aufruf lässt sich nicht rückgängig machen. Haltene Tasten werden beim Abbruch freigegeben, sofern Windows die Freigabe annimmt.
- Tastatureingaben können Passwörter, Nachrichten und andere vertrauliche Texte enthalten. Makros werden lokal gespeichert; schütze dein Windows-Benutzerkonto entsprechend und lösche nicht mehr benötigte Aufnahmen.

## Projektdateien

- `MakroWerkstatt.csproj` – WPF-App
- `MacroModels.cs`, `MacroStorage.cs` – Aktionsmodell und versioniertes lokales JSON
- `GlobalInputHook.cs`, `MacroPlayback.cs` – Windows-Hooks und Eingabewiedergabe
- `Assets\makrowerkstatt.svg` – editierbare Symbolquelle
- `Assets\makrowerkstatt.ico` – mehrstufiges Windows-Symbol
- `docs\makro-format-v1.example.json` und `docs\makro-format-v2.example.json` – Beispiele für das alte und aktuelle Format
- `tools\generate-icon.ps1` – Symbol neu erzeugen
- `tools\publish-windows.ps1` – EXE veröffentlichen und Desktop-Verknüpfung einrichten
- `tools\SmokeTests\` – Offline-Smoke-Test für JSON-Format und Timinglogik
- `tools\verify-ui.ps1` – UI-Abläufe mit Windows UI Automation prüfen; erfordert eine geöffnete App und entfernt sein temporäres Testmakro

## Tests ausführen

Die automatisierten Logiktests und den schnellen Konsolen-Smoke-Test kannst du lokal so starten:

```powershell
dotnet test .\tests\MakroWerkstatt.Tests\MakroWerkstatt.Tests.csproj -c Release
dotnet run --project .\tools\SmokeTests\SmokeTests.csproj -c Release
```

## Prüfungen erneut ausführen

Der Logik-Smoke-Test benötigt nur das .NET 8 SDK:

```powershell
dotnet run --project .\tools\SmokeTests\SmokeTests.csproj -c Release
```

Für den UI-Smoke-Test die App öffnen und anschließend in einem zweiten PowerShell-Fenster ausführen:

```powershell
pwsh -ExecutionPolicy Bypass -File .\tools\verify-ui.ps1
```

## Entfernen

Lösche die Desktop-Verknüpfung und bei Bedarf den Projektordner `C:\Macro-App`. Makros und Einstellungen werden dadurch nicht gelöscht. Wenn du sie ebenfalls entfernen möchtest, lösche separat `%LOCALAPPDATA%\MakroWerkstatt`.
