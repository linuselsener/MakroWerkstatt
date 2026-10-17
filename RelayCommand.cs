using System.Windows.Input;

namespace MakroWerkstatt;

public sealed class RelayCommand : ICommand
{
    private readonly Action? _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<object?>? _executeWithParameter;
    private readonly Func<object?, bool>? _canExecuteWithParameter;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool> canExecute)
    {
        _executeWithParameter = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecuteWithParameter = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecuteWithParameter?.Invoke(parameter) ?? _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (_executeWithParameter is not null) _executeWithParameter(parameter);
        else _execute!();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
