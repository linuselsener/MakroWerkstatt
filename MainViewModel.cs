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
    private bool _currentIsSaved;
    private bool _inputHookAvailable;
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
    private string _repeatCount = "1";
    private string _randomMinMs = "1";
    private string _randomMaxMs = "80";
    private bool _loadingSettings;
    private bool _settingsLoaded;
    private bool _disposed;
    private GlobalHotkey _recordingHotkey = new(0x78, HotkeyModifiers.None);
    private GlobalHotkey _playbackHotkey = new(0x79, HotkeyModifiers.None);

    public ObservableCollection<MacroAction> Actions { get; } = [];
    public ObservableCollection<MacroDocument> Library { get; } = [];
    public ICommand RemoveActionCommand { get; }
    public ICommand ReloadLibraryCommand { get; }
    public ICommand SaveMacroCommand { get; }
    public ICommand DeleteMacroCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand StopPlaybackCommand { get; }
    public ICommand StartRecordingCommand { get; }
    public ICommand ToggleRecordingPauseCommand { get; }
    public ICommand StopRecordingCommand { get; }
    public ICommand DiscardRecordingCommand { get; }

    public event EventHandler? EstimateInvalidated;
    public event EventHandler? FlushPendingMouseMovementRequested;
    public event EventHandler? RecordingStartRequested;
    public event EventHandler? PlaybackStartRequested;
    public event EventHandler? PlaybackStateChanged;
    public event EventHandler? PlaybackFinished;
    public event Action<bool>? EmergencyStopEnabledChanged;
    public event EventHandler? HotkeysChanged;

    public MacroAction? SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (SetProperty(ref _selectedAction, value))
                RaiseCommandStates();
        }
    }

    public MacroDocument? CurrentMacro
    {
        get => _currentMacro;
        set
        {
            if (SetProperty(ref _currentMacro, value))
                RaiseCommandStates();
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
                return;
            }
            if (!CanDiscardCurrentChanges())
            {
                OnPropertyChanged();
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

    public string MacroName
    {
        get => _macroName;
        set
        {
            if (!SetProperty(ref _macroName, value)) return;
            if (CurrentMacro is not null)
            {
                CurrentMacro.Name = value;
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

    public MainViewModel(MacroStorage storage, IDialogService dialogs)
    {
        _storage = storage;
        _dialogs = dialogs;
        RemoveActionCommand = new RelayCommand(RemoveSelectedAction, () => SelectedAction is not null && !IsBusy);
        ReloadLibraryCommand = new RelayCommand(ReloadLibrary, () => !IsBusy);
        SaveMacroCommand = new RelayCommand(() => SaveCurrentMacro(showSuccess: true), CanSaveMacro);
        DeleteMacroCommand = new RelayCommand(DeleteCurrentMacro, () => !IsBusy && CurrentIsSaved && CurrentMacro is not null);
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
        Library.CollectionChanged += (_, _) => OnPropertyChanged(nameof(MacroCountText));
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
        CurrentMacro.Actions = Actions.ToList();
        CurrentMacro.UpdatedUtc = DateTimeOffset.UtcNow;
        CurrentMacro.FormatVersion = MacroStorage.CurrentFormatVersion;
        try
        {
            _storage.Save(CurrentMacro);
        }
        catch (Exception ex)
        {
            _dialogs.Show($"Das Makro konnte nicht lokal gespeichert werden.\n\n{ex.Message}",
                "Speichern fehlgeschlagen", DialogKind.Error);
            return false;
        }

        CurrentIsSaved = true;
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
        IsDirty = false;
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
        OnPropertyChanged(nameof(RepeatCount));
        OnPropertyChanged(nameof(RandomMinMs));
        OnPropertyChanged(nameof(RandomMaxMs));
        OnPropertyChanged(nameof(RecordingHotkey));
        OnPropertyChanged(nameof(PlaybackHotkey));
        OnPropertyChanged(nameof(RecordingHotkeyText));
        OnPropertyChanged(nameof(PlaybackHotkeyText));
        UpdateEstimate();
    }

    public bool TrySetHotkey(bool forRecording, GlobalHotkey binding)
    {
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
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
        ScheduleSettingsSave();
        return true;
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
        EmergencyStopEnabledChanged?.Invoke(true);
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
            EmergencyStopEnabledChanged?.Invoke(false);
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
        IsDirty = false;
        Actions.Clear();
        foreach (var action in selected.Actions) Actions.Add(action);
        FooterMessage = $"„{selected.Name}“ geöffnet";
        RaiseCommandStates();
        EstimateInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void SetSelectedLibraryItem(MacroDocument? item)
    {
        SetProperty(ref _selectedLibraryItem, item, nameof(SelectedLibraryItem));
    }

    public bool CanDiscardCurrentChanges()
    {
        if (!IsDirty) return true;
        if (!_dialogs.ConfirmDiscardChanges()) return false;
        IsDirty = false;
        return true;
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
        IsDirty = true;
        Actions.Clear();
        _recordClock.Restart();
        RecordingState = RecordingState.Recording;
        FooterMessage = "Aufnahme läuft · Aktionen in MakroWerkstatt werden nicht erfasst";
    }

    private void ToggleRecordingPause()
    {
        if (IsRecording)
        {
            FlushPendingMouseMovementRequested?.Invoke(this, EventArgs.Empty);
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
        if (IsRecording) FlushPendingMouseMovementRequested?.Invoke(this, EventArgs.Empty);
        _recordClock.Stop();
        RecordingState = RecordingState.Idle;
        FooterMessage = $"Aufnahme beendet · {Actions.Count:N0} Aktionen noch nicht gespeichert";
    }

    private void DiscardRecording()
    {
        if (!IsRecording && !IsPaused) return;
        _recordClock.Stop();
        RecordingState = RecordingState.Idle;
        Actions.Clear();
        CurrentMacro = null;
        CurrentIsSaved = false;
        IsDirty = false;
        MacroName = "Neue Aufnahme";
        SetSelectedLibraryItem(null);
        FooterMessage = "Aufnahme sofort gestoppt und verworfen";
    }

    private void RemoveSelectedAction()
    {
        if (IsBusy || SelectedAction is not { } action) return;
        Actions.Remove(action);
        if (CurrentMacro is not null) CurrentMacro.Actions = Actions.ToList();
        SelectedAction = null;
        IsDirty = true;
        FooterMessage = "Aktion entfernt · Änderungen noch nicht gespeichert";
    }

    private void StopClockAndRecording()
    {
        _recordClock.Stop();
        RecordingState = RecordingState.Idle;
    }

    private void RaiseCommandStates()
    {
        ((RelayCommand)RemoveActionCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ReloadLibraryCommand).RaiseCanExecuteChanged();
        ((RelayCommand)SaveMacroCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DeleteMacroCommand).RaiseCanExecuteChanged();
        ((RelayCommand)PlayCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StopPlaybackCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StartRecordingCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ToggleRecordingPauseCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StopRecordingCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DiscardRecordingCommand).RaiseCanExecuteChanged();
    }
}
