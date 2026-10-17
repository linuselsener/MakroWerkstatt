using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace MakroWerkstatt;

/// <summary>Captures a key on the focused button and delegates its meaning to a view-model command.</summary>
public static class HotkeyCaptureBehavior
{
    private static readonly ConditionalWeakTable<ButtonBase, CaptureState> States = new();

    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(KeyCaptureTarget), typeof(HotkeyCaptureBehavior),
        new PropertyMetadata(KeyCaptureTarget.None, OnTargetChanged));

    public static readonly DependencyProperty BeginCommandProperty = DependencyProperty.RegisterAttached(
        "BeginCommand", typeof(ICommand), typeof(HotkeyCaptureBehavior));

    public static readonly DependencyProperty CaptureCommandProperty = DependencyProperty.RegisterAttached(
        "CaptureCommand", typeof(ICommand), typeof(HotkeyCaptureBehavior));

    public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.RegisterAttached(
        "CancelCommand", typeof(ICommand), typeof(HotkeyCaptureBehavior));

    public static readonly DependencyProperty IsCapturingProperty = DependencyProperty.RegisterAttached(
        "IsCapturing", typeof(bool), typeof(HotkeyCaptureBehavior),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsCapturingChanged));

    public static readonly DependencyProperty PromptProperty = DependencyProperty.RegisterAttached(
        "Prompt", typeof(string), typeof(HotkeyCaptureBehavior), new PropertyMetadata("Taste drücken · Esc bricht ab"));

    public static KeyCaptureTarget GetTarget(DependencyObject element) =>
        (KeyCaptureTarget)element.GetValue(TargetProperty);

    public static void SetTarget(DependencyObject element, KeyCaptureTarget value) =>
        element.SetValue(TargetProperty, value);

    public static ICommand? GetBeginCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(BeginCommandProperty);

    public static void SetBeginCommand(DependencyObject element, ICommand? value) =>
        element.SetValue(BeginCommandProperty, value);

    public static ICommand? GetCaptureCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(CaptureCommandProperty);

    public static void SetCaptureCommand(DependencyObject element, ICommand? value) =>
        element.SetValue(CaptureCommandProperty, value);

    public static ICommand? GetCancelCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(CancelCommandProperty);

    public static void SetCancelCommand(DependencyObject element, ICommand? value) =>
        element.SetValue(CancelCommandProperty, value);

    public static bool GetIsCapturing(DependencyObject element) =>
        (bool)element.GetValue(IsCapturingProperty);

    public static void SetIsCapturing(DependencyObject element, bool value) =>
        element.SetValue(IsCapturingProperty, value);

    public static string GetPrompt(DependencyObject element) =>
        (string)element.GetValue(PromptProperty);

    public static void SetPrompt(DependencyObject element, string value) =>
        element.SetValue(PromptProperty, value);

    private static void OnTargetChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ButtonBase button) return;
        if (args.OldValue is KeyCaptureTarget oldTarget && oldTarget != KeyCaptureTarget.None)
        {
            CancelIfActive(button);
            button.RemoveHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnClick));
            button.Unloaded -= OnUnloaded;
            States.Remove(button);
        }

        if (args.NewValue is KeyCaptureTarget newTarget && newTarget != KeyCaptureTarget.None)
        {
            _ = States.GetValue(button, _ => new CaptureState());
            button.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnClick));
            button.Unloaded += OnUnloaded;
        }
    }

    private static void OnIsCapturingChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ButtonBase button || !States.TryGetValue(button, out var state)) return;
        if ((bool)args.NewValue)
        {
            button.SetCurrentValue(ContentControl.ContentProperty, GetPrompt(button));
            AttachWindowHandler(button, state);
        }
        else
        {
            DetachWindowHandler(state);
            button.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
        }
    }

    private static void OnClick(object sender, RoutedEventArgs args)
    {
        if (sender is not ButtonBase button) return;
        var target = GetTarget(button);
        var command = GetBeginCommand(button);
        if (target != KeyCaptureTarget.None && command?.CanExecute(target) == true)
            command.Execute(target);
    }

    private static void OnWindowPreviewKeyDown(ButtonBase button, KeyEventArgs args)
    {
        if (!GetIsCapturing(button)) return;
        args.Handled = true;
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key == Key.Escape)
        {
            CancelIfActive(button);
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        var request = new KeyCaptureRequest(GetTarget(button), KeyInterop.VirtualKeyFromKey(key), modifiers);
        var command = GetCaptureCommand(button);
        if (command?.CanExecute(request) == true) command.Execute(request);
    }

    private static void AttachWindowHandler(ButtonBase button, CaptureState state)
    {
        if (state.Window is not null) return;
        var window = Window.GetWindow(button);
        if (window is null) return;
        state.Window = window;
        state.PreviewKeyHandler = (_, args) => OnWindowPreviewKeyDown(button, args);
        window.AddHandler(Keyboard.PreviewKeyDownEvent, state.PreviewKeyHandler, handledEventsToo: true);
    }

    private static void DetachWindowHandler(CaptureState state)
    {
        if (state.Window is null || state.PreviewKeyHandler is null) return;
        state.Window.RemoveHandler(Keyboard.PreviewKeyDownEvent, state.PreviewKeyHandler);
        state.Window = null;
        state.PreviewKeyHandler = null;
    }

    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is ButtonBase button) CancelIfActive(button);
    }

    private static void CancelIfActive(ButtonBase button)
    {
        if (!GetIsCapturing(button)) return;
        var target = GetTarget(button);
        var command = GetCancelCommand(button);
        if (command?.CanExecute(target) == true) command.Execute(target);
        if (GetIsCapturing(button)) button.SetCurrentValue(IsCapturingProperty, false);
    }

    private sealed class CaptureState
    {
        public Window? Window { get; set; }
        public KeyEventHandler? PreviewKeyHandler { get; set; }
    }
}
