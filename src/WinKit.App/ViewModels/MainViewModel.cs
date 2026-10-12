using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using WinKit.App.Mvvm;
using WinKit.App.Services;
using WinKit.Core.Configuration;
using WinKit.Core.Diagnostics;

namespace WinKit.App.ViewModels
{
    /// <summary>
    /// 应用外壳 ViewModel：导航（分组 + 搜索过滤）、页面缓存、主题切换、状态栏信息。
    ///
    /// 设计约束：
    /// - 这里不做任何 IO（打开目录只是交给 explorer.exe，不读盘）；
    /// - 同一个模块的页面视图只创建一次并缓存，切回来复用 —— 否则页面里的定时器会随切换泄漏；
    /// - 页面创建失败只降级成一个提示视图，不允许把整个外壳带崩。
    /// </summary>
    public sealed class MainViewModel : ObservableObject, IDisposable
    {
        private const double DefaultWindowWidth = 1180d;
        private const double DefaultWindowHeight = 720d;
        private const double MinWindowWidth = 900d;
        private const double MinWindowHeight = 600d;

        private readonly List<ToolModuleViewModel> _modules;
        private readonly Dictionary<string, FrameworkElement> _viewCache =
            new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);
        private readonly bool _readOnly;

        private string _searchText = string.Empty;
        private ToolModuleViewModel? _selectedModule;
        private FrameworkElement? _currentView;
        private string _themeLabel;
        private bool _disposed;

