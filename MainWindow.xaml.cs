using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MakroWerkstatt;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IInputCaptureService _inputCaptureService;
    public ObservableCollection<MacroAction> Actions => _viewModel.Actions;

    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private string _playbackDetail => _viewModel.PlaybackDetail;
    private string _footerMessage { get => _viewModel.FooterMessage; set => _viewModel.FooterMessage = value; }
    private bool _isRecording => _viewModel.IsRecording;
    private bool _isPaused => _viewModel.IsPaused;
    private bool _isPlaying => _viewModel.IsPlaying;
    private bool _uiReady;
    private bool _closeWhenPlaybackEnds;
    private bool _allowClose;
    private Button? _hotkeyCaptureButton;

    public MainWindow()
    {
        InitializeComponent();
        _inputCaptureService = new GlobalInputCaptureService(new WpfUiDispatcher(Dispatcher));
        _viewModel = new MainViewModel(new MacroStorage(), new WpfDialogService(this), _inputCaptureService)
        {
            ShouldIgnoreCapturedAction = ShouldIgnoreOwnWindowAction
        };
        DataContext = _viewModel;
        _uiTimer.Tick += (_, _) => UpdateActivityDisplay();
        _uiTimer.Start();
        Actions.CollectionChanged += (_, _) => RefreshActionList();
        _viewModel.RecordingStartRequested += (_, _) => CancelHotkeyCapture();
        _viewModel.PlaybackStartRequested += (_, _) => CancelHotkeyCapture();
        _viewModel.PlaybackStateChanged += (_, _) =>
        {
            if (_uiReady) UpdateUI();
        };
        _viewModel.PlaybackFinished += (_, _) =>
        {
            if (_closeWhenPlaybackEnds)
            {
                _allowClose = true;
                Close();
            }
            else if (_uiReady) UpdateUI();
        };
        _viewModel.EstimateInvalidated += (_, _) =>
        {
            if (!_uiReady) return;
            UpdateUI();
            UpdateEstimate();
        };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (_uiReady) UpdateUI();
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel.LoadSettings();
        UpdateHotkeyLabels();

        LoadLibrary(showWarnings: true);
        try
        {
            _inputCaptureService.SetRawInputTarget(new WindowInteropHelper(this).Handle);
            _inputCaptureService.Start();
        }
        catch (Exception ex)
        {
            FileLogger.Error("MainWindow.Window_Loaded", "Input hook initialization failed", ex);
            _viewModel.InputHookAvailable = false;
            _footerMessage = "Tastatur-/Maus-Hook nicht verfügbar";
            MessageBox.Show(this,
                "Der globale Tastatur- und Maushook konnte nicht gestartet werden. Aufnehmen und Abspielen sind nicht verfügbar.",
                "MakroWerkstatt · Eingabezugriff", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _uiReady = true;
        UpdateUI();
        UpdateEstimate();
    }

    private void LoadLibrary(bool showWarnings)
    {
        _viewModel.LoadLibrary(showWarnings);
    }

    private bool ShouldIgnoreOwnWindowAction(MacroAction action)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return false;
        if (action.Type is MacroActionType.KeyDown or MacroActionType.KeyUp)
            return GetForegroundWindow() == handle;

        if (action.Type == MacroActionType.MouseMoveRelative)
            return GetForegroundWindow() == handle;

        if (!GetWindowRect(handle, out var rect)) return false;
        return action.X >= rect.Left && action.X < rect.Right && action.Y >= rect.Top && action.Y < rect.Bottom;
    }

    private void HotkeyCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _isRecording || _isPaused || _isPlaying) return;
        CancelHotkeyCapture();
        _hotkeyCaptureButton = button;
        _inputCaptureService.SetHotkeysEnabled(false);
        button.SetCurrentValue(ContentControl.ContentProperty, "Jetzt F1–F12 drücken · Esc bricht ab");
        button.Focus();
        _footerMessage = "Tastenkürzel auswählen · Esc bricht die Auswahl ab";
        UpdateActivityDisplay();
    }

    private void HotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_hotkeyCaptureButton is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            _footerMessage = "Tastenkürzel-Auswahl abgebrochen";
            UpdateActivityDisplay();
            return;
        }
        if ((int)key < (int)Key.F1 || (int)key > (int)Key.F12)
        {
            _footerMessage = "Bitte F1 bis F12 drücken, optional mit Ctrl, Alt oder Shift";
            UpdateActivityDisplay();
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        var keyCode = KeyInterop.VirtualKeyFromKey(key);
        var binding = new GlobalHotkey(keyCode, modifiers);
        var isRecordingBinding = _hotkeyCaptureButton == RecordHotkeyButton;
        if (!_viewModel.TrySetHotkey(isRecordingBinding, binding))
        {
            UpdateActivityDisplay();
            return;
        }

        _inputCaptureService.SetHotkeysEnabled(true);
        _hotkeyCaptureButton = null;
        UpdateHotkeyLabels();
        UpdateActivityDisplay();
    }

    private void CancelHotkeyCapture()
    {
        if (_hotkeyCaptureButton is null) return;
        _hotkeyCaptureButton = null;
        _inputCaptureService.SetHotkeysEnabled(true);
        UpdateHotkeyLabels();
    }

    private void UpdateHotkeyLabels()
    {
        if (RecordHotkeyButton is not null)
            RecordHotkeyButton.SetCurrentValue(ContentControl.ContentProperty, _viewModel.RecordingHotkeyText);
        if (PlaybackHotkeyButton is not null)
            PlaybackHotkeyButton.SetCurrentValue(ContentControl.ContentProperty, _viewModel.PlaybackHotkeyText);
    }

    private void UpdateEstimate() => _viewModel.UpdateEstimate();

    private void StopPlayback()
    {
        _viewModel.StopPlaybackCommand.Execute(null);
        UpdateUI();
    }

    private void RefreshActionList()
    {
        EmptyActionsPanel.Visibility = Actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Actions.Count > 0 && (Actions.Count < 3 || Actions.Count % 10 == 0)) ActionListView.ScrollIntoView(Actions[^1]);
    }

    private void UpdateUI()
    {
        if (!_uiReady) return;
        var busy = _isRecording || _isPaused || _isPlaying;
        NewRecordingButton.IsEnabled = !busy;
        MacroListBox.IsEnabled = !_isRecording && !_isPaused && !_isPlaying;
        StopRecordingButton.IsEnabled = _isRecording || _isPaused;
        RecordHotkeyButton.IsEnabled = !busy;
        PlaybackHotkeyButton.IsEnabled = !busy;

        PauseRecordingButton.Visibility = _isRecording || _isPaused ? Visibility.Visible : Visibility.Collapsed;
        PauseRecordingButton.Content = _isPaused ? "▶  Fortsetzen" : "Ⅱ  Pause";
        DiscardRecordingButton.Visibility = _isRecording || _isPaused ? Visibility.Visible : Visibility.Collapsed;
        StopPlaybackButton.Visibility = _isPlaying ? Visibility.Visible : Visibility.Collapsed;
        ActivityTimerText.Visibility = _isRecording || _isPaused ? Visibility.Visible : Visibility.Collapsed;

        if (_isRecording || _isPaused)
        {
            StatusBadgeBorder.Background = Brush("#7F3038");
            StatusText.Text = _isPaused ? "PAUSIERT" : "AUFNAHME";
            StatusText.Foreground = Brushes.White;
            ActivityBanner.Background = Brush(_isPaused ? "#FFF8EB" : "#FFF0F0");
            ActivityBanner.BorderBrush = Brush(_isPaused ? "#EBD7AD" : "#F3CDCF");
            ActivityDot.Fill = _isPaused ? Brush("#D09620") : (Brush)FindResource("Red");
            ActivityStatusText.Text = _isPaused ? "AUFNAHME PAUSIERT" : "AUFNAHME LÄUFT";
            ActivityHintText.Text = _isPaused
                ? "  ·  Aufnahme pausiert; drücke Fortsetzen oder stoppe und lösche die Sequenz."
                : "  ·  Aktionen in anderen Fenstern werden erfasst. Vertrauliche Texte können enthalten sein.";
            _footerMessage = _isPaused ? "Aufnahme pausiert" : $"Aufnahme läuft · {Actions.Count:N0} Aktionen";
        }
        else if (_isPlaying)
        {
            StatusBadgeBorder.Background = Brush("#1E795E");
            StatusText.Text = "WIEDERGABE";
            StatusText.Foreground = Brushes.White;
            ActivityBanner.Background = Brush("#ECF7F3");
            ActivityBanner.BorderBrush = Brush("#C8E9DB");
            ActivityDot.Fill = (Brush)FindResource("Green");
            ActivityStatusText.Text = _playbackDetail;
            ActivityHintText.Text = $"  ·  {_viewModel.PlaybackHotkey}, Esc oder die Stopp-Schaltfläche beendet die Wiedergabe.";
        }
        else
        {
            StatusBadgeBorder.Background = Brush("#273951");
            StatusText.Text = "BEREIT";
            StatusText.Foreground = Brush("#DAE4EF");
            ActivityBanner.Background = Brush("#EAF1FA");
            ActivityBanner.BorderBrush = Brush("#D7E3F2");
            ActivityDot.Fill = Brush("#8EA2BB");
            ActivityStatusText.Text = _viewModel.CurrentMacro is null ? "Bereit für eine neue Sequenz" : $"{_viewModel.CurrentMacro.Name} · {Actions.Count:N0} Aktionen";
            ActivityHintText.Text = $"  ·  Aufnahme: {_viewModel.RecordingHotkey} · Wiedergabe starten/stoppen: {_viewModel.PlaybackHotkey}.";
        }

        DurationTextBox.IsEnabled = StretchCheckBox.IsChecked == true;
        DurationUnitComboBox.IsEnabled = StretchCheckBox.IsChecked == true;
        RandomMinTextBox.IsEnabled = RandomizerCheckBox.IsChecked == true;
        RandomMaxTextBox.IsEnabled = RandomizerCheckBox.IsChecked == true;
        RelativeMouseCheckBox.IsEnabled = !busy;
        RepeatCountTextBox.IsEnabled = !busy;
        FooterStatusText.Text = _footerMessage;
        UpdateActivityDisplay();
        UpdateEstimate();
    }

    private void UpdateActivityDisplay()
    {
        if (!_uiReady) return;
        var elapsed = _viewModel.RecordingElapsed;
        ActivityTimerText.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        FooterStatusText.Text = _footerMessage;
    }

    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isPlaying && !_allowClose)
        {
            e.Cancel = true;
            _closeWhenPlaybackEnds = true;
            _viewModel.SuppressPlaybackErrorDialog = true;
            StopPlayback();
            return;
        }

        if (!_allowClose && !_viewModel.TryClose())
        {
            e.Cancel = true;
            UpdateUI();
            return;
        }

        _viewModel.SaveSettings(showFailure: false);
        _uiTimer.Stop();
        _inputCaptureService.Stop();
        _viewModel.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
}
