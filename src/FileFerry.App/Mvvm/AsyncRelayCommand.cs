using System.Windows.Input;

namespace FileFerry.App.Mvvm;

/// <summary>
/// Async command for VMs: one concurrent task (repeat taps are ignored),
/// its own CTS per invocation plus <see cref="Cancel"/> for "close the screen".
/// An unexpected exception from the delegate is not swallowed (exceptions are bugs only).
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, CancellationToken, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Creates the command without a parameter (the screen's primary action).
    /// </summary>
    public AsyncRelayCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null)
        : this((_, ct) => execute(ct), canExecute is null ? null : _ => canExecute())
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    /// <summary>
    /// Creates the command with a parameter (actions on a list/menu item).
    /// </summary>
    public AsyncRelayCommand(Func<object?, CancellationToken, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>
    /// Command is busy — repeat invocation is blocked (loading state).
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke(parameter) ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Raise();
        try
        {
            await _execute(parameter, _cts.Token).ConfigureAwait(false);
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
            Raise();
        }
    }

    /// <summary>
    /// Cancel the current invocation (the user closed the screen).
    /// </summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>
    /// Re-evaluate <see cref="CanExecute"/> (the VM changed the availability condition, e.g. IsBusy).
    /// </summary>
    public void RaiseCanExecuteChanged() => Raise();

    /// <summary>
    /// Availability event — always on the UI thread (the native Button reacts in setEnabled).
    /// </summary>
    private void Raise() => BindingMainThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}
