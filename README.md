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

## Release erstellen

1. Hebe `<Version>` in `MakroWerkstatt.csproj` im Format `x.y.z` an und pflege die belegten Änderungen in `CHANGELOG.md`.
2. Committe die Änderung und setze einen passenden Tag, zum Beispiel `git tag v1.1.0` und `git push origin v1.1.0`. Der Tag muss exakt zur Projektversion passen.
3. Der Tag startet `.github/workflows/release.yml`. Der Workflow baut, führt die xUnit- und Smoke-Tests aus und lädt das versionierte ZIP sowie `SHA256SUMS.txt` als Artefakt hoch. Ein GitHub Release wird nicht automatisch veröffentlicht.
4. Vergleiche den SHA-256-Wert aus `SHA256SUMS.txt` mit `Get-FileHash`:

   ```powershell
   Get-Content .\SHA256SUMS.txt
   Get-FileHash .\MakroWerkstatt-1.1.0-win-x64.zip -Algorithm SHA256
   Get-FileHash .\MakroWerkstatt.exe -Algorithm SHA256
   ```

Die EXE ist nicht signiert. Windows SmartScreen kann deshalb beim ersten Start warnen. `tools\publish-windows.ps1 -NoShortcut` erstellt das Releasepaket, ohne die Desktop-Verknüpfung anzufassen; der normale Aufruf aktualisiert diese weiterhin.

## Aufnahme und Bedienung

1. **Neue Aufnahme** ausdrücklich anklicken oder das globale Kürzel **F9** drücken; beim Start muss der Hinweis zu vertraulichen Eingaben bestätigt werden.
2. Maus- und Tastaturaktionen in anderen Fenstern ausführen. Mausbewegungen werden auch erfasst, während du eine Maustaste oder Tastaturtaste gedrückt hältst. Die App nimmt sowohl Bildschirmpositionen als auch relative Maus-Deltas über Windows Raw Input auf. So bleiben normale Zeigerwege, Kamerabewegungen in Spielen und kombinierte Abläufe besser erhalten. Der rote, dauerhaft sichtbare Status zeigt eine aktive Aufnahme an. Eingaben in MakroWerkstatt selbst werden nicht aufgezeichnet.
3. **Pause** hält die Uhr an; pausierte Zeit wird nicht in die Zeitstempel übernommen. **Fortsetzen** setzt die Aufnahme fort.
4. **Aufnahme stoppen** behält die aktuelle Sequenz zur Prüfung. **Aufnahme löschen** stoppt und verwirft die ungespeicherte Sequenz sofort.
5. Über **Aktionen bearbeiten** lassen sich Aktionen nach oben oder unten verschieben, duplizieren, löschen oder in ihrer Zeit und ihren vorhandenen Werten ändern. Mehrfachauswahl ist möglich; **Entf** löscht, **Strg+Z** macht rückgängig und **Strg+Y** wiederholt. Die Tasten- und Maustastenpaare werden bei den passenden Bearbeitungen gemeinsam behandelt. Name eintragen und **Aufnahme speichern** wählen; Änderungen werden nicht automatisch auf Platte geschrieben.
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

Die App baut keine Netzwerkverbindung auf und sendet keine Makros, Eingaben oder Nutzungsdaten. Makrodateien werden in einer temporären Datei im selben Ordner vorbereitet und nach erfolgreichem Zurücklesen atomar ersetzt. Beim Start oder Laden werden vorhandene Dateien nicht automatisch umgeschrieben.

Makros werden standardmäßig im **MakroWerkstatt-Format 3** gespeichert: Die JSON-Hülle enthält `formatVersion: 3`, `encryption: "dpapi-currentuser"` und eine Base64-Nutzlast. Darin liegt das bisherige Format-2-Dokument, das Windows DPAPI mit dem Scope `CurrentUser` schützt. Version 1 und 2 bleiben lesbar. Die Aktion **Bestehende Makros verschlüsseln** migriert vorhandene Klartextdateien einzeln nach Bestätigung.

