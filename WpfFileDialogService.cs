using Microsoft.Win32;
using System.Windows;

namespace MakroWerkstatt;

public sealed class WpfFileDialogService(Window owner) : IFileDialogService
{
    private const string ExportFilter = "MakroWerkstatt-Export (*.mwexport)|*.mwexport";

    public string? SaveMacroExport()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Makros verschlüsselt exportieren",
            Filter = ExportFilter,
            DefaultExt = ".mwexport",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            FileName = "MakroWerkstatt-Backup.mwexport"
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public string? OpenMacroExport()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Makros importieren",
            Filter = ExportFilter,
            DefaultExt = ".mwexport",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}

public sealed class WpfPassphraseDialogService(Window owner) : IPassphraseDialogService
{
    public char[]? RequestPassphrase(bool confirm)
    {
        var dialog = new PassphraseDialogWindow(confirm) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Passphrase : null;
    }
}
