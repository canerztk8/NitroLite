using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using NitroLite.Core;

namespace NitroLite.Desktop.Mvvm;

/// <summary>
/// The smallest thing that makes a view model bindable: change notification and a setter that only fires when the
/// value actually changed.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled from a toolkit package. The whole surface is two members, and every extra package
/// is one more version to keep in step with Avalonia for a Linux build that has to restore on a machine with no
/// Windows SDK.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Assigns and notifies. Returns true when the value changed, so a caller can chain a follow-up.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }
}

/// <summary>
/// A command that runs an async handler and refuses to run twice at once.
/// </summary>
/// <remarks>
/// The re-entrancy guard is the point: every command here writes to the hardware, and a double click that started two
/// writes would interleave with the read-back that verifies the first one. The handler is awaited, so the button is
/// disabled for exactly as long as the write takes.
/// </remarks>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // An exception out of an async void handler is raised on the thread pool, where nothing catches it and
            // the process dies. A command that failed is a status line, not a crash.
            AppLog.Error("Command failed", ex.ToString());
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A command that runs a synchronous handler, for the ones that only touch the settings file.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
