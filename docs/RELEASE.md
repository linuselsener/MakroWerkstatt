# Release erstellen

1. Setze `<Version>` in `MakroWerkstatt.csproj` auf die nächste Version im Format `x.y.z` und ergänze belegte Änderungen in `CHANGELOG.md`.
2. Committe die Änderung auf `main`. Setze den passenden Tag mit `git tag vX.Y.Z` und pushe ihn. Der Tag muss exakt zur Projektversion passen.
3. Der Tag `v*` startet `.github/workflows/release.yml`. Der Workflow baut die Solution, führt xUnit- und Smoke-Tests aus und erstellt das selbstständige Windows-x64-ZIP mit `SHA256SUMS.txt`.
4. Der Workflow legt mit der vorinstallierten GitHub CLI ein GitHub Release als **Entwurf** an. Die Notizen stammen aus dem passenden Abschnitt in `CHANGELOG.md`; fehlt dieser Abschnitt, wird ein Hinweistext verwendet. Der Entwurf wird nicht automatisch veröffentlicht.
5. Prüfe die angehängten Dateien und vergleiche die Prüfsummen. Für eine lokal erzeugte Veröffentlichung kann `tools/publish-windows.ps1 -NoShortcut` verwendet werden.

Die EXE ist nicht signiert. Windows SmartScreen kann deshalb beim ersten Start eine Warnung anzeigen. Prüfe die Herkunft der ZIP und die angezeigten SHA-256-Werte vor der Verwendung.
