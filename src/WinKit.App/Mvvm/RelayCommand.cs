using System;
using System.Threading.Tasks;
using System.Windows.Input;
using WinKit.Core.Diagnostics;

namespace WinKit.App.Mvvm
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute == null ? (Predicate<object?>?)null : _ => canExecute())
        {
        }

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);

        public void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            _execute(parameter);
        }

        /// <summary>
        /// 请求一次全局 CanExecute 重新求值（影响所有基于 CommandManager 的命令）。
        /// 注意这是**静态**方法，所以 RelayCommand 上没有同名实例方法；
        /// 需要按实例刷新时请用 AsyncRelayCommand 的实例方法，或直接调用本方法。
        /// </summary>
        public static void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// 异步命令。带重入保护：执行期间 CanExecute 为 false，避免用户连点导致并发执行。
    /// 异常一律记入日志而不是抛到 Dispatcher，防止整个界面因一个后台异常崩掉。
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Predicate<object?>? _canExecute;
        private bool _running;

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute == null ? (Predicate<object?>?)null : _ => canExecute())
        {
        }

        public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool IsRunning => _running;

        /// <summary>
        /// 请求一次 CanExecute 重新求值。与 WPF 惯例保持一致，调用方可以直接在命令实例上调用
        /// （例如 RefreshCommand.RaiseCanExecuteChanged()），不必去碰 RelayCommand 的静态方法。
        /// </summary>
        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();

        public bool CanExecute(object? parameter) => !_running && (_canExecute == null || _canExecute(parameter));

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            _running = true;
            RelayCommand.RaiseCanExecuteChanged();
            try
            {
                await _execute(parameter);
            }
            catch (OperationCanceledException)
            {
                // 用户主动取消，不算错误
            }
            catch (Exception ex)
            {
                Log.Error("异步命令执行失败：" + ex.Message, ex);
            }
            finally
            {
                _running = false;
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }
}
