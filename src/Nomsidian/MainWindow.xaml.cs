using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Nomsidian.Services;

namespace Nomsidian;

public partial class MainWindow : Window
{
    private const string VirtualHostName = "nomu.local";
    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromSeconds(1);

    private readonly string _rootDirectory;
    private readonly TaskCompletionSource _editorReadyTcs = new();
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly List<string> _navigationHistory = new();
    private readonly List<MarkdownFileNode> _allFiles = new();
    private string? _currentFilePath;
    private string _currentText = string.Empty;
    private bool _isDirty;
    private int _navigationIndex = -1;
    private bool _isNavigatingHistory;
    private bool _sidebarVisible = true;
    private double _lastSidebarWidth = 240;

    public MainWindow(string rootDirectory)
    {
        InitializeComponent();

        _rootDirectory = rootDirectory;
        _autoSaveTimer = new DispatcherTimer { Interval = AutoSaveDelay };
        _autoSaveTimer.Tick += AutoSaveTimer_OnTick;
        SourceInitialized += (_, _) => TryEnableDarkTitleBar();
        Loaded += async (_, _) => await RunAndReportErrorsAsync(InitializeEditorAsync);
        ReloadFileTree();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private void TryEnableDarkTitleBar()
    {
        const int DwmwaUseImmersiveDarkMode = 20;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var enabled = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // 古い Windows ではダークタイトルバー非対応のため無視する
        }
    }

