using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace MakroWerkstatt;

public partial class PassphraseDialogWindow : Window
{
    private readonly bool _confirm;

    public char[]? Passphrase { get; private set; }

    public PassphraseDialogWindow(bool confirm)
    {
        InitializeComponent();
        _confirm = confirm;
        Title = confirm ? "Export-Passphrase" : "Import-Passphrase";
        HeadingText.Text = confirm ? "Export-Passphrase festlegen" : "Export-Passphrase eingeben";
        ExplanationText.Text = confirm
            ? "Mindestens 12 Zeichen. Passphrase vergessen = Export unlesbar. Sie wird weder gespeichert noch wiederhergestellt."
            : "Die Passphrase wird nur für diesen Import verwendet und nicht gespeichert.";
        if (confirm) ConfirmationPanel.Visibility = Visibility.Visible;
        Loaded += (_, _) => PrimaryPasswordBox.Focus();
        Closed += (_, _) => ClearBoxes();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        var primary = CopySecurePassword(PrimaryPasswordBox);
        if (primary.Length < MacroExportService.MinimumPassphraseLength)
        {
            Zero(primary);
            ValidationText.Text = "Die Passphrase muss mindestens 12 Zeichen lang sein.";
            PrimaryPasswordBox.Clear();
            PrimaryPasswordBox.Focus();
            return;
        }

        if (_confirm)
        {
            var confirmation = CopySecurePassword(ConfirmationPasswordBox);
            var matches = SecureEquals(primary, confirmation);
            Zero(confirmation);
            if (!matches)
            {
                Zero(primary);
                ValidationText.Text = "Die Passphrasen stimmen nicht überein.";
                ClearBoxes();
                PrimaryPasswordBox.Focus();
                return;
            }
        }

        ClearBoxes();
        Passphrase = primary;
        DialogResult = true;
    }

    private static char[] CopySecurePassword(PasswordBox box)
    {
        using var secure = box.SecurePassword;
        if (secure.Length == 0) return [];
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            var characters = new char[secure.Length];
            Marshal.Copy(pointer, characters, 0, characters.Length);
            return characters;
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }
    }

    private static bool SecureEquals(char[] left, char[] right)
    {
        if (left.Length != right.Length) return false;
        var difference = 0;
        for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
        return difference == 0;
    }

    private void ClearBoxes()
    {
        PrimaryPasswordBox.Clear();
        ConfirmationPasswordBox.Clear();
    }

    private static void Zero(char[] characters) =>
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
}
