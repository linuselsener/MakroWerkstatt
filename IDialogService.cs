namespace MakroWerkstatt;

public enum DialogKind
{
    Information,
    Warning,
    Error
}

public enum ClosePromptChoice
{
    Save,
    Discard,
    Cancel
}

public enum ImportCollisionAction
{
    Skip,
    Copy,
    Overwrite
}

public interface IDialogService
{
    bool ConfirmRecordingConsent();
    bool ConfirmDiscardChanges();
    bool ConfirmDeleteMacro(string name);
    bool ConfirmEncryptExistingMacros(int macroCount);
    ImportCollisionAction ChooseImportCollisionAction();
    bool ConfirmImportOverwrite();
    ClosePromptChoice ConfirmCloseWithUnsavedChanges();
    void Show(string message, string title, DialogKind kind);
}
