using System.Windows;

namespace MakroWerkstatt;

public sealed class WpfDialogService(Window owner) : IDialogService
{
    public bool ConfirmRecordingConsent() => MessageBox.Show(owner,
        "Die Aufnahme erfasst Maus- und Tastaturaktionen in anderen Fenstern, solange sie läuft. Texteingaben können Passwörter oder andere vertrauliche Inhalte enthalten. Eingaben in MakroWerkstatt selbst werden ausgelassen.\n\nJetzt ausdrücklich mit der Aufnahme beginnen?",
        "Aufnahme starten · Datenschutz", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
        == MessageBoxResult.OK;

    public bool ConfirmDiscardChanges() => MessageBox.Show(owner,
        "Es gibt nicht gespeicherte Änderungen. Sollen sie verworfen werden?",
        "Ungespeicherte Änderungen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
        == MessageBoxResult.Yes;

    public bool ConfirmDeleteMacro(string name) => MessageBox.Show(owner,
        $"„{name}“ und alle dazugehörigen lokal gespeicherten Aktionen endgültig löschen? Nicht gespeicherte Änderungen werden ebenfalls verworfen.",
        "Makro löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
        == MessageBoxResult.Yes;

    public bool ConfirmEncryptExistingMacros(int macroCount) => MessageBox.Show(owner,
        $"Sollen {macroCount} bestehende Klartext-Makrodatei(en) jetzt verschlüsselt werden? Jede Datei wird vor dem Ersetzen zurückgelesen und geprüft.",
        "Bestehende Makros verschlüsseln", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
        == MessageBoxResult.Yes;

    public ClosePromptChoice ConfirmCloseWithUnsavedChanges() => MessageBox.Show(owner,
        "Es gibt noch nicht gespeicherte Makroänderungen. Möchtest du sie vor dem Schließen speichern?",
        "Makro nicht gespeichert", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) switch
    {
        MessageBoxResult.Yes => ClosePromptChoice.Save,
        MessageBoxResult.No => ClosePromptChoice.Discard,
        _ => ClosePromptChoice.Cancel
    };

    public void Show(string message, string title, DialogKind kind) => MessageBox.Show(owner, message, title,
        MessageBoxButton.OK, kind switch
        {
            DialogKind.Information => MessageBoxImage.Information,
            DialogKind.Warning => MessageBoxImage.Warning,
            _ => MessageBoxImage.Error
        });
}
