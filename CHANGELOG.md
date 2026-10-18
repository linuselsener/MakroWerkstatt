# Änderungsprotokoll

Alle bemerkenswerten Änderungen an MakroWerkstatt werden hier dokumentiert. Das Format folgt Keep a Changelog; Versionsnummern verwenden das Schema `x.y.z`.

## [Unreleased]

### Dokumentation

- Aktualisiert den Meldeweg für Sicherheitslücken und präzisiert die Angaben zum Datenpfad.
- Zeigt die neueste Release-Version dynamisch an und verwendet einen versionsunabhängigen Prüfbefehl.

## [1.1.0] - 2026-10-17

### Hinzugefügt

- Makros lassen sich lokal mit Windows DPAPI schützen und als passphrase-verschlüsselte Exporte mit PBKDF2 und AES-GCM austauschen.
- Verschlüsselte Makroexporte können geprüft und importiert werden; Namenskollisionen werden behandelt.
- Der Aktionseditor unterstützt Mehrfachauswahl, Verschieben, Duplizieren, Löschen, Werte- und Zeitänderungen sowie Undo und Redo.
- Aktionen lassen sich in der Liste per Drag & Drop umordnen; dabei bleiben Zeitstempel an den Listenplätzen.
- Releasepakete enthalten ein versionsbezogenes ZIP und SHA-256-Prüfsummen für ZIP und EXE.

### Geändert

- Eingabeaufnahme, UI-Weiterleitung und Hotkey-Validierung sind in testbare Dienste beziehungsweise das ViewModel aufgeteilt.
- Die Versionsnummer wird aus der Projektversion abgeleitet und im Fenstertitel angezeigt.

### Sicherheit und Zuverlässigkeit

- Der Undo-Verlauf wird beim Makrowechsel und beim Schließen geleert.
- Editoroperationen prüfen Aktionsdokumente und halten gefundene Tasten- und Maustastenpaare zusammen.
- Zufallsfolgen- und Regressionstests decken Editor-Invarianten und abgelehnte Operationen ab.
