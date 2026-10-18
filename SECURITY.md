# Sicherheitsmeldungen

Unterstützt wird jeweils die neueste veröffentlichte Version von MakroWerkstatt.

Melde Sicherheitslücken über **Security → Report a vulnerability** im Repository. Falls der Link nicht funktioniert, eröffne ein Issue ohne technische Details und bitte dort um einen privaten Meldeweg. Veröffentliche in einem Issue keine Makrodateien, Zugangsdaten oder Details, mit denen sich eine Schwachstelle ausnutzen lässt.

Makros werden standardmäßig mit Windows DPAPI für das aktuelle Windows-Benutzerkonto verschlüsselt. Einstellungen und Logs bleiben unverschlüsselt. Der Datenpfad ist konfigurierbar; globale Eingabe-Hooks laufen, solange das Hauptfenster geöffnet ist, während Eingaben nur nach Bestätigung und während einer aktiven Aufnahme als Makro-Aktionen übernommen werden. Weitere Details zu Speicherorten und Grenzen stehen in [Datenschutz und Sicherheit](README.md#datenschutz-und-sicherheit).

Die verteilte EXE ist nicht digital signiert. Die Anwendung kann synthetische Eingaben an Windows senden; eine Funktion in Spielen oder Programmen mit Anti-Cheat- oder Raw-Input-Beschränkungen wird nicht garantiert.