        public MainViewModel()
        {
            _readOnly = AppServices.IsInitialized && AppServices.Config != null && AppServices.Config.Current.ReadOnlyMode;

            var discovered = ModuleRegistry.Discover();
            _modules = new List<ToolModuleViewModel>(discovered.Count);
            foreach (var module in discovered)
            {
                if (module == null) continue;
                _modules.Add(new ToolModuleViewModel(module));
            }

            VisibleModules = new ObservableCollection<ToolModuleViewModel>(_modules);

            // 分组交给 VM 侧做：XAML 里就不用写 CollectionViewSource 的绑定，
            // 少一个"资源里 Binding 找不到 DataContext"的坑。
            var view = CollectionViewSource.GetDefaultView(VisibleModules);
            if (view != null && view.GroupDescriptions != null)
            {
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ToolModuleViewModel.Group)));
            }
            // GetDefaultView 对非空集合不会返回 null；这里只是给可空分析一个明确结论
            ModulesView = view!;

            // 配置读不到时退化成默认尺寸，绝不因为配置缺失让外壳起不来
            AppConfig config = new AppConfig();
            var store = AppServices.Config;
            if (AppServices.IsInitialized && store != null && store.Current != null)
            {
                config = store.Current;
            }
            WindowWidth = ClampDimension(config.WindowWidth, DefaultWindowWidth, MinWindowWidth, SafeVirtualScreenWidth());
            WindowHeight = ClampDimension(config.WindowHeight, DefaultWindowHeight, MinWindowHeight, SafeVirtualScreenHeight());
            WindowMaximized = config.WindowMaximized;

            _themeLabel = DescribeTheme(ThemeManager.Current.AppliedTheme);
            VersionText = DescribeVersion();
            StatusText = BuildStatusText();

            ToggleThemeCommand = new RelayCommand(ToggleTheme);
            OpenDataFolderCommand = new RelayCommand(() => OpenFolder(ResolveDataDirectory(), "数据目录"));
            OpenLogFolderCommand = new RelayCommand(() => OpenFolder(ResolveLogDirectory(), "日志目录"));

            ThemeManager.Current.ThemeChanged += OnThemeChanged;

            // 默认选中第一个模块
            if (_modules.Count > 0)
            {
                _selectedModule = _modules[0];
                RefreshCurrentView();
                OnPropertyChanged(nameof(SelectedModule));
            }

            Log.Info("应用外壳就绪：共 " + _modules.Count + " 个功能模块");
        }

        /// <summary>全量模块（导航过滤只影响 VisibleModules，不影响这里）。</summary>
        public IReadOnlyList<ToolModuleViewModel> Modules => _modules;

        /// <summary>当前可见（经过搜索过滤）的模块，供左侧导航绑定。</summary>
        public ObservableCollection<ToolModuleViewModel> VisibleModules { get; }

        /// <summary>带分组描述的默认视图，供 ListBox 绑定（GroupStyle 显示 "总览" / "系统" 等分组标题）。</summary>
        public ICollectionView ModulesView { get; }

        public ToolModuleViewModel? SelectedModule
        {
            get => _selectedModule;
            set
            {
                if (!SetProperty(ref _selectedModule, value)) return;
                RefreshCurrentView();
            }
        }

        public FrameworkElement? CurrentView
        {
            get => _currentView;
            private set => SetProperty(ref _currentView, value);
        }

        /// <summary>非空时按 Title / Keywords / Group 过滤导航（忽略大小写，空格分隔多个关键字）。</summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
                ApplyFilter();
            }
        }

        public string ThemeLabel
        {
            get => _themeLabel;
            private set => SetProperty(ref _themeLabel, value);
        }

        public string PermissionText => AppServices.PermissionText;

        public bool IsElevated => AppServices.IsElevated;

        /// <summary>只读审计模式：界面上的写操作入口都要先看这个值。</summary>
        public bool IsReadOnly => _readOnly;

        public string DataDirectoryText => AppServices.DataDirectoryText;

        public string ModeText => AppServices.ModeText;

        public string VersionText { get; }

        public string StatusText { get; }

        public double WindowWidth { get; }

        public double WindowHeight { get; }

        public bool WindowMaximized { get; }

        /// <summary>换肤（会写回配置里的 theme，属于界面偏好，不受只读模式限制）。</summary>
        public RelayCommand ToggleThemeCommand { get; }

        /// <summary>只读模式下依然可用（纯浏览，无副作用）。</summary>
        public RelayCommand OpenDataFolderCommand { get; }

        /// <summary>只读模式下依然可用（纯浏览，无副作用）。</summary>
        public RelayCommand OpenLogFolderCommand { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { ThemeManager.Current.ThemeChanged -= OnThemeChanged; } catch (Exception) { }
        }

        private void ApplyFilter()
        {
            var query = _searchText == null ? string.Empty : _searchText.Trim();
            var filtered = query.Length == 0
                ? _modules
                : _modules.Where(m => m.Matches(query)).ToList();

            VisibleModules.Clear();
            foreach (var item in filtered) VisibleModules.Add(item);

            // 当前页面还在结果里就别动它，避免搜索时页面被反复重建
            if (_selectedModule != null && VisibleModules.Contains(_selectedModule)) return;

            SelectedModule = VisibleModules.Count > 0 ? VisibleModules[0] : null;
        }

        private void RefreshCurrentView()
        {
            // 关键：切页时先把 ContentControl.Content 置空，让旧页面真正被摘离可视树。
            // 若只是把旧页面藏起来（Visibility=Collapsed），它不会收到 Unloaded，
            // 页面里挂在 Loaded/Unloaded 上的采样定时器就会一直跑下去。
            // 视图本身仍然留在缓存里，切回来是复用而不是重建。
            CurrentView = null;

            var module = _selectedModule;
            if (module == null) return;

            if (_viewCache.TryGetValue(module.Key, out var cached))
            {
                CurrentView = cached;
                return;
            }

            FrameworkElement view;
            try
            {
                view = module.Module.CreateView();
                if (view == null) view = CreateFallbackView(module, null);
            }
            catch (Exception ex)
            {
                Log.Error("创建功能页面失败：" + module.Key + " → " + ex.Message, ex);
                view = CreateFallbackView(module, ex);
            }

            _viewCache[module.Key] = view;
            CurrentView = view;
        }

        private void ToggleTheme()
        {
            try
            {
                ThemeManager.Current.Toggle();
                ThemeLabel = DescribeTheme(ThemeManager.Current.AppliedTheme);
            }
            catch (Exception ex)
            {
                Log.Error("切换主题失败：" + ex.Message, ex);
            }
        }

        private void OnThemeChanged(string theme)
        {
            ThemeLabel = DescribeTheme(theme);
        }

        private string BuildStatusText()
        {
            var parts = new List<string>();
            parts.Add("共 " + _modules.Count + " 个功能");
            if (_readOnly) parts.Add("只读模式：写操作已禁用");
            if (!IsElevated) parts.Add("部分功能需要管理员权限");
            return string.Join(" · ", parts.ToArray());
        }

        private static string DescribeTheme(string? theme)
        {
            return string.Equals(theme, ThemeMode.Light, StringComparison.OrdinalIgnoreCase) ? "浅色" : "深色";
        }

        private static string DescribeVersion()
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version;
            return version == null ? "v0.1.0" : "v" + version.ToString(3);
        }

        private static double ClampDimension(double value, double fallback, double minimum, double screenLimit)
        {
            var result = value;
            if (double.IsNaN(result) || double.IsInfinity(result) || result <= 0) result = fallback;
            if (result < minimum) result = minimum;
            if (screenLimit > 0 && result > screenLimit) result = screenLimit;
            return result;
        }

        /// <summary>SystemParameters 在非交互会话下可能抛异常，取不到就当没有上限。</summary>
        private static double SafeVirtualScreenWidth()
        {
            try { return SystemParameters.VirtualScreenWidth; } catch (Exception) { return 0d; }
        }

        private static double SafeVirtualScreenHeight()
        {
            try { return SystemParameters.VirtualScreenHeight; } catch (Exception) { return 0d; }
        }

        private static string? ResolveDataDirectory()
        {
            if (!AppServices.IsInitialized || AppServices.Data == null) return null;
            return AppServices.Data.Directory;
        }

        private static string? ResolveLogDirectory()
        {
            if (AppServices.IsInitialized && AppServices.Data != null) return AppServices.Data.LogDirectory;

            // 兜底：从当前日志文件反推目录
            var current = Log.CurrentFile;
            return string.IsNullOrEmpty(current) ? null : System.IO.Path.GetDirectoryName(current);
        }

        /// <summary>用 explorer.exe 打开目录。只读无害，所以不检查只读模式；写操作入口才需要禁用。</summary>
        private static void OpenFolder(string? path, string label)
        {
            if (string.IsNullOrEmpty(path))
            {
                Log.Warn("无法打开" + label + "：路径不可用");
                ShowWarning("无法打开" + label + "：路径不可用。");
                return;
            }

            try
            {
                var target = path!;
                if (System.IO.File.Exists(target))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + target + "\"");
                }
                else
                {
                    if (!System.IO.Directory.Exists(target)) Log.Warn(label + "尚不存在：" + target);
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + target + "\"");
                }
                Log.Info("已请求打开" + label + "：" + target);
            }
            catch (Exception ex)
            {
                Log.Error("打开" + label + "失败：" + ex.Message, ex);
                ShowWarning("打开" + label + "失败：" + ex.Message);
            }
        }

        private static void ShowWarning(string message)
        {
            try
            {
                MessageBox.Show(message, "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception)
            {
                // 弹不出对话框时只留日志
            }
        }

        private static FrameworkElement CreateFallbackView(ToolModuleViewModel module, Exception? error)
        {
            var text = "无法加载「" + module.Title + "」页面。";
            if (error != null) text += Environment.NewLine + error.Message;

            var block = new System.Windows.Controls.TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
            };
            block.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return block;
        }
    }

    /// <summary>
    /// 左侧导航的一项。公开类型是必须的：WPF 绑定要求属性公开可见，
    /// 而 MainViewModel.Modules 是公开属性，不能暴露 internal 类型。
    /// </summary>
    public sealed class ToolModuleViewModel
    {
        public ToolModuleViewModel(IToolModule module)
        {
            Module = module ?? throw new ArgumentNullException(nameof(module));

            var id = module.Id;
            Key = string.IsNullOrEmpty(id) ? "module-" + Guid.NewGuid().ToString("N") : id!;
        }

        public IToolModule Module { get; }

        /// <summary>视图缓存用的稳定键（模块 Id 缺失时退化成随机键，保证不会互相覆盖）。</summary>
        public string Key { get; }

        public string Title => Module.Title;

        public string Icon => Module.Icon;

        public string Group => Module.Group;

        public bool RequiresAdmin => Module.RequiresAdmin;

        public string Keywords => Module.Keywords;

        /// <summary>命令面板式搜索：空格分隔的多个关键字必须全部命中（Title / Keywords / Group / Id）。</summary>
        public bool Matches(string query)
        {
            var trimmed = query == null ? string.Empty : query.Trim();
            if (trimmed.Length == 0) return true;

            var haystack = Title + " " + Keywords + " " + Group + " " + Key;
            var terms = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var term in terms)
            {
                if (haystack.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) < 0) return false;
            }
            return true;
        }

        public override string ToString() => Title;
    }
}