    private void MainWindow_OnStateChanged(object? sender, EventArgs e)
    {
        RootBorder.Margin = WindowState == WindowState.Maximized
            ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left)
            : new Thickness(0);
    }

    private void AutoSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        _ = RunAndReportErrorsAsync(FlushPendingAutoSaveAsync);
    }

    private async Task FlushPendingAutoSaveAsync()
    {
        _autoSaveTimer.Stop();

        if (_currentFilePath is not null && _isDirty)
        {
            await SaveToFileAsync(_currentFilePath);
        }
    }

    private static async Task RunAndReportErrorsAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "nomsidian エラー");
        }
    }

    private async Task InitializeEditorAsync()
    {
        await EditorView.EnsureCoreWebView2Async();

        var webUiDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "webui");
        EditorView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHostName, webUiDirectory, CoreWebView2HostResourceAccessKind.Allow);

        EditorView.CoreWebView2.WebMessageReceived += CoreWebView2_OnWebMessageReceived;

        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            EditorView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            _editorReadyTcs.SetResult();
        }
        EditorView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

        EditorView.CoreWebView2.Navigate($"https://{VirtualHostName}/editor.html");
    }

    private void CoreWebView2_OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();

        if (type == "error")
        {
            MessageBox.Show(
                $"{root.GetProperty("source").GetString()}\n\n{root.GetProperty("message").GetString()}",
                "nomsidian JS エラー");
            return;
        }

        if (type != "change")
        {
            return;
        }

        _currentText = root.GetProperty("text").GetString() ?? string.Empty;
        _isDirty = true;
        UpdateStatusBar();

        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void ReloadFileTree()
    {
        var root = DirectoryService.BuildTree(_rootDirectory);
        FileTree.ItemsSource = root.Children;
        Title = $"nomsidian - {_rootDirectory}";
        SidebarHeaderText.Text = root.Name.Length > 0 ? root.Name.ToUpperInvariant() : "FILES";

        _allFiles.Clear();
        _allFiles.AddRange(FlattenFiles(root));
    }

    private static IEnumerable<MarkdownFileNode> FlattenFiles(MarkdownFileNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.IsDirectory)
            {
                foreach (var descendant in FlattenFiles(child))
                {
                    yield return descendant;
                }
            }
            else
            {
                yield return child;
            }
        }
    }

    private void FileTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is MarkdownFileNode { IsDirectory: false } node)
        {
            _ = RunAndReportErrorsAsync(() => OpenFileAsync(node.FullPath));
        }
    }

    private async Task OpenFileAsync(string path)
    {
        await _editorReadyTcs.Task;
        await FlushPendingAutoSaveAsync();

        _currentText = FileService.ReadAllText(path);
        _currentFilePath = path;
        _isDirty = false;
        UpdateStatusBar();
        RecordNavigation(path);

        var script = $"window.__nomuSetContent({JsonSerializer.Serialize(_currentText)})";
        await EditorView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void RecordNavigation(string path)
    {
        if (_isNavigatingHistory)
        {
            return;
        }

        if (_navigationIndex >= 0 && _navigationIndex < _navigationHistory.Count
            && _navigationHistory[_navigationIndex] == path)
        {
            return;
        }

        if (_navigationIndex < _navigationHistory.Count - 1)
        {
            _navigationHistory.RemoveRange(_navigationIndex + 1, _navigationHistory.Count - _navigationIndex - 1);
        }

        _navigationHistory.Add(path);
        _navigationIndex = _navigationHistory.Count - 1;
        UpdateNavigationButtons();
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _navigationIndex > 0;
        ForwardButton.IsEnabled = _navigationIndex >= 0 && _navigationIndex < _navigationHistory.Count - 1;
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_navigationIndex <= 0)
        {
            return;
        }

        _navigationIndex--;
        UpdateNavigationButtons();
        _isNavigatingHistory = true;
        _ = RunAndReportErrorsAsync(async () =>
        {
            try
            {
                await OpenFileAsync(_navigationHistory[_navigationIndex]);
            }
            finally
            {
                _isNavigatingHistory = false;
            }
        });
    }

    private void ForwardButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_navigationIndex < 0 || _navigationIndex >= _navigationHistory.Count - 1)
        {
            return;
        }

        _navigationIndex++;
        UpdateNavigationButtons();
        _isNavigatingHistory = true;
        _ = RunAndReportErrorsAsync(async () =>
        {
            try
            {
                await OpenFileAsync(_navigationHistory[_navigationIndex]);
            }
            finally
            {
                _isNavigatingHistory = false;
            }
        });
    }

    private void SidebarToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        _sidebarVisible = !_sidebarVisible;

        if (_sidebarVisible)
        {
            SidebarColumn.Width = new GridLength(_lastSidebarWidth);
            SidebarColumn.MinWidth = 140;
        }
        else
        {
            _lastSidebarWidth = SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : _lastSidebarWidth;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
        }

        SidebarSplitterBorder.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowSidebar()
    {
        if (!_sidebarVisible)
        {
            SidebarToggleButton_OnClick(this, new RoutedEventArgs());
        }
    }

    private void ExplorerToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowSidebar();
        ExplorerToggleButton.IsChecked = true;
        SearchToggleButton.IsChecked = false;
        FileTree.Visibility = Visibility.Visible;
        SearchPanel.Visibility = Visibility.Collapsed;
        SidebarHeaderText.Text = Path.GetFileName(_rootDirectory.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
            ? name.ToUpperInvariant()
            : "FILES";
    }

    private void SearchToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowSidebar();
        SearchToggleButton.IsChecked = true;
        ExplorerToggleButton.IsChecked = false;
        FileTree.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Visible;
        SidebarHeaderText.Text = "SEARCH";
        SearchBox.Focus();
    }

    private void SearchBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        SearchResultsList.ItemsSource = query.Length == 0
            ? Array.Empty<MarkdownFileNode>()
            : _allFiles.Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void SearchResultsList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is MarkdownFileNode node)
        {
            _ = RunAndReportErrorsAsync(() => OpenFileAsync(node.FullPath));
        }
    }

    private void UpdateStatusBar()
    {
        StatusPathText.Text = _currentFilePath is null
            ? "ファイルが選択されていません"
            : (_isDirty ? "* " : string.Empty) + Path.GetRelativePath(_rootDirectory, _currentFilePath);
    }

    private void OpenMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Markdown ファイル (*.md)|*.md|すべてのファイル (*.*)|*.*",
            InitialDirectory = _rootDirectory,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _ = RunAndReportErrorsAsync(() => OpenFileAsync(dialog.FileName));
        }
    }

    private void SaveMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentFilePath is null)
        {
            SaveAsMenuItem_OnClick(sender, e);
            return;
        }

        _ = RunAndReportErrorsAsync(() => SaveToFileAsync(_currentFilePath));
    }

    private void SaveAsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Markdown ファイル (*.md)|*.md|すべてのファイル (*.*)|*.*",
            InitialDirectory = _rootDirectory,
            FileName = _currentFilePath is null ? "untitled.md" : Path.GetFileName(_currentFilePath),
        };

        if (dialog.ShowDialog(this) == true)
        {
            _ = RunAndReportErrorsAsync(() => SaveToFileAsync(dialog.FileName, reloadTree: true));
        }
    }

    private async Task SaveToFileAsync(string path, bool reloadTree = false)
    {
        var resultJson = await EditorView.CoreWebView2.ExecuteScriptAsync("window.__nomuGetContent()");
        var text = JsonSerializer.Deserialize<string>(resultJson) ?? _currentText;

        FileService.WriteAllText(path, text);
        _currentText = text;
        _currentFilePath = path;
        _isDirty = false;
        UpdateStatusBar();

        if (reloadTree)
        {
            ReloadFileTree();
        }
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool _closeConfirmed;

    private void MainWindow_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed || !_autoSaveTimer.IsEnabled && !_isDirty)
        {
            return;
        }

        e.Cancel = true;
        _ = CloseAfterFlushAsync();
    }

    private async Task CloseAfterFlushAsync()
    {
        await RunAndReportErrorsAsync(FlushPendingAutoSaveAsync);
        _closeConfirmed = true;
        Close();
    }
}
