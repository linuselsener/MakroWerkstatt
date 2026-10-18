# Speicherformat, Export und Import

## Lokale Ablage und Verschlüsselung

Der Standard-Datenpfad wird über die Windows-API `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` ermittelt; darunter liegen Makros in `MakroWerkstatt\Makros\<ID>.json` und Einstellungen einschließlich Kürzel und Wiedergabe-Timing in `MakroWerkstatt\settings.json`. `MAKROWERKSTATT_DATA_DIR` kann den Stammordner überschreiben; Logs liegen im Unterordner `logs`. Die Umgebungsvariable `LOCALAPPDATA` wird nicht direkt ausgewertet. Die App baut keine Netzwerkverbindung auf und sendet keine Makros, Eingaben oder Nutzungsdaten.

Makrodateien werden zunächst in einer temporären Datei im selben Ordner geschrieben und nach erfolgreichem Zurücklesen atomar ersetzt. Beim Start oder Laden werden vorhandene Dateien nicht automatisch umgeschrieben.

Standardmäßig werden Makros im MakroWerkstatt-Format 3 gespeichert. Die JSON-Hülle enthält `formatVersion: 3`, `encryption: "dpapi-currentuser"` und eine Base64-Nutzlast. Darin liegt das bisherige Format-2-Dokument, das Windows DPAPI für das aktuelle Benutzerkonto schützt. Version 1 und 2 bleiben lesbar. Die Aktion **Bestehende Makros verschlüsseln** migriert vorhandene Klartextdateien einzeln nach Bestätigung.

Die Einstellungen **Makros verschlüsseln** kann neue Speichervorgänge stattdessen im bisherigen Klartextformat 2 ablegen. Eine lokale DPAPI-Datei lässt sich nur mit dem Windows-Benutzerkonto öffnen, für das sie geschützt wurde. Eine Dateisicherung allein garantiert daher keine Wiederherstellung nach einem Rechnerwechsel oder Verlust des Windows-Profils.

## Makrodokument

Das Format-2-Dokument enthält eine stabile ID, Name, Erstellungs- und Änderungszeit in UTC sowie eine geordnete Aktionsliste. `atMs` ist ein nicht negativer Millisekunden-Zeitstempel relativ zum aktiven Aufnahmebeginn; Pausen sind ausgenommen. Aktionsarten sind `keyDown`, `keyUp`, `mouseMove`, `mouseMoveRelative`, `mouseButtonDown`, `mouseButtonUp` und `mouseWheel`. `mouseMove` enthält Bildschirm-Pixelkoordinaten; `mouseMoveRelative` enthält Maus-Deltas (`x` = ΔX, `y` = ΔY). Tasten werden als virtuelle Windows-Taste plus Scan-Code gespeichert. Unbekannte oder neuere Formatversionen werden nicht abgespielt und beim Öffnen als Problem gemeldet.

Beispiele: [Format 1](makro-format-v1.example.json), [Format 2](makro-format-v2.example.json) und [Format-3-Hülle](makro-format-v3.example.json). Das Format-3-Beispiel enthält absichtlich einen Platzhalter und lässt sich nicht entschlüsseln.

## Zeitachse und Aktionseditor

Der Editor ändert keine Formatfelder. Beim Verschieben wechseln Aktionen ihre Listenplätze; die Zeitstempel bleiben an den jeweiligen Plätzen. Beim Löschen behalten übrige Aktionen ihre absoluten Zeitstempel. Ein Duplikat wird nach der letzten ausgewählten Aktion eingefügt, startet 1 ms danach und übernimmt die Zeitabstände zwischen den kopierten Aktionen. Nachfolgende Zeitstempel werden nur dann nach hinten verschoben, wenn es sonst zu einer nicht monotonen Reihenfolge käme.

**Pause einfügen** erhöht den Abstand vor der nächsten Aktion und verschiebt alle danach liegenden Zeitstempel gemeinsam. Eine Pause hinter der letzten Aktion gibt es nicht, da sie die Wiedergabe nicht beeinflussen würde. Beim Ändern des Abstands einer Aktion werden diese und alle folgenden Zeitstempel gemeinsam verschoben; die Abstände innerhalb des folgenden Abschnitts bleiben erhalten. Neu eingegebene Abstände und Pausen sind auf 86.400.000 ms (24 Stunden) begrenzt. Die bestehende Dateivalidierung begrenzt absolute Zeitstempel nicht auf diesen Wert.