Das Format-2-Payload-Dokument enthält eine stabile ID, Name, Erstellungs- und Änderungszeit in UTC sowie die geordnete Aktionsliste. `atMs` ist ein nicht negativer Millisekunden-Zeitstempel relativ zum aktiven Aufnahmebeginn; Pausen sind ausgenommen. Aktionsarten sind `keyDown`, `keyUp`, `mouseMove`, `mouseMoveRelative`, `mouseButtonDown`, `mouseButtonUp` und `mouseWheel`. `mouseMove` enthält Bildschirm-Pixelkoordinaten; `mouseMoveRelative` enthält Maus-Deltas (`x` = ΔX, `y` = ΔY). Tasten werden als virtuelle Windows-Taste plus Scan-Code gespeichert. Unbekannte oder neuere Formatversionen werden nicht abgespielt und beim Öffnen als Problem gemeldet. Beispiele: [Format 2](docs/makro-format-v2.example.json) und [Format-3-Hülle](docs/makro-format-v3.example.json). Das Format-3-Beispiel enthält absichtlich einen erfundenen Platzhalter und lässt sich nicht entschlüsseln.

### Zeitachse und Aktionseditor

Der Editor ändert keine Formatfelder. Beim Verschieben wechseln Aktionen ihre Listenplätze; die Zeitstempel bleiben an den jeweiligen Plätzen. Beim Löschen behalten übrige Aktionen ihre absoluten Zeitstempel. Ein Duplikat wird nach der letzten ausgewählten Aktion eingefügt, startet 1 ms danach und übernimmt die Zeitabstände zwischen den kopierten Aktionen. Nachfolgende Zeitstempel werden nur dann nach hinten verschoben, wenn es sonst zu einer nicht monotonen Reihenfolge käme. **Pause einfügen** erhöht den Abstand vor der nächsten Aktion und verschiebt alle danach liegenden Zeitstempel gemeinsam. Eine Pause hinter der letzten Aktion gibt es nicht, da sie die Wiedergabe nicht beeinflussen würde. Beim Ändern des Abstands einer Aktion werden diese und alle folgenden Zeitstempel gemeinsam verschoben; die Abstände innerhalb des folgenden Abschnitts bleiben erhalten.

Der Editor begrenzt neu eingegebene Abstände und Pausen auf 86.400.000 ms (24 Stunden). Die bestehende Dateivalidierung hat keine Obergrenze für absolute Zeitstempel. Koordinaten, relative Deltas und Mausradwerte sind 32-Bit-Ganzzahlen, wie im Modell; Maustasten stammen aus der zentralen Liste erlaubter Werte. Wird eine Taste oder Maustaste bearbeitet, ändern sich beide Seiten eines gefundenen Runter/Hoch-Paars gemeinsam. Beim Tastenersatz setzt der Editor den Scancode auf 0 und stellt das Extended-Key-Flag aus dem virtuellen Tastencode wieder her. Beim Löschen oder Duplizieren wird ein gefundenes Paar ebenfalls gemeinsam behandelt. Der Editor lässt keine Bearbeitung zu, die bei Wiedergabe eine zusätzliche Taste oder Maustaste am Sequenzende gehalten zurückließe. Unvollständige Paare aus einer vorhandenen Aufnahme werden nicht automatisch ergänzt und lassen sich nicht als einzelner Tasteneingabewert bearbeiten oder duplizieren; beim Stoppen der Wiedergabe gibt der bestehende Freigabepfad gehaltene Eingaben frei.

## Datenschutz und Verschlüsselung

Standardmäßig verschlüsselt die App das gesamte Makrodokument einschließlich Name, Zeitangaben und Aktionen mit Windows DPAPI für das aktuelle Benutzerkonto. Die Einstellung **Makros verschlüsseln** kann neue Speichervorgänge auf das bisherige Klartextformat 2 umstellen. Einstellungen wie Kürzel und Wiedergabe-Timing bleiben unverschlüsselt; sie enthalten keine aufgezeichneten Eingaben.

Verschlüsselte lokale Makros lassen sich nur mit dem Windows-Benutzerkonto öffnen, für das sie geschützt wurden. Eine Sicherung der lokalen Makrodateien allein garantiert daher keine Wiederherstellung: Nach einem Rechnerwechsel oder Verlust des Windows-Profils können diese Dateien unlesbar sein. Für kontoübergreifende Backups verwende den passphrase-verschlüsselten Export. Die App entschlüsselt bestehende Dateien beim Laden nur im Arbeitsspeicher und ersetzt sie nicht automatisch.

Makros können weiterhin Passwörter, Nachrichten und andere vertrauliche Eingaben enthalten. DPAPI schützt gespeicherte Dateien im Ruhezustand, aber nicht vor Malware, die im selben Windows-Benutzerkonto ausgeführt wird. Behandle gespeicherte oder bewusst unverschlüsselte Makros weiterhin vertraulich.

