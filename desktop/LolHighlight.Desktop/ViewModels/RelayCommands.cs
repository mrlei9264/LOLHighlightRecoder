using System.Windows.Input;

namespace LolHighlight.Desktop.ViewModels;

internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public AsyncRelayCommand(Func<Task> execute) => _execute = execute;

    public bool CanExecute(object? parameter) => !_isExecuting;

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync(parameter);
        }
        catch
        {
            // 命令的失败状态由对应 ViewModel/服务显示，绝不能让异常从 async void 逃逸并终结进程。
        }
    }

    public async Task ExecuteAsync(object? parameter)
    {
        if (_isExecuting)
            return;

        _isExecuting = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        finally
        {
            _isExecuting = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public RelayCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}