Koordinaten, relative Deltas und Mausradwerte sind 32-Bit-Ganzzahlen; Maustasten stammen aus der zentralen Liste erlaubter Werte. Wird eine Taste oder Maustaste bearbeitet, ändert der Editor beide Seiten eines gefundenen Runter/Hoch-Paars gemeinsam. Beim Tastenersatz setzt der Editor den Scancode auf 0 und stellt das Extended-Key-Flag aus dem virtuellen Tastencode wieder her. Beim Löschen oder Duplizieren wird ein gefundenes Paar ebenfalls gemeinsam behandelt. Der Editor lässt keine Bearbeitung zu, die bei Wiedergabe eine zusätzliche Taste oder Maustaste am Sequenzende gehalten zurückließe. Unvollständige Paare aus einer vorhandenen Aufnahme werden nicht automatisch ergänzt und lassen sich nicht als einzelner Tasteneingabewert bearbeiten oder duplizieren; beim Stoppen der Wiedergabe gibt der Freigabepfad gehaltene Eingaben frei.

## Export

**Alle exportieren** schreibt alle lokal lesbaren Makros in eine `.mwexport`-Datei. **Auswahl exportieren** enthält nur das ausgewählte, lokal gespeicherte Makro. Der Export ist immer verschlüsselt; einen Klartext-Export gibt es nicht. Nicht lesbare Makrodateien werden übersprungen und in der Ergebnisübersicht mitgezählt.

Die Datei ist ein JSON-Umschlag mit `exportFormat: 1`, `kdf: "pbkdf2-sha256"` und standardmäßig 600.000 Iterationen. Sie enthält ein zufälliges Salt mit 16 Byte und eine Nonce mit 12 Byte. `cipher: "aes-256-gcm"` verwendet einen 256-Bit-Schlüssel und einen 16-Byte-Authentifizierungstag. Die Headerwerte werden authentifiziert; der Klartext mit den vollständigen Format-2-Makrodokumenten liegt nur innerhalb der Verschlüsselung. Beim Import werden 600.000 bis 5.000.000 Iterationen, höchstens 50 MB und höchstens 10.000 Makros akzeptiert. Diese Formatparameter sind keine Geheimnisse und ersetzen keine starke Passphrase.

Die Passphrase muss mindestens 12 Zeichen lang sein und wird bei der Erstellung zweimal eingegeben. Sie wird nicht gespeichert und kann nicht wiederhergestellt werden. Eine vergessene Passphrase macht den Export unlesbar. Verwende eine starke, einzigartige Passphrase und bewahre sie getrennt von der Exportdatei auf. Bewahre die Exportdatei außerdem an einem sicheren Ort getrennt von diesem Rechner auf.

DPAPI-geschützte lokale Makros lassen sich nur mit dem Windows-Benutzerkonto öffnen, für das sie geschützt wurden. DPAPI schützt gespeicherte Dateien im Ruhezustand, aber nicht vor Schadsoftware, die im selben Windows-Benutzerkonto läuft.

## Import

Beim Import wird die Datei zunächst geprüft und entschlüsselt. Falsche Passphrase und beschädigte Datei werden bewusst mit derselben neutralen Meldung behandelt. Ungültige Makros werden einzeln gezählt; die übrigen können weiter importiert werden. Namenskollisionen werden standardmäßig übersprungen. Eine Kopie erhält einen eindeutigen Namenszusatz; Überschreiben verlangt eine zusätzliche ausdrückliche Bestätigung. Importierte Makros werden gemäß **Makros verschlüsseln** gespeichert: bei aktivierter Einstellung im lokalen Format 3 mit DPAPI, sonst im lokalen Format 2.

## Standardkürzel und Grenzen

- **F9:** Aufnahme global starten oder stoppen. Beim Start erscheint eine Datenschutzabfrage.
- **F10:** Wiedergabe global starten oder stoppen. Vor dem Start läuft ein sichtbarer Drei-Sekunden-Countdown.
- Aufnahme- und Wiedergabekürzel lassen sich unabhängig auf F1 bis F12 mit optional Ctrl, Alt oder Shift setzen. Beide Kürzel müssen verschieden sein.
- Während Countdown oder Wiedergabe stoppt **Esc** die Wiedergabe. Die Taste wird in diesem Moment nicht an die Zielanwendung weitergegeben.

Windows-Eingaben und Timer werden nicht in Echtzeit garantiert. Synthetische Eingaben können durch geschützte Anwendungen, Anti-Cheat oder Raw-Input-Beschränkungen blockiert werden. Weitere Hinweise stehen in der Haupt-[README](../README.md#bekannte-einschränkungen).
