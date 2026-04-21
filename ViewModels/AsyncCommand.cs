using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using RightSpeak.Services;

namespace RightSpeak.ViewModels;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (_isExecuting)
        {
            return false;
        }

        try
        {
            return _canExecute?.Invoke() ?? true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "async_command_can_execute_failed",
                new Dictionary<string, string?>
                {
                    ["commandType"] = GetType().FullName,
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
            return false;
        }
    }

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteCoreAsync(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AppDiagnostics.Info(
                "async_command_execute_canceled",
                new Dictionary<string, string?>
                {
                    ["commandType"] = GetType().FullName
                });
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "async_command_execute_failed",
                new Dictionary<string, string?>
                {
                    ["commandType"] = GetType().FullName,
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
    }

    public Task ExecuteAsync(object? parameter = null)
    {
        return ExecuteCoreAsync(parameter);
    }

    private async Task ExecuteCoreAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        try
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "async_command_can_execute_changed_failed",
                new Dictionary<string, string?>
                {
                    ["commandType"] = GetType().FullName,
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
    }
}