## Export und Import

Mit **Alle exportieren** werden alle lokal lesbaren Makros in eine `.mwexport`-Datei geschrieben. **Auswahl exportieren** enthält nur das ausgewählte, lokal gespeicherte Makro. Der Export ist immer verschlüsselt; einen Klartext-Export gibt es nicht. Nicht lesbare Makrodateien werden beim Export übersprungen und in der Ergebnisübersicht mitgezählt.

Die Datei ist ein JSON-Umschlag mit dem separaten `exportFormat: 1`. Der Umschlag nennt `kdf: "pbkdf2-sha256"` mit standardmäßig 600.000 Iterationen sowie zufälliges Salt mit 16 Byte und Nonce mit 12 Byte. `cipher: "aes-256-gcm"` verwendet einen 256-Bit-Schlüssel und einen 16-Byte-Authentifizierungstag. Die Headerwerte werden authentifiziert; der Klartext mit den vollständigen Format-2-Makrodokumenten liegt nur innerhalb der Verschlüsselung. Beim Import werden 600.000 bis 5.000.000 Iterationen, höchstens 50 MB und höchstens 10.000 Makros akzeptiert. Diese Formatparameter sind keine Geheimnisse und ersetzen keine starke Passphrase.

Beim Export muss die Passphrase mindestens 12 Zeichen lang sein und zweimal eingegeben werden. **Die Passphrase wird nicht gespeichert und kann nicht wiederhergestellt werden. Passphrase vergessen = Export unlesbar.** Ein Export ist so sicher wie seine Passphrase. Verwende eine starke, einzigartige Passphrase und bewahre sie getrennt von der Exportdatei auf. Bewahre die Exportdatei außerdem an einem sicheren Ort getrennt von diesem Rechner auf.

Beim Import wird die Datei zunächst geprüft und entschlüsselt; falsche Passphrase und beschädigte Datei werden bewusst mit derselben neutralen Meldung behandelt. Ungültige Makros werden einzeln gezählt und die übrigen können weiter importiert werden. Namenskollisionen werden standardmäßig übersprungen. Eine Kopie erhält einen eindeutigen Namenszusatz; Überschreiben verlangt eine zusätzliche ausdrückliche Bestätigung. Importierte Makros werden gemäß **Makros verschlüsseln** gespeichert: bei aktivierter Einstellung im lokalen Format 3 mit DPAPI, sonst im lokalen Format 2.

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
- `MacroModels.cs`, `MacroStorage.cs`, `MacroExportService.cs` – Aktionsmodell, lokales Format und passphrase-verschlüsseltes Exportformat
- `IFileDialogService.cs`, `MacroExportFileWriter.cs` – Datei-Auswahlabstraktion und atomarer Exportdateischreiber
- `GlobalInputHook.cs`, `MacroPlayback.cs` – Windows-Hooks und Eingabewiedergabe
- `Assets\makrowerkstatt.svg` – editierbare Symbolquelle
- `Assets\makrowerkstatt.ico` – mehrstufiges Windows-Symbol
- `docs\makro-format-v1.example.json`, `docs\makro-format-v2.example.json` und `docs\makro-format-v3.example.json` – Formatbeispiele
- `tools\generate-icon.ps1` – Symbol neu erzeugen
- `tools\publish-windows.ps1` – EXE veröffentlichen und Desktop-Verknüpfung einrichten
- `tools\SmokeTests\` – Offline-Smoke-Test für JSON-Format und Timinglogik
- `tools\verify-ui.ps1` – UI-Abläufe mit Windows UI Automation in einer isolierten App-Instanz und einem temporären Datenordner prüfen

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

Der UI-Smoke-Test startet eine eigene App-Instanz mit einem eindeutigen temporären Datenordner und greift nicht auf `%LOCALAPPDATA%\MakroWerkstatt` zu. Nach einem Release-Build kannst du ihn so starten:

```powershell
pwsh -ExecutionPolicy Bypass -File .\tools\verify-ui.ps1
```

## Entfernen

Lösche die Desktop-Verknüpfung und bei Bedarf den Projektordner `C:\Macro-App`. Makros und Einstellungen werden dadurch nicht gelöscht. Wenn du sie ebenfalls entfernen möchtest, lösche separat `%LOCALAPPDATA%\MakroWerkstatt`.
