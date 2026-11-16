using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FileFerry.App.Mvvm;

/// <summary>
/// INotifyPropertyChanged base for VMs: the UI layer pulls in no external MVVM packages —
/// only the minimum actually used (Set + PropertyChanged).
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Assign and notify only if the value actually changed.
    /// </summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        BindingMainThread.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
        return true;
    }

    /// <summary>
    /// Notify about a change of a computed property (also on the UI thread).
    /// </summary>
    protected void Raise([CallerMemberName] string? propertyName = null) =>
        BindingMainThread.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
}
