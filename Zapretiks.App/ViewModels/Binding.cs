using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Zapretiks.App.ViewModels;

internal abstract class ViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(property);
        return true;
    }

    protected void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

internal sealed class UiCommand(Func<object?, Task> execute, Action<Exception> onError,
    Predicate<object?>? canExecute = null) : ICommand
{
    private bool running;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute(parameter);
        }
        catch (Exception error)
        {
            onError(error);
        }
        finally
        {
            running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
