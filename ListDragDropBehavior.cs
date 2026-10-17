using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MakroWerkstatt;

/// <summary>Reorders selected action rows by dragging them within their ListView.</summary>
public static class ListDragDropBehavior
{
    private const string DragFormat = "MakroWerkstatt.ActionListDrag";
    private static readonly ConditionalWeakTable<ListView, DragState> States = new();

    public static readonly DependencyProperty IsDragEnabledProperty = DependencyProperty.RegisterAttached(
        "IsDragEnabled", typeof(bool), typeof(ListDragDropBehavior),
        new FrameworkPropertyMetadata(false, OnIsDragEnabledChanged));

    public static readonly DependencyProperty MoveCommandProperty = DependencyProperty.RegisterAttached(
        "MoveCommand", typeof(ICommand), typeof(ListDragDropBehavior));

    public static bool GetIsDragEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsDragEnabledProperty);

    public static void SetIsDragEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsDragEnabledProperty, value);

    public static ICommand? GetMoveCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(MoveCommandProperty);

    public static void SetMoveCommand(DependencyObject element, ICommand? value) =>
        element.SetValue(MoveCommandProperty, value);

    private static void OnIsDragEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ListView list) return;
        if ((bool)args.OldValue)
        {
            list.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            list.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
            list.PreviewMouseMove -= OnPreviewMouseMove;
            list.DragEnter -= OnDragOver;
            list.DragOver -= OnDragOver;
            list.Drop -= OnDrop;
            list.SetCurrentValue(UIElement.AllowDropProperty, false);
            if (States.TryGetValue(list, out var state)) state.Clear();
        }

        if ((bool)args.NewValue)
        {
            _ = States.GetValue(list, _ => new DragState());
            list.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            list.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
            list.PreviewMouseMove += OnPreviewMouseMove;
            list.DragEnter += OnDragOver;
            list.DragOver += OnDragOver;
            list.Drop += OnDrop;
            list.SetCurrentValue(UIElement.AllowDropProperty, true);
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (sender is not ListView list || !GetIsDragEnabled(list)
            || !States.TryGetValue(list, out var state)) return;
        var container = ItemsControl.ContainerFromElement(list, args.OriginalSource as DependencyObject) as ListViewItem;
        state.Clear();
        if (container is null) return;
        var item = list.ItemContainerGenerator.ItemFromContainer(container);
        state.StartPoint = args.GetPosition(list);
        state.InitialSelection = list.SelectedItems.Cast<object>().ToArray();
        state.SourceWasSelected = state.InitialSelection.Contains(item);
    }

    private static void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (sender is ListView list && States.TryGetValue(list, out var state))
            state.Clear();
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs args)
    {
        if (sender is not ListView list || !GetIsDragEnabled(list)
            || !States.TryGetValue(list, out var state) || state.StartPoint is not { } startPoint
            || args.LeftButton != MouseButtonState.Pressed)
            return;

        var currentPoint = args.GetPosition(list);
        if (Math.Abs(currentPoint.X - startPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPoint.Y - startPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        if (state.SourceWasSelected && state.InitialSelection.Length > 1)
        {
            list.SelectedItems.Clear();
            foreach (var item in state.InitialSelection)
                if (list.Items.Contains(item)) list.SelectedItems.Add(item);
        }
        state.Clear();
        if (list.SelectedItems.Count == 0) return;

        var data = new DataObject(DragFormat, new DragToken(list));
        args.Handled = true;
        DragDrop.DoDragDrop(list, data, DragDropEffects.Move);
    }

    private static void OnDragOver(object sender, DragEventArgs args)
    {
        if (sender is not ListView list || !GetIsDragEnabled(list) || !IsLocalDrag(args, list))
        {
            args.Effects = DragDropEffects.None;
            args.Handled = true;
            return;
        }

        var targetIndex = GetTargetIndex(list, args);
        var command = GetMoveCommand(list);
        args.Effects = command?.CanExecute(targetIndex) == true ? DragDropEffects.Move : DragDropEffects.None;
        args.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs args)
    {
        if (sender is not ListView list || !GetIsDragEnabled(list) || !IsLocalDrag(args, list))
        {
            args.Effects = DragDropEffects.None;
            args.Handled = true;
            return;
        }

        var targetIndex = GetTargetIndex(list, args);
        var command = GetMoveCommand(list);
        if (command?.CanExecute(targetIndex) == true)
        {
            command.Execute(targetIndex);
            args.Effects = DragDropEffects.Move;
        }
        else
        {
            args.Effects = DragDropEffects.None;
        }
        args.Handled = true;
    }

    private static bool IsLocalDrag(DragEventArgs args, ListView list) =>
        args.Data.GetDataPresent(DragFormat)
        && args.Data.GetData(DragFormat) is DragToken token
        && ReferenceEquals(token.Source, list);

    private static int GetTargetIndex(ListView list, DragEventArgs args)
    {
        var container = ItemsControl.ContainerFromElement(list, args.OriginalSource as DependencyObject) as ListViewItem;
        if (container is null) return list.Items.Count;
        var index = list.ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0) return list.Items.Count;
        return args.GetPosition(container).Y >= container.ActualHeight / 2d ? index + 1 : index;
    }

    private sealed record DragToken(ListView Source);

    private sealed class DragState
    {
        public Point? StartPoint { get; set; }
        public object[] InitialSelection { get; set; } = [];
        public bool SourceWasSelected { get; set; }

        public void Clear()
        {
            StartPoint = null;
            InitialSelection = [];
            SourceWasSelected = false;
        }
    }
}
