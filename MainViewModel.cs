using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;

namespace MakroWerkstatt;

public enum RecordingState
{
    Idle,
    Recording,
    Paused
}

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly MacroStorage _storage;
    private readonly IDialogService _dialogs;
    private readonly IFileDialogService _fileDialogs;
    private readonly IPassphraseDialogService _passphraseDialogs;
    private readonly MacroExportService _exportService;
    private readonly MacroExportFileWriter _exportFileWriter;
    private readonly IInputCaptureService _inputCaptureService;
    private readonly MacroEditService _macroEditService = new();
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private readonly Stopwatch _recordClock = new();
    private readonly object _settingsSaveLock = new();
    private CancellationTokenSource? _playbackCancellation;
    private Timer? _settingsSaveTimer;
    private MacroAction? _selectedAction;
    private MacroDocument? _currentMacro;
    private MacroDocument? _selectedLibraryItem;
    private RecordingState _recordingState;
    private bool _isPlaying;
    private bool _isDirty;
    private bool _metadataDirty;
    private bool _currentIsSaved;
    private bool _inputHookAvailable;
    private bool _relativeMouseCaptureAvailable;
    private string _macroName = "Neue Aufnahme";
    private string _footerMessage = "Bereit";
    private string _playbackDetail = "";
    private string _estimateText = "Geschätzte Laufzeit: –";
    private string _estimateDetailText = "Wähle ein Makro, um die Laufzeit zu berechnen.";
    private bool _stretchEnabled = true;
    private string _duration = "1";
    private int _durationUnitIndex;
    private bool _randomizerEnabled;
    private bool _relativeMouseMovement;
    private bool _encryptMacros = true;
    private string _repeatCount = "1";
    private string _randomMinMs = "1";
    private string _randomMaxMs = "80";
    private bool _loadingSettings;
    private bool _settingsLoaded;
    private bool _disposed;
    private GlobalHotkey _recordingHotkey = new(0x78, HotkeyModifiers.None);
    private GlobalHotkey _playbackHotkey = new(0x79, HotkeyModifiers.None);
    private string _editDelayMs = "0";
    private string _pauseDurationMs = "1000";
    private string _editX = "0";
    private string _editY = "0";
    private string _editWheelDelta = "0";
    private string _editButton = MacroMouseButtons.Left;

    public ObservableCollection<MacroAction> Actions { get; } = [];
    public ObservableCollection<MacroDocument> Library { get; } = [];
    public ObservableCollection<MacroAction> SelectedActions { get; } = [];
    public ICommand RemoveActionCommand { get; }
    public ICommand DeleteActionsCommand { get; }
    public ICommand MoveActionUpCommand { get; }
    public ICommand MoveActionDownCommand { get; }
    public ICommand DuplicateActionsCommand { get; }
    public ICommand InsertPauseCommand { get; }
    public ICommand UndoActionEditCommand { get; }
    public ICommand RedoActionEditCommand { get; }
    public ICommand ApplyActionEditsCommand { get; }
    public ICommand ReloadLibraryCommand { get; }
    public ICommand SaveMacroCommand { get; }
    public ICommand DeleteMacroCommand { get; }
    public ICommand EncryptExistingMacrosCommand { get; }
    public ICommand ExportAllMacrosCommand { get; }
    public ICommand ExportSelectedMacroCommand { get; }
    public ICommand ImportMacrosCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand StopPlaybackCommand { get; }
    public ICommand StartRecordingCommand { get; }
    public ICommand ToggleRecordingPauseCommand { get; }
    public ICommand StopRecordingCommand { get; }
    public ICommand DiscardRecordingCommand { get; }

    public event EventHandler? EstimateInvalidated;
    public event EventHandler? RecordingStartRequested;
    public event EventHandler? PlaybackStartRequested;
    public event EventHandler? PlaybackStateChanged;
    public event EventHandler? PlaybackFinished;
    public event EventHandler? EditorSelectionChanged;

    /// <summary>Allows the view to keep its window-geometry input exclusion while input remains service-driven.</summary>
    public Func<MacroAction, bool>? ShouldIgnoreCapturedAction { get; set; }

    public MacroAction? SelectedAction
    {
        get => _selectedAction;
        set => SetSelectedActions(value is null ? [] : [value]);
    }

    public string EditDelayMs { get => _editDelayMs; set => SetProperty(ref _editDelayMs, value); }
    public string PauseDurationMs { get => _pauseDurationMs; set => SetProperty(ref _pauseDurationMs, value); }
    public string EditX { get => _editX; set => SetProperty(ref _editX, value); }
    public string EditY { get => _editY; set => SetProperty(ref _editY, value); }
    public string EditWheelDelta { get => _editWheelDelta; set => SetProperty(ref _editWheelDelta, value); }
    public string EditButton { get => _editButton; set => SetProperty(ref _editButton, value); }
    public IReadOnlyList<string> AllowedMouseButtons => MacroMouseButtons.AllowedValues;
    public string EditKeyDisplay => SelectedAction is { Type: MacroActionType.KeyDown or MacroActionType.KeyUp } action
        ? $"Taste · VK {action.KeyCode:X2}"
        : "Taste nicht ausgewählt";
    public bool IsKeyActionSelected => SelectedAction?.Type is MacroActionType.KeyDown or MacroActionType.KeyUp;
    public bool IsMouseButtonActionSelected => SelectedAction?.Type is MacroActionType.MouseButtonDown or MacroActionType.MouseButtonUp;
    public bool IsMousePositionActionSelected => SelectedAction?.Type is MacroActionType.MouseMove or MacroActionType.MouseMoveRelative
        or MacroActionType.MouseButtonDown or MacroActionType.MouseButtonUp or MacroActionType.MouseWheel;
    public bool IsWheelActionSelected => SelectedAction?.Type == MacroActionType.MouseWheel;
    public bool IsEditorEnabled => !IsBusy && CurrentMacro is not null;
    public bool CanInsertPauseAfterSelection => IsEditorEnabled && CanInsertPause();
    public bool CanEditMousePosition => IsEditorEnabled && IsMousePositionActionSelected;
    public bool CanEditWheelDelta => IsEditorEnabled && IsWheelActionSelected;
    public bool CanEditMouseButton => IsEditorEnabled && IsMouseButtonActionSelected;
    public bool CanCaptureActionKey => IsEditorEnabled && IsKeyActionSelected;
    public bool CanUndoActionEdit => _macroEditService.CanUndo;
    public bool CanRedoActionEdit => _macroEditService.CanRedo;

    public MacroDocument? CurrentMacro
    {
        get => _currentMacro;
        set
        {
            if (SetProperty(ref _currentMacro, value))
            {
                OnPropertyChanged(nameof(IsEditorEnabled));
                OnPropertyChanged(nameof(CanInsertPauseAfterSelection));
                OnPropertyChanged(nameof(CanEditMousePosition));
                OnPropertyChanged(nameof(CanEditWheelDelta));
                OnPropertyChanged(nameof(CanEditMouseButton));
                OnPropertyChanged(nameof(CanCaptureActionKey));
                RaiseCommandStates();
            }
        }
    }

    public MacroDocument? SelectedLibraryItem
    {
        get => _selectedLibraryItem;
        set
        {
            if (ReferenceEquals(_selectedLibraryItem, value)) return;
            if (value is null)
            {
                SetProperty(ref _selectedLibraryItem, null);
                RaiseCommandStates();
                return;
            }
            if (!CanDiscardCurrentChanges())
            {
                OnPropertyChanged();
                RaiseCommandStates();
                return;
            }
            LoadSelectedMacro(value);
        }
    }

    public RecordingState RecordingState
    {
        get => _recordingState;
        private set
        {
            if (!SetProperty(ref _recordingState, value)) return;
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsEditorEnabled));
            OnPropertyChanged(nameof(CanInsertPauseAfterSelection));
            OnPropertyChanged(nameof(CanEditMousePosition));
            OnPropertyChanged(nameof(CanEditWheelDelta));
            OnPropertyChanged(nameof(CanEditMouseButton));
            OnPropertyChanged(nameof(CanCaptureActionKey));
            RaiseCommandStates();
        }
    }

    public bool IsRecording => RecordingState == RecordingState.Recording;
    public bool IsPaused => RecordingState == RecordingState.Paused;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!SetProperty(ref _isPlaying, value)) return;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsEditorEnabled));
            OnPropertyChanged(nameof(CanInsertPauseAfterSelection));
            OnPropertyChanged(nameof(CanEditMousePosition));
            OnPropertyChanged(nameof(CanEditWheelDelta));
            OnPropertyChanged(nameof(CanEditMouseButton));
            OnPropertyChanged(nameof(CanCaptureActionKey));
            RaiseCommandStates();
            UpdateEstimate();
        }
    }

    public bool IsBusy => IsRecording || IsPaused || IsPlaying;

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (SetProperty(ref _isDirty, value))
                ((RelayCommand)SaveMacroCommand).RaiseCanExecuteChanged();
        }
    }

    public bool CurrentIsSaved
    {
        get => _currentIsSaved;
        set
        {
            if (SetProperty(ref _currentIsSaved, value))
                ((RelayCommand)DeleteMacroCommand).RaiseCanExecuteChanged();
        }
    }

    public bool InputHookAvailable
    {
        get => _inputHookAvailable;
        set
        {
            if (SetProperty(ref _inputHookAvailable, value))
                RaiseCommandStates();
        }
    }

    public bool RelativeMouseCaptureAvailable
    {
        get => _relativeMouseCaptureAvailable;
        private set => SetProperty(ref _relativeMouseCaptureAvailable, value);
    }

    public string MacroName
    {
        get => _macroName;
        set
        {
            if (!SetProperty(ref _macroName, value)) return;
            if (CurrentMacro is not null)
            {
                CurrentMacro.Name = value;
                _metadataDirty = true;
                _macroEditService.UpdateDocument(CurrentMacro);
                IsDirty = true;
                RaiseCommandStates();
            }
        }
    }

    public string FooterMessage
    {
        get => _footerMessage;
        set => SetProperty(ref _footerMessage, value);
    }

    public string PlaybackDetail
    {
        get => _playbackDetail;
        private set => SetProperty(ref _playbackDetail, value);
    }

    public string EstimateText
    {
        get => _estimateText;
        private set => SetProperty(ref _estimateText, value);
    }

    public string EstimateDetailText
    {
        get => _estimateDetailText;
        private set => SetProperty(ref _estimateDetailText, value);
    }

    public GlobalHotkey RecordingHotkey => _recordingHotkey;
    public GlobalHotkey PlaybackHotkey => _playbackHotkey;
    public string RecordingHotkeyText => _recordingHotkey.ToString();
    public string PlaybackHotkeyText => _playbackHotkey.ToString();

    public bool StretchEnabled { get => _stretchEnabled; set => UpdateSetting(ref _stretchEnabled, value); }
    public string Duration { get => _duration; set => UpdateSetting(ref _duration, value); }
    public int DurationUnitIndex { get => _durationUnitIndex; set => UpdateSetting(ref _durationUnitIndex, value); }
    public bool RandomizerEnabled { get => _randomizerEnabled; set => UpdateSetting(ref _randomizerEnabled, value); }
    public bool RelativeMouseMovement { get => _relativeMouseMovement; set => UpdateSetting(ref _relativeMouseMovement, value); }
    public bool EncryptMacros { get => _encryptMacros; set => UpdateSetting(ref _encryptMacros, value); }
    public string RepeatCount { get => _repeatCount; set => UpdateSetting(ref _repeatCount, value); }
    public string RandomMinMs { get => _randomMinMs; set => UpdateSetting(ref _randomMinMs, value); }
    public string RandomMaxMs { get => _randomMaxMs; set => UpdateSetting(ref _randomMaxMs, value); }

    public bool SuppressPlaybackErrorDialog { get; set; }

    public TimeSpan RecordingElapsed => _recordClock.Elapsed;

    public string ActionCountText => Actions.Count == 1
        ? "  ·  1 Aktion"
        : $"  ·  {Actions.Count:N0} Aktionen";

    public string MacroCountText => Library.Count == 1 ? "1 Makro" : $"{Library.Count:N0} Makros";
    public string StorageFolderPath => _storage.MacroFolder;

    public MainViewModel(MacroStorage storage, IDialogService dialogs, IInputCaptureService? inputCaptureService = null,
        IFileDialogService? fileDialogs = null, IPassphraseDialogService? passphraseDialogs = null,
        MacroExportService? exportService = null, MacroExportFileWriter? exportFileWriter = null)
    {
        _storage = storage;
        _dialogs = dialogs;
        _fileDialogs = fileDialogs ?? new UnavailableFileDialogService();
        _passphraseDialogs = passphraseDialogs ?? new UnavailablePassphraseDialogService();
        _exportService = exportService ?? new MacroExportService();
        _exportFileWriter = exportFileWriter ?? new MacroExportFileWriter();
        _inputCaptureService = inputCaptureService ?? new InertInputCaptureService();
        RemoveActionCommand = new RelayCommand(RemoveSelectedActions, () => CanEditActions && SelectedActions.Count > 0);
        DeleteActionsCommand = RemoveActionCommand;
        MoveActionUpCommand = new RelayCommand(() => MoveSelectedActions(moveUp: true), CanMoveSelectedActionsUp);
        MoveActionDownCommand = new RelayCommand(() => MoveSelectedActions(moveUp: false), CanMoveSelectedActionsDown);
        DuplicateActionsCommand = new RelayCommand(DuplicateSelectedActions, () => CanEditActions && SelectedActions.Count > 0
            && Actions.Count + SelectedActions.Count <= MacroStorage.MaximumActionCount);
        InsertPauseCommand = new RelayCommand(InsertPause, CanInsertPause);
        UndoActionEditCommand = new RelayCommand(UndoActionEdit, () => CanEditActions && _macroEditService.CanUndo);
        RedoActionEditCommand = new RelayCommand(RedoActionEdit, () => CanEditActions && _macroEditService.CanRedo);
        ApplyActionEditsCommand = new RelayCommand(ApplyActionEdits, () => CanEditActions && SelectedAction is not null);
        ReloadLibraryCommand = new RelayCommand(ReloadLibrary, () => !IsBusy);
        SaveMacroCommand = new RelayCommand(() => SaveCurrentMacro(showSuccess: true), CanSaveMacro);
        DeleteMacroCommand = new RelayCommand(DeleteCurrentMacro, () => !IsBusy && CurrentIsSaved && CurrentMacro is not null);
        EncryptExistingMacrosCommand = new RelayCommand(EncryptExistingMacros, () => !IsBusy);
        ExportAllMacrosCommand = new RelayCommand(() => ExportMacros(selectedOnly: false), () => !IsBusy && Library.Count > 0);
        ExportSelectedMacroCommand = new RelayCommand(() => ExportMacros(selectedOnly: true), () => !IsBusy && SelectedLibraryItem is not null);
        ImportMacrosCommand = new RelayCommand(ImportMacros, () => !IsBusy);
        PlayCommand = new RelayCommand(StartPlayback, CanPlay);
        StopPlaybackCommand = new RelayCommand(StopPlayback, () => IsPlaying);
        StartRecordingCommand = new RelayCommand(StartRecording, () => !IsBusy);
        ToggleRecordingPauseCommand = new RelayCommand(ToggleRecordingPause, () => IsRecording || IsPaused);
        StopRecordingCommand = new RelayCommand(StopRecording, () => IsRecording || IsPaused);
        DiscardRecordingCommand = new RelayCommand(DiscardRecording, () => IsRecording || IsPaused);
        Actions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ActionCountText));
            OnPropertyChanged(nameof(RecordingElapsed));
            RaiseCommandStates();
            UpdateEstimate();
            EstimateInvalidated?.Invoke(this, EventArgs.Empty);
        };
        Library.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MacroCountText));
            RaiseCommandStates();
        };
        _inputCaptureService.ActionCaptured += InputCaptureService_ActionCaptured;
        _inputCaptureService.HotkeyPressed += InputCaptureService_HotkeyPressed;
        _inputCaptureService.EmergencyStopPressed += InputCaptureService_EmergencyStopPressed;
        _inputCaptureService.StatusChanged += InputCaptureService_StatusChanged;
        _inputCaptureService.SetHotkeys(_recordingHotkey, _playbackHotkey);
        UpdateEstimate();
    }

    public void LoadLibrary(bool showWarnings)
    {
        var documents = _storage.LoadAll(out var warnings);
        Library.Clear();
        foreach (var document in documents) Library.Add(document);
        SetSelectedLibraryItem(null);
        if (showWarnings && warnings.Count > 0)
        {
            FooterMessage = $"{warnings.Count} Datei(en) konnten nicht geöffnet werden";
            _dialogs.Show("Einige Makrodateien konnten nicht geladen werden:\n\n" + string.Join("\n", warnings.Take(8)) +
                (warnings.Count > 8 ? "\n…" : ""), "MakroWerkstatt · Bibliothek", DialogKind.Warning);
        }
    }

    public void ReloadLibrary()
    {
        if (IsBusy || !CanDiscardCurrentChanges()) return;
        ResetCurrentMacro();
        LoadLibrary(showWarnings: true);
        FooterMessage = "Bibliothek neu geladen";
    }

    public bool SaveCurrentMacro(bool showSuccess)
    {
        if (CurrentMacro is null || Actions.Count == 0)
        {
            _dialogs.Show("Es gibt noch keine Aktionen zum Speichern. Starte eine Aufnahme und beende sie zuerst.",
                "Noch nichts zu speichern", DialogKind.Information);
            return false;
        }
        if (IsBusy)
        {
            _dialogs.Show("Beende zuerst die laufende Aufnahme oder Wiedergabe.",
                "Speichern gerade nicht möglich", DialogKind.Information);
            return false;
        }

        var name = MacroName.Trim();
        if (name.Length is < 1 or > 100)
        {
            _dialogs.Show("Der Name muss zwischen 1 und 100 Zeichen lang sein.", "Ungültiger Makroname", DialogKind.Warning);
            return false;
        }

        CurrentMacro.Name = name;
        CurrentMacro.Actions = Actions.Select(CloneAction).ToList();
        CurrentMacro.UpdatedUtc = DateTimeOffset.UtcNow;
        CurrentMacro.FormatVersion = MacroStorage.PayloadFormatVersion;
        try
        {
            _storage.Save(CurrentMacro, EncryptMacros);
        }
        catch (Exception)
        {
            _dialogs.Show("Das Makro konnte nicht lokal gespeichert oder geprüft werden. Die bisherige Datei wurde nicht ersetzt.",
                "Speichern fehlgeschlagen", DialogKind.Error);
            return false;
        }

        CurrentIsSaved = true;
        _metadataDirty = false;
        _macroEditService.UpdateDocument(CurrentMacro);
        _macroEditService.MarkSaved();
        IsDirty = false;
        var index = -1;
        for (var i = 0; i < Library.Count; i++)
            if (Library[i].Id == CurrentMacro.Id) { index = i; break; }
        if (index >= 0) Library[index] = CurrentMacro;
        else Library.Insert(0, CurrentMacro);
        SetSelectedLibraryItem(CurrentMacro);
        FooterMessage = $"„{name}“ lokal gespeichert";
        RaiseCommandStates();
        EstimateInvalidated?.Invoke(this, EventArgs.Empty);
        if (showSuccess)
            _dialogs.Show($"„{name}“ wurde lokal gespeichert.\n\n{_storage.MacroFolder}",
                "Makro gespeichert", DialogKind.Information);
        return true;
    }

    private void ExportMacros(bool selectedOnly)
    {
        if (IsBusy) return;
        char[]? passphrase = null;
        byte[]? exportBytes = null;
        try
        {
            passphrase = _passphraseDialogs.RequestPassphrase(confirm: true);
            if (passphrase is null) return;
            if (passphrase.Length < MacroExportService.MinimumPassphraseLength)
            {
                _dialogs.Show("Die Passphrase muss mindestens 12 Zeichen lang sein.",
                    "Passphrase zu kurz", DialogKind.Warning);
                return;
            }

            IReadOnlyList<MacroDocument> documents;
            var skipped = 0;
            if (selectedOnly)
            {
                var selected = SelectedLibraryItem;
                if (selected is null) return;
                try
                {
                    documents = [_storage.LoadById(selected.Id)];
                }
                catch (Exception)
                {
                    documents = [];
                    skipped = 1;
                }
            }
            else
            {
                documents = _storage.LoadAll(out var warnings);
                skipped = warnings.Count;
            }

            if (documents.Count == 0)
            {
                FooterMessage = $"Makroexport abgeschlossen · 0 exportiert, {skipped} übersprungen";
                _dialogs.Show($"Makros exportiert: 0\nÜbersprungen: {skipped}",
                    "Makroexport abgeschlossen", skipped == 0 ? DialogKind.Information : DialogKind.Warning);
                return;
            }

            var destination = _fileDialogs.SaveMacroExport();
            if (string.IsNullOrWhiteSpace(destination)) return;

            exportBytes = _exportService.Export(documents, passphrase);
            _exportFileWriter.WriteAtomically(destination, exportBytes);
            FooterMessage = $"Makroexport abgeschlossen · {documents.Count} exportiert, {skipped} übersprungen";
            _dialogs.Show($"Makros exportiert: {documents.Count}\nÜbersprungen: {skipped}",
                "Makroexport abgeschlossen", skipped == 0 ? DialogKind.Information : DialogKind.Warning);
        }
        catch (Exception)
        {
            _dialogs.Show("Der verschlüsselte Export konnte nicht sicher erstellt oder gespeichert werden. Eine bestehende Zieldatei wurde nicht ersetzt.",
                "Makroexport fehlgeschlagen", DialogKind.Error);
        }
        finally
        {
            if (passphrase is not null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(passphrase.AsSpan()));
            if (exportBytes is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(exportBytes);
        }
    }

    private void ImportMacros()
    {
        if (IsBusy) return;
        if (IsDirty)
        {
            _dialogs.Show("Speichere oder verwirf zuerst die ungespeicherten Änderungen, bevor du Makros importierst.",
                "Import gerade nicht möglich", DialogKind.Warning);
            return;
        }

        var source = _fileDialogs.OpenMacroExport();
        if (string.IsNullOrWhiteSpace(source)) return;

        char[]? passphrase = null;
        try
        {
            passphrase = _passphraseDialogs.RequestPassphrase(confirm: false);
            if (passphrase is null) return;

            var export = _exportService.ImportFile(source, passphrase);
            var importedCount = 0;
            var skippedCount = 0;
            var knownMacros = _storage.LoadAll(out _).ToList();
            foreach (var macro in export.Macros)
            {
                var existing = knownMacros.FirstOrDefault(item => item.Id == macro.Id
                    || string.Equals(item.Name, macro.Name, StringComparison.OrdinalIgnoreCase))
                    ?? Library.FirstOrDefault(item => item.Id == macro.Id
                        || string.Equals(item.Name, macro.Name, StringComparison.OrdinalIgnoreCase));
                var storedIdExists = existing is null && _storage.HasStoredFile(macro.Id);
                if (existing is not null || storedIdExists)
                {
                    var action = _dialogs.ChooseImportCollisionAction();
                    if (action == ImportCollisionAction.Skip)
                    {
                        skippedCount++;
                        continue;
                    }
                    if (action == ImportCollisionAction.Overwrite)
                    {
                        if (!_dialogs.ConfirmImportOverwrite())
                        {
                            skippedCount++;
                            continue;
                        }
                        if (existing is not null) macro.Id = existing.Id;
                    }
                    else
                    {
                        AssignUniqueCopyIdentity(macro);
                        existing = null;
                    }
                }

                try
                {
                    _storage.Save(macro, EncryptMacros);
                    UpsertImportedMacro(macro, existing);
                    knownMacros.RemoveAll(item => item.Id == macro.Id
                        || (existing is not null && item.Id == existing.Id));
                    knownMacros.Add(macro);
                    importedCount++;
                }
                catch (Exception)
                {
                    skippedCount++;
                }
            }

            FooterMessage = $"Makroimport abgeschlossen · {importedCount} importiert, {skippedCount} übersprungen, {export.InvalidCount} ungültig";
            _dialogs.Show($"Importiert: {importedCount}\nÜbersprungen: {skippedCount}\nUngültig: {export.InvalidCount}",
                "Makroimport abgeschlossen",
                skippedCount == 0 && export.InvalidCount == 0 ? DialogKind.Information : DialogKind.Warning);
        }
        catch (Exception)
        {
            _dialogs.Show("Falsche Passphrase oder beschädigte Datei.", "Import nicht möglich", DialogKind.Error);
        }
        finally
        {
            if (passphrase is not null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(passphrase.AsSpan()));
        }
    }

    private void AssignUniqueCopyIdentity(MacroDocument macro)
    {
        var baseName = macro.Name;
        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? " (Import)" : $" (Import {number})";
            var prefixLength = Math.Max(1, 100 - suffix.Length);
            var candidate = baseName[..Math.Min(baseName.Length, prefixLength)].TrimEnd() + suffix;
            if (candidate.Length <= 100 && !Library.Any(item => string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                macro.Name = candidate;
                break;
            }
        }

        do { macro.Id = Guid.NewGuid(); } while (_storage.HasStoredFile(macro.Id));
        macro.UpdatedUtc = DateTimeOffset.UtcNow;
    }

    private void UpsertImportedMacro(MacroDocument macro, MacroDocument? previous)
    {
        var index = previous is null ? -1 : Library.IndexOf(previous);
        if (index < 0) index = Library.ToList().FindIndex(item => item.Id == macro.Id);
        if (index >= 0) Library[index] = macro;
        else Library.Insert(0, macro);

        var wasSelected = SelectedLibraryItem?.Id == macro.Id;
        var wasCurrent = CurrentMacro?.Id == macro.Id;
        if (wasCurrent)
        {
            CurrentMacro = macro;
            _macroName = macro.Name;
            OnPropertyChanged(nameof(MacroName));
            CurrentIsSaved = true;
            _metadataDirty = false;
            _macroEditService.Reset(macro, macro.Actions, isSaved: true);
            IsDirty = false;
            SetSelectedActions([]);
            Actions.Clear();
            foreach (var action in macro.Actions) Actions.Add(action);
        }
        if (wasSelected) SetSelectedLibraryItem(macro);
        RaiseCommandStates();
    }

    private void DeleteCurrentMacro()
    {
        if (IsBusy || !CurrentIsSaved || CurrentMacro is null) return;
        var id = CurrentMacro.Id;
        var name = CurrentMacro.Name;
        if (!_dialogs.ConfirmDeleteMacro(name)) return;
        try
        {
            _storage.Delete(id);
        }
        catch (Exception ex)
        {
            _dialogs.Show($"Das Makro konnte nicht gelöscht werden.\n\n{ex.Message}", "Löschen fehlgeschlagen", DialogKind.Error);
            return;
        }

        var item = Library.FirstOrDefault(macro => macro.Id == id);
        if (item is not null) Library.Remove(item);
        ResetCurrentMacro();
        FooterMessage = $"„{name}“ gelöscht";
    }

    public void ResetCurrentMacro()
    {
        CurrentMacro = null;
        SetSelectedLibraryItem(null);
        CurrentIsSaved = false;
        _metadataDirty = false;
        IsDirty = false;
        _macroEditService.Reset(new MacroDocument(), [], isSaved: true);
        SetSelectedActions([]);
        Actions.Clear();
        MacroName = "Neue Aufnahme";
        RaiseCommandStates();
        EstimateInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public void LoadSettings()
    {
        var settings = _storage.LoadSettings();
        _loadingSettings = true;
        _stretchEnabled = settings.StretchEnabled;
        _duration = string.IsNullOrWhiteSpace(settings.Duration) ? "1" : settings.Duration;
        _durationUnitIndex = Math.Clamp(settings.DurationUnitIndex, 0, 1);
        _randomizerEnabled = settings.RandomizerEnabled;
        _relativeMouseMovement = settings.RelativeMouseMovement;
        _encryptMacros = settings.EncryptMacros;
        _repeatCount = string.IsNullOrWhiteSpace(settings.RepeatCount) ? "1" : settings.RepeatCount;
        _randomMinMs = string.IsNullOrWhiteSpace(settings.RandomMinMs) ? "1" : settings.RandomMinMs;
        _randomMaxMs = string.IsNullOrWhiteSpace(settings.RandomMaxMs) ? "80" : settings.RandomMaxMs;
        _recordingHotkey = ParseHotkeyOrDefault(settings.RecordingHotkey, "F9");
        _playbackHotkey = ParseHotkeyOrDefault(settings.PlaybackHotkey, "F10");
        if (_playbackHotkey == _recordingHotkey)
            _playbackHotkey = ParseHotkeyOrDefault(_recordingHotkey.ToString() == "F9" ? "F10" : "F9", "F10");
        _loadingSettings = false;
        _settingsLoaded = true;
        OnPropertyChanged(nameof(StretchEnabled));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationUnitIndex));
        OnPropertyChanged(nameof(RandomizerEnabled));
        OnPropertyChanged(nameof(RelativeMouseMovement));
        OnPropertyChanged(nameof(EncryptMacros));
        OnPropertyChanged(nameof(RepeatCount));
        OnPropertyChanged(nameof(RandomMinMs));
        OnPropertyChanged(nameof(RandomMaxMs));
        OnPropertyChanged(nameof(RecordingHotkey));
        OnPropertyChanged(nameof(PlaybackHotkey));
        OnPropertyChanged(nameof(RecordingHotkeyText));
        OnPropertyChanged(nameof(PlaybackHotkeyText));
        _inputCaptureService.SetHotkeys(_recordingHotkey, _playbackHotkey);
        UpdateEstimate();
    }

    public bool TrySetHotkey(bool forRecording, GlobalHotkey binding)
    {
        if (!GlobalHotkey.TryCreate(binding.VirtualKey, binding.Modifiers, out var validatedBinding) || validatedBinding is null)
        {
            FooterMessage = "Bitte F1 bis F12 drücken, optional mit Ctrl, Alt oder Shift";
            return false;
        }

        binding = validatedBinding;
        var other = forRecording ? _playbackHotkey : _recordingHotkey;
        if (binding == other)
        {
            FooterMessage = "Dieses Kürzel ist bereits für die andere Funktion belegt";
            return false;
        }

        if (forRecording) _recordingHotkey = binding;
        else _playbackHotkey = binding;
        OnPropertyChanged(forRecording ? nameof(RecordingHotkeyText) : nameof(PlaybackHotkeyText));
        OnPropertyChanged(forRecording ? nameof(RecordingHotkey) : nameof(PlaybackHotkey));
        FooterMessage = $"Tastenkürzel gespeichert · Aufnahme: {_recordingHotkey}, Wiedergabe: {_playbackHotkey}";
        _inputCaptureService.SetHotkeys(_recordingHotkey, _playbackHotkey);
        ScheduleSettingsSave();
        return true;
    }

    public bool TrySetHotkey(bool forRecording, int virtualKey, HotkeyModifiers modifiers)
    {
        if (!GlobalHotkey.TryCreate(virtualKey, modifiers, out var binding) || binding is null)
        {
            FooterMessage = "Bitte F1 bis F12 drücken, optional mit Ctrl, Alt oder Shift";
            return false;
        }

        return TrySetHotkey(forRecording, binding);
    }

    public void RemoveCapturedShortcutModifiers(IEnumerable<int> modifierKeys)
    {
        foreach (var keyCode in modifierKeys.Distinct())
        {
            var unmatchedKeyUps = 0;
            for (var index = Actions.Count - 1; index >= 0; index--)
            {
                var action = Actions[index];
                if (action.KeyCode != keyCode || action.Type is not (MacroActionType.KeyDown or MacroActionType.KeyUp)) continue;
                if (action.Type == MacroActionType.KeyUp)
                {
                    unmatchedKeyUps++;
                    continue;
                }
                if (unmatchedKeyUps > 0)
                {
                    unmatchedKeyUps--;
                    continue;
                }
                Actions.RemoveAt(index);
                IsDirty = true;
                break;
            }
        }
    }

    public bool TryClose()
    {
        if (IsRecording || IsPaused) StopRecording();
        if (!IsDirty) return true;
        return _dialogs.ConfirmCloseWithUnsavedChanges() switch
        {
            ClosePromptChoice.Save => SaveCurrentMacro(showSuccess: false),
            ClosePromptChoice.Discard => true,
            _ => false
        };
    }

    public void SaveSettings(bool showFailure)
    {
        if (_disposed) return;
        try
        {
            _storage.SaveSettings(new MacroSettings
            {
                StretchEnabled = StretchEnabled,
                Duration = Duration,
                DurationUnitIndex = Math.Clamp(DurationUnitIndex, 0, 1),
                RandomizerEnabled = RandomizerEnabled,
                RelativeMouseMovement = RelativeMouseMovement,
                EncryptMacros = EncryptMacros,
                RepeatCount = RepeatCount,
                RandomMinMs = RandomMinMs,
                RandomMaxMs = RandomMaxMs,
                RecordingHotkey = _recordingHotkey.ToString(),
                PlaybackHotkey = _playbackHotkey.ToString()
            });
        }
        catch (Exception ex)
        {
            if (showFailure) FooterMessage = $"Einstellungen konnten nicht gespeichert werden: {ex.Message}";
        }
    }

    public void Dispose()
    {
        lock (_settingsSaveLock)
        {
            _disposed = true;
            _settingsSaveTimer?.Dispose();
            _settingsSaveTimer = null;
        }
        _inputCaptureService.ActionCaptured -= InputCaptureService_ActionCaptured;
        _inputCaptureService.HotkeyPressed -= InputCaptureService_HotkeyPressed;
        _inputCaptureService.EmergencyStopPressed -= InputCaptureService_EmergencyStopPressed;
        _inputCaptureService.StatusChanged -= InputCaptureService_StatusChanged;
        _playbackCancellation?.Dispose();
        _playbackCancellation = null;
    }

    public void UpdateEstimate()
    {
        if (!TryCreatePlaybackPlan(out var plan, out var error) || plan is null)
        {
            EstimateText = "Geschätzte Laufzeit: –";
            EstimateDetailText = error;
            RaiseCommandStates();
            return;
        }

        EstimateText = $"Geschätzte Laufzeit: etwa {FormatDuration(plan.EstimatedAverageMs)}";
        var passSummary = plan.RepeatCount == 1 ? "1 Durchlauf" : $"{plan.RepeatCount} Durchläufe";
        if (StretchEnabled && plan.Actions.Count == 1 && plan.Actions[0].AtMs == 0)
        {
            EstimateDetailText = $"{passSummary} · Eine einzelne Aktion ohne Zeitabstand wird je Durchlauf unmittelbar ausgeführt.";
        }
        else if (plan.RandomizerEnabled)
        {
            var spacingNote = plan.EvenlyDistribute ? " Zeitstempel ohne Abstand werden gleichmäßig verteilt." : "";
            EstimateDetailText = $"{passSummary} · Mögliche Spanne {FormatDuration(plan.EstimatedMinimumMs)}–{FormatDuration(plan.EstimatedMaximumMs)} · {plan.TotalTransitionCount:N0} Übergänge mit je {plan.RandomMinMs}–{plan.RandomMaxMs} ms Zusatzwartezeit.{spacingNote}";
        }
        else if (plan.EvenlyDistribute)
        {
            EstimateDetailText = $"{passSummary} · Die Aufnahme enthält keine messbaren Zeitabstände; die Aktionen werden pro Durchlauf gleichmäßig über die Zieldauer verteilt.";
        }
        else if (StretchEnabled)
        {
            EstimateDetailText = $"{passSummary} · Die Aktionen werden je Durchlauf auf die eingestellte Zieldauer verteilt.";
        }
        else
        {
            EstimateDetailText = $"{passSummary} im aufgezeichneten Timing · Windows kann einzelne Zeitabstände leicht variieren.";
        }
        RaiseCommandStates();
    }

    private bool TryCreatePlaybackPlan(out PlaybackPlan? plan, out string error)
    {
        plan = null;
        error = "";
        if (CurrentMacro is null || Actions.Count == 0)
        {
            error = "Wähle ein gespeichertes Makro oder nimm zuerst Aktionen auf.";
            return false;
        }

        if (!int.TryParse(RepeatCount, NumberStyles.Integer, CultureInfo.CurrentCulture, out var repeatCount) &&
            !int.TryParse(RepeatCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out repeatCount))
        {
            error = "Die Anzahl der Durchläufe muss eine ganze Zahl zwischen 1 und 100 sein.";
            return false;
        }
        if (repeatCount is < 1 or > 100)
        {
            error = "Die Anzahl der Durchläufe muss zwischen 1 und 100 liegen.";
            return false;
        }

        var durationMs = 0d;
        if (StretchEnabled)
        {
            if (!decimal.TryParse(Duration, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) &&
                !decimal.TryParse(Duration, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
            {
                error = "Gib für die Zieldauer eine gültige Zahl ein.";
                return false;
            }
            if (amount <= 0)
            {
                error = "Die Zieldauer muss größer als null sein.";
                return false;
            }
            durationMs = (double)amount * (DurationUnitIndex == 1 ? 3_600_000d : 60_000d);
            if (durationMs > 24 * 60 * 60 * 1000d)
            {
                error = "Die Zieldauer darf höchstens 24 Stunden betragen.";
                return false;
            }
        }

        var min = 1;
        var max = 80;
        if (RandomizerEnabled)
        {
            if (!int.TryParse(RandomMinMs, out min) || !int.TryParse(RandomMaxMs, out max))
            {
                error = "Minimale und maximale Zusatzverzögerung müssen ganze Millisekunden sein.";
                return false;
            }
            if (min < 1 || max > 1000 || min > max)
            {
                error = "Die Verzögerung muss zwischen 1 und 1000 ms liegen; Min. darf Max. nicht überschreiten.";
                return false;
            }
        }

        var macroForPlayback = new MacroDocument
        {
            Id = CurrentMacro.Id,
            Name = CurrentMacro.Name,
            FormatVersion = CurrentMacro.FormatVersion,
            Actions = Actions.ToList()
        };
        plan = PlaybackPlan.Create(macroForPlayback, StretchEnabled, durationMs, RandomizerEnabled, min, max, repeatCount);
        return true;
    }

    private bool CanPlay() => InputHookAvailable && !IsBusy && TryCreatePlaybackPlan(out _, out _);

    private async void StartPlayback()
    {
        PlaybackStartRequested?.Invoke(this, EventArgs.Empty);
        if (IsBusy) return;
        if (!InputHookAvailable)
        {
            _dialogs.Show("Der globale Eingabe-Hook ist nicht verfügbar. Die Wiedergabe kann nicht gestartet werden.",
                "Wiedergabe nicht verfügbar", DialogKind.Warning);
            return;
        }
        if (!TryCreatePlaybackPlan(out var plan, out var error) || plan is null)
        {
            _dialogs.Show(error, "Wiedergabe nicht möglich", DialogKind.Warning);
            UpdateEstimate();
            return;
        }

        _playbackCancellation = new CancellationTokenSource();
        var cancellationToken = _playbackCancellation.Token;
        var hasRelativeMoves = plan.Actions.Any(action => action.Type == MacroActionType.MouseMoveRelative);
        var inputState = new PlaybackInputState(RelativeMouseMovement, hasRelativeMoves);
        Exception? playbackError = null;
        IsPlaying = true;
        PlaybackDetail = "Vorbereitung läuft";
        _inputCaptureService.SetEmergencyStopEnabled(true);
        FooterMessage = "Wiedergabe vorbereitet · Esc stoppt sofort";
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            for (var count = 3; count >= 1; count--)
            {
                PlaybackDetail = $"Wiedergabe startet in {count} …";
                PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
                await Task.Delay(1000, cancellationToken);
            }
            PlaybackDetail = "Makro läuft · Esc stoppt sofort";
            FooterMessage = "Makro wird wiedergegeben · Esc stoppt sofort";
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            await MacroPlayback.RunAsync(plan, inputState.Execute, cancellationToken);
            PlaybackDetail = "Wiedergabe abgeschlossen";
            FooterMessage = plan.RepeatCount == 1
                ? "Wiedergabe abgeschlossen · 1 Durchlauf"
                : $"Wiedergabe abgeschlossen · {plan.RepeatCount} Durchläufe";
        }
        catch (OperationCanceledException)
        {
            PlaybackDetail = "Wiedergabe gestoppt";
            FooterMessage = "Wiedergabe gestoppt · aktive Tasten wurden freigegeben";
        }
        catch (Exception ex)
        {
            playbackError = ex;
            PlaybackDetail = "Wiedergabe mit Fehler beendet";
            FooterMessage = "Windows hat eine Eingabe blockiert oder die Wiedergabe ist fehlgeschlagen";
        }
        finally
        {
            try { inputState.ReleaseAll(); }
            catch (Exception ex)
            {
                FileLogger.Error("MainViewModel.StartPlayback", "Playback input release failed", ex);
            }
            _inputCaptureService.SetEmergencyStopEnabled(false);
            IsPlaying = false;
            _playbackCancellation?.Dispose();
            _playbackCancellation = null;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            PlaybackFinished?.Invoke(this, EventArgs.Empty);
        }

        if (playbackError is not null && !SuppressPlaybackErrorDialog)
            _dialogs.Show($"Die Wiedergabe wurde beendet. Windows kann Eingaben in geschützte oder mit erhöhten Rechten gestartete Fenster blockieren.\n\n{playbackError.Message}",
                "Wiedergabe fehlgeschlagen", DialogKind.Warning);
    }

    private void StopPlayback()
    {
        if (!IsPlaying) return;
        PlaybackDetail = "Stopp angefordert …";
        FooterMessage = "Not-Aus ausgelöst · Wiedergabe wird beendet";
        _playbackCancellation?.Cancel();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSetting<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName)) return;
        if (_loadingSettings) return;
        UpdateEstimate();
        RaiseCommandStates();
        ScheduleSettingsSave();
    }

    private void ScheduleSettingsSave()
    {
        if (!_settingsLoaded) return;
        lock (_settingsSaveLock)
        {
            if (_disposed) return;
            _settingsSaveTimer?.Dispose();
            _settingsSaveTimer = new Timer(SettingsSaveTimerElapsed, null, TimeSpan.FromMilliseconds(650), Timeout.InfiniteTimeSpan);
        }
    }

    private void SettingsSaveTimerElapsed(object? state)
    {
        if (_uiContext is null)
        {
            SaveSettings(showFailure: false);
            return;
        }
        _uiContext.Post(_ => SaveSettings(showFailure: true), null);
    }

    private static GlobalHotkey ParseHotkeyOrDefault(string? configured, string fallback) =>
        GlobalHotkey.TryParse(configured, out var parsed) && parsed is not null
            ? parsed
            : GlobalHotkey.TryParse(fallback, out var defaultBinding) && defaultBinding is not null
                ? defaultBinding
                : throw new InvalidOperationException("Das Standard-Tastenkürzel ist ungültig.");

    private static string FormatDuration(double milliseconds)
    {
        var totalSeconds = (long)Math.Ceiling(Math.Max(0, milliseconds) / 1000d);
        var duration = TimeSpan.FromSeconds(totalSeconds);
        if (totalSeconds >= 3600) return $"{(long)duration.TotalHours} Std. {duration.Minutes:00} Min.";
        if (totalSeconds >= 60) return $"{(long)duration.TotalMinutes} Min. {duration.Seconds:00} Sek.";
        return $"{totalSeconds} Sek.";
    }

    private bool CanSaveMacro() => !IsBusy && CurrentMacro is not null && Actions.Count > 0 && IsDirty;

    private void LoadSelectedMacro(MacroDocument selected)
    {
        CurrentMacro = null;
        MacroName = selected.Name;
        CurrentMacro = selected;
        SetSelectedLibraryItem(selected);
        CurrentIsSaved = true;
        _metadataDirty = false;
        _macroEditService.Reset(selected, selected.Actions, isSaved: true);
        IsDirty = false;
        SetSelectedActions([]);
        Actions.Clear();
        foreach (var action in selected.Actions) Actions.Add(action);
        FooterMessage = $"„{selected.Name}“ geöffnet";
        RaiseCommandStates();
        EstimateInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void EncryptExistingMacros()
    {
        if (IsBusy) return;

        int plaintextCount;
        try
        {
            plaintextCount = _storage.CountPlaintextMacros();
        }
        catch (Exception)
        {
            _dialogs.Show("Die vorhandenen Makrodateien konnten nicht geprüft werden.",
                "Verschlüsselung nicht möglich", DialogKind.Error);
            return;
        }

        if (plaintextCount == 0)
        {
            _dialogs.Show("Es wurden keine Klartext-Makros gefunden.",
                "Keine Migration erforderlich", DialogKind.Information);
            return;
        }
        if (!_dialogs.ConfirmEncryptExistingMacros(plaintextCount)) return;

        MacroMigrationResult result;
        try
        {
            result = _storage.EncryptExistingPlaintextMacros();
        }
        catch (Exception)
        {
            _dialogs.Show("Die Makrodateien konnten nicht vollständig verarbeitet werden. Nicht ersetzte Dateien bleiben unverändert.",
                "Makromigration fehlgeschlagen", DialogKind.Error);
            return;
        }
        FooterMessage = $"Makromigration abgeschlossen · {result.Succeeded} erfolgreich, {result.Failed} fehlgeschlagen";
        _dialogs.Show($"Verschlüsselt: {result.Succeeded}\nFehlgeschlagen: {result.Failed}\n\nFehlgeschlagene Dateien wurden nicht ersetzt.",
            "Makromigration abgeschlossen", result.Failed == 0 ? DialogKind.Information : DialogKind.Warning);
    }

    private void SetSelectedLibraryItem(MacroDocument? item)
    {
        SetProperty(ref _selectedLibraryItem, item, nameof(SelectedLibraryItem));
        RaiseCommandStates();
    }

    public bool CanDiscardCurrentChanges()
    {
        if (!IsDirty) return true;
        if (!_dialogs.ConfirmDiscardChanges()) return false;
        return true;
    }

    public void SetSelectedActions(IEnumerable<MacroAction> selectedActions)
    {
        var currentActions = Actions.ToHashSet();
        var selection = selectedActions.Where(currentActions.Contains).Distinct().ToArray();
        var unchanged = selection.Length == SelectedActions.Count
            && selection.Select((action, index) => ReferenceEquals(action, SelectedActions[index])).All(value => value);
        if (unchanged) return;

        SelectedActions.Clear();
        foreach (var action in selection) SelectedActions.Add(action);
        var primary = selection.LastOrDefault();
        if (!ReferenceEquals(_selectedAction, primary))
        {
            _selectedAction = primary;
            OnPropertyChanged(nameof(SelectedAction));
        }
        OnPropertyChanged(nameof(EditKeyDisplay));
        OnPropertyChanged(nameof(IsKeyActionSelected));
        OnPropertyChanged(nameof(IsMouseButtonActionSelected));
        OnPropertyChanged(nameof(IsMousePositionActionSelected));
        OnPropertyChanged(nameof(IsWheelActionSelected));
        OnPropertyChanged(nameof(CanInsertPauseAfterSelection));
        OnPropertyChanged(nameof(CanEditMousePosition));
        OnPropertyChanged(nameof(CanEditWheelDelta));
        OnPropertyChanged(nameof(CanEditMouseButton));
        OnPropertyChanged(nameof(CanCaptureActionKey));
        UpdateEditorFields();
        RaiseCommandStates();
        EditorSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CaptureAction(MacroAction action)
    {
        if (!IsRecording || CurrentMacro is null) return;
        action.AtMs = _recordClock.ElapsedMilliseconds;
        if (Actions.Count > 0 && action.AtMs < Actions[^1].AtMs) action.AtMs = Actions[^1].AtMs;
        if (Actions.Count >= MacroStorage.MaximumActionCount)
        {
            StopClockAndRecording();
            FooterMessage = $"Aufnahme automatisch beendet · Limit von {MacroStorage.MaximumActionCount:N0} Aktionen erreicht";
            return;
        }

        Actions.Add(action);
        IsDirty = true;
        FooterMessage = $"Aufnahme läuft · {Actions.Count:N0} Aktionen";
        if (Actions.Count == MacroStorage.MaximumActionCount)
        {
            StopClockAndRecording();
            FooterMessage = $"Aufnahme automatisch beendet · Limit von {MacroStorage.MaximumActionCount:N0} Aktionen erreicht";
        }
    }

    private void InputCaptureService_ActionCaptured(MacroAction action)
    {
        if (_disposed || ShouldIgnoreCapturedAction?.Invoke(action) == true) return;
        CaptureAction(action);
    }

    private void InputCaptureService_HotkeyPressed(GlobalHotkeyAction action, int[] modifierKeys)
    {
        if (_disposed) return;
        if (IsRecording && modifierKeys.Length > 0)
            RemoveCapturedShortcutModifiers(modifierKeys);

        if (action == GlobalHotkeyAction.ToggleRecording)
        {
            if (IsRecording || IsPaused) StopRecordingCommand.Execute(null);
            else if (!IsPlaying) StartRecordingCommand.Execute(null);
            return;
        }

        if (IsPlaying) StopPlaybackCommand.Execute(null);
        else if (!IsRecording && !IsPaused) PlayCommand.Execute(null);
    }

    private void InputCaptureService_EmergencyStopPressed()
    {
        if (!_disposed) StopPlaybackCommand.Execute(null);
    }

    private void InputCaptureService_StatusChanged(InputCaptureStatus status)
    {
        if (_disposed) return;
        InputHookAvailable = status.IsAvailable;
        RelativeMouseCaptureAvailable = status.RelativeMouseCaptureAvailable;
        if (!status.IsAvailable) return;

        FooterMessage = status.RelativeMouseCaptureAvailable
            ? $"Eingabe-Hooks aktiv · Aufnahme {_recordingHotkey}, Wiedergabe {_playbackHotkey}"
            : "Eingabe-Hooks aktiv · relative Mausaufnahme nicht verfügbar";
    }

    private void StartRecording()
    {
        if (IsBusy) return;
        RecordingStartRequested?.Invoke(this, EventArgs.Empty);
        if (!InputHookAvailable)
        {
            _dialogs.Show("Der globale Eingabe-Hook ist nicht verfügbar. Die Aufnahme kann nicht gestartet werden.",
                "Aufnahme nicht verfügbar", DialogKind.Warning);
            return;
        }
        if (!CanDiscardCurrentChanges() || !_dialogs.ConfirmRecordingConsent()) return;

        SetSelectedLibraryItem(null);
        CurrentMacro = new MacroDocument
        {
            Name = $"Aufnahme {DateTime.Now:dd.MM.yyyy HH-mm}",
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Actions = []
        };
        MacroName = CurrentMacro.Name;
        CurrentIsSaved = false;
        _metadataDirty = true;
        IsDirty = true;
        SetSelectedActions([]);
        Actions.Clear();
        _macroEditService.Reset(CurrentMacro, [], isSaved: false);
        _recordClock.Restart();
        RecordingState = RecordingState.Recording;
        FooterMessage = "Aufnahme läuft · Aktionen in MakroWerkstatt werden nicht erfasst";
    }

    private void ToggleRecordingPause()
    {
        if (IsRecording)
        {
            _inputCaptureService.FlushPendingMouseMovement();
            _recordClock.Stop();
            RecordingState = RecordingState.Paused;
            FooterMessage = "Aufnahme pausiert";
        }
        else if (IsPaused)
        {
            _recordClock.Start();
            RecordingState = RecordingState.Recording;
            FooterMessage = "Aufnahme fortgesetzt";
        }
    }

    private void StopRecording()
    {
        if (!IsRecording && !IsPaused) return;
        if (IsRecording) _inputCaptureService.FlushPendingMouseMovement();
        _recordClock.Stop();
        RecordingState = RecordingState.Idle;
        if (CurrentMacro is not null) _macroEditService.Reset(CurrentMacro, Actions, isSaved: false);
        FooterMessage = $"Aufnahme beendet · {Actions.Count:N0} Aktionen noch nicht gespeichert";
    }

    private void DiscardRecording()
    {
        if (!IsRecording && !IsPaused) return;
        _recordClock.Stop();
        RecordingState = RecordingState.Idle;
        SetSelectedActions([]);
        Actions.Clear();
        CurrentMacro = null;
        CurrentIsSaved = false;
        _metadataDirty = false;
        IsDirty = false;
        _macroEditService.Reset(new MacroDocument(), [], isSaved: true);
        MacroName = "Neue Aufnahme";
        SetSelectedLibraryItem(null);
        FooterMessage = "Aufnahme sofort gestoppt und verworfen";
    }

    private bool CanEditActions => !IsBusy && CurrentMacro is not null;

    private bool CanMoveSelectedActionsUp() => CanEditActions && _macroEditService.CanMove(SelectedActionIndices(), moveUp: true);

    private bool CanMoveSelectedActionsDown() => CanEditActions && _macroEditService.CanMove(SelectedActionIndices(), moveUp: false);

    private bool CanInsertPause() => CanEditActions && SelectedAction is not null
        && Actions.IndexOf(SelectedAction) >= 0 && Actions.IndexOf(SelectedAction) < Actions.Count - 1;

    private int[] SelectedActionIndices()
    {
        if (SelectedActions.Count == 0) return [];
        var selection = SelectedActions.ToHashSet();
        var indices = new List<int>(selection.Count);
        for (var index = 0; index < Actions.Count; index++)
            if (selection.Contains(Actions[index])) indices.Add(index);
        return indices.ToArray();
    }

    private void MoveSelectedActions(bool moveUp)
    {
        if (!CanEditActions || !_macroEditService.TryMove(SelectedActionIndices(), moveUp))
        {
            ShowEditFailure();
            return;
        }
        CompleteActionEdit(moveUp ? "Auswahl nach oben verschoben" : "Auswahl nach unten verschoben");
    }

    private void DuplicateSelectedActions()
    {
        if (!CanEditActions || !_macroEditService.TryDuplicate(SelectedActionIndices()))
        {
            ShowEditFailure();
            return;
        }
        CompleteActionEdit("Auswahl dupliziert");
    }

    private void RemoveSelectedActions()
    {
        if (!CanEditActions || !_macroEditService.TryDelete(SelectedActionIndices()))
        {
            if (SelectedActions.Count > 0) ShowEditFailure();
            return;
        }
        CompleteActionEdit("Auswahl entfernt");
    }

    private void InsertPause()
    {
        if (!CanInsertPause() || SelectedAction is null) return;
        if (!long.TryParse(PauseDurationMs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration)
            || duration is < 1 or > MacroEditService.MaximumEditableGapMs)
        {
            ShowInvalidEditInput("Die Pause muss zwischen 1 und 86.400.000 Millisekunden liegen.");
            return;
        }
        _macroEditService.UpdateDocument(CurrentMacro!);
        if (!_macroEditService.TryInsertPauseAfter(Actions.IndexOf(SelectedAction), duration))
        {
            ShowEditFailure();
            return;
        }
        CompleteActionEdit("Pause vor der nächsten Aktion eingefügt");
    }

    private void ApplyActionEdits()
    {
        if (!CanEditActions || SelectedAction is null) return;
        var actionIndex = Actions.IndexOf(SelectedAction);
        if (actionIndex < 0) return;

        var currentGap = SelectedAction.AtMs - (actionIndex == 0 ? 0 : Actions[actionIndex - 1].AtMs);
        if (!long.TryParse(EditDelayMs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enteredGap)
            || enteredGap < 0 || enteredGap > MacroEditService.MaximumEditableGapMs && enteredGap != currentGap)
        {
            ShowInvalidEditInput("Der Abstand muss zwischen 0 und 86.400.000 Millisekunden liegen.");
            return;
        }

        var edit = new MacroActionEdit(DelayFromPreviousMs: enteredGap == currentGap ? null : enteredGap);
        if (IsMousePositionActionSelected)
        {
            if (!TryParseEditorInt(EditX, out var x) || !TryParseEditorInt(EditY, out var y))
            {
                ShowInvalidEditInput("X und Y müssen ganze Zahlen sein.");
                return;
            }
            edit = edit with { X = x, Y = y };
        }
        if (IsMouseButtonActionSelected)
        {
            if (!MacroMouseButtons.IsAllowed(EditButton))
            {
                ShowInvalidEditInput("Wähle eine erlaubte Maustaste aus.");
                return;
            }
            edit = edit with { Button = EditButton };
        }
        if (IsWheelActionSelected)
        {
            if (!TryParseEditorInt(EditWheelDelta, out var wheelDelta))
            {
                ShowInvalidEditInput("Das Mausrad-Delta muss eine ganze Zahl sein.");
                return;
            }
            edit = edit with { WheelDelta = wheelDelta };
        }

        _macroEditService.UpdateDocument(CurrentMacro!);
        if (!_macroEditService.TryEditAction(actionIndex, edit))
        {
            ShowEditFailure();
            return;
        }
        CompleteActionEdit("Aktion geändert");
    }

    public bool TrySetSelectedKey(int keyCode)
    {
        if (!CanEditActions || SelectedAction is null || keyCode is < 0 or > 255) return false;
        _macroEditService.UpdateDocument(CurrentMacro!);
        if (!_macroEditService.TryEditAction(Actions.IndexOf(SelectedAction), new MacroActionEdit(KeyCode: keyCode)))
        {
            ShowEditFailure();
            return false;
        }
        CompleteActionEdit("Taste geändert");
        return true;
    }

    private static bool TryParseEditorInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private void UndoActionEdit()
    {
        if (!CanEditActions || !_macroEditService.CanUndo) return;
        _macroEditService.Undo();
        CompleteActionEdit("Bearbeitung rückgängig gemacht");
    }

    private void RedoActionEdit()
    {
        if (!CanEditActions || !_macroEditService.CanRedo) return;
        _macroEditService.Redo();
        CompleteActionEdit("Bearbeitung wiederholt");
    }

    private void CompleteActionEdit(string message)
    {
        var editedActions = _macroEditService.Actions;
        var selected = _macroEditService.LastSelectionIndices.Where(index => index >= 0 && index < editedActions.Count).ToArray();
        SetSelectedActions([]);
        Actions.Clear();
        foreach (var action in editedActions) Actions.Add(action);
        if (CurrentMacro is not null) CurrentMacro.Actions = Actions.Select(CloneAction).ToList();
        SetSelectedActions(selected.Select(index => Actions[index]));
        IsDirty = _metadataDirty || !_macroEditService.IsAtSavedState;
        OnPropertyChanged(nameof(CanUndoActionEdit));
        OnPropertyChanged(nameof(CanRedoActionEdit));
        FooterMessage = $"{message} · Änderungen noch nicht gespeichert";
        RaiseCommandStates();
        EstimateInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateEditorFields()
    {
        if (SelectedAction is not { } action)
        {
            EditDelayMs = "0";
            EditX = "0";
            EditY = "0";
            EditWheelDelta = "0";
            EditButton = MacroMouseButtons.Left;
            return;
        }
        var index = Actions.IndexOf(action);
        var previousTime = index <= 0 ? 0 : Actions[index - 1].AtMs;
        EditDelayMs = Math.Max(0, action.AtMs - previousTime).ToString(CultureInfo.InvariantCulture);
        EditX = action.X.ToString(CultureInfo.InvariantCulture);
        EditY = action.Y.ToString(CultureInfo.InvariantCulture);
        EditWheelDelta = action.WheelDelta.ToString(CultureInfo.InvariantCulture);
        if (MacroMouseButtons.IsAllowed(action.Button)) EditButton = action.Button;
    }

    private void ShowEditFailure() => ShowInvalidEditInput("Die Bearbeitung ist ungültig oder würde eine gehaltene Taste verändern.");

    private void ShowInvalidEditInput(string message) => _dialogs.Show(message, "Aktion nicht geändert", DialogKind.Warning);

    private static MacroAction CloneAction(MacroAction action) => new()
    {
        AtMs = action.AtMs,
        Type = action.Type,
        X = action.X,
        Y = action.Y,
        KeyCode = action.KeyCode,
        ScanCode = action.ScanCode,
        RawFlags = action.RawFlags,
        WheelDelta = action.WheelDelta,
        Button = action.Button
    };

    private void StopClockAndRecording()
    {
        _recordClock.Stop();
        RecordingState = RecordingState.Idle;
        if (CurrentMacro is not null) _macroEditService.Reset(CurrentMacro, Actions, isSaved: false);
    }

    private void RaiseCommandStates()
    {
        ((RelayCommand)RemoveActionCommand).RaiseCanExecuteChanged();
        ((RelayCommand)MoveActionUpCommand).RaiseCanExecuteChanged();
        ((RelayCommand)MoveActionDownCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DuplicateActionsCommand).RaiseCanExecuteChanged();
        ((RelayCommand)InsertPauseCommand).RaiseCanExecuteChanged();
        ((RelayCommand)UndoActionEditCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RedoActionEditCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ApplyActionEditsCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanUndoActionEdit));
        OnPropertyChanged(nameof(CanRedoActionEdit));
        ((RelayCommand)ReloadLibraryCommand).RaiseCanExecuteChanged();
        ((RelayCommand)SaveMacroCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DeleteMacroCommand).RaiseCanExecuteChanged();
        ((RelayCommand)EncryptExistingMacrosCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ExportAllMacrosCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ExportSelectedMacroCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ImportMacrosCommand).RaiseCanExecuteChanged();
        ((RelayCommand)PlayCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StopPlaybackCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StartRecordingCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ToggleRecordingPauseCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StopRecordingCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DiscardRecordingCommand).RaiseCanExecuteChanged();
    }
}
