namespace MakroWerkstatt;

public interface IFileDialogService
{
    string? SaveMacroExport();
    string? OpenMacroExport();
}

public interface IPassphraseDialogService
{
    char[]? RequestPassphrase(bool confirm);
}

internal sealed class UnavailableFileDialogService : IFileDialogService
{
    public string? SaveMacroExport() => null;
    public string? OpenMacroExport() => null;
}

internal sealed class UnavailablePassphraseDialogService : IPassphraseDialogService
{
    public char[]? RequestPassphrase(bool confirm) => null;
}
