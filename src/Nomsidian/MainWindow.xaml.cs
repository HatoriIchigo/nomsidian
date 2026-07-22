using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private static readonly TimeSpan ExternalChangeDebounce = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan OwnWriteIgnoreWindow = TimeSpan.FromMilliseconds(750);

    private readonly string _rootDirectory;
    private readonly TaskCompletionSource _editorReadyTcs = new();
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _externalChangeTimer;
    private readonly List<string> _navigationHistory = new();
    private readonly List<MarkdownFileNode> _allFiles = new();
    private readonly ObservableCollection<OpenDocument> _openDocuments = new();
    private FileSystemWatcher? _fileWatcher;
    private DateTime _ignoreWatcherUntilUtc = DateTime.MinValue;
    private OpenDocument? _activeDocument;
    private int _navigationIndex = -1;
    private bool _isNavigatingHistory;
    private bool _sidebarVisible = true;
    private bool _isSwitchingTabProgrammatically;
    private double _lastSidebarWidth = 240;

    public MainWindow(string rootDirectory)
    {
        InitializeComponent();

        _rootDirectory = rootDirectory;
        _autoSaveTimer = new DispatcherTimer { Interval = AutoSaveDelay };
        _autoSaveTimer.Tick += AutoSaveTimer_OnTick;
        _externalChangeTimer = new DispatcherTimer { Interval = ExternalChangeDebounce };
        _externalChangeTimer.Tick += ExternalChangeTimer_OnTick;
        TabStrip.ItemsSource = _openDocuments;
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

        if (_activeDocument is { IsDirty: true } document)
        {
            await SaveDocumentAsync(document);
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

        if (type != "change" || _activeDocument is null)
        {
            return;
        }

        _activeDocument.Text = root.GetProperty("text").GetString() ?? string.Empty;
        _activeDocument.IsDirty = true;
        UpdateStatusBar();

        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void ReloadFileTree()
    {
        var root = DirectoryService.BuildTree(_rootDirectory);
        FileTree.ItemsSource = root.Children;
        Title = $"nomsidian - {_rootDirectory}";
        if (ExplorerToggleButton.IsChecked == true)
        {
            SidebarHeaderText.Text = root.Name.Length > 0 ? root.Name.ToUpperInvariant() : "FILES";
        }

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

        var existing = _openDocuments.FirstOrDefault(d => d.FilePath == path);
        if (existing is not null)
        {
            await ActivateDocumentAsync(existing);
            return;
        }

        var text = FileService.ReadAllText(path);
        var document = new OpenDocument(path, text);
        _openDocuments.Add(document);
        await ActivateDocumentAsync(document);
    }

    private async Task ActivateDocumentAsync(OpenDocument document)
    {
        if (_activeDocument == document)
        {
            return;
        }

        await FlushPendingAutoSaveAsync();

        _activeDocument = document;
        UpdateStatusBar();
        RecordNavigation(document.FilePath);
        SetupFileWatcher(document.FilePath);

        _isSwitchingTabProgrammatically = true;
        TabStrip.SelectedItem = document;
        _isSwitchingTabProgrammatically = false;

        var script = $"window.__nomuSetContent({JsonSerializer.Serialize(document.Text)})";
        await EditorView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void TabStrip_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isSwitchingTabProgrammatically || TabStrip.SelectedItem is not OpenDocument document || document == _activeDocument)
        {
            return;
        }

        _ = RunAndReportErrorsAsync(() => ActivateDocumentAsync(document));
    }

    private void TabCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).Tag is OpenDocument document)
        {
            _ = RunAndReportErrorsAsync(() => CloseDocumentAsync(document));
        }
    }

    private async Task CloseDocumentAsync(OpenDocument document)
    {
        var wasActive = document == _activeDocument;
        if (wasActive)
        {
            await FlushPendingAutoSaveAsync();
        }

        var index = _openDocuments.IndexOf(document);
        _openDocuments.Remove(document);

        if (!wasActive)
        {
            return;
        }

        _activeDocument = null;

        if (_openDocuments.Count > 0)
        {
            var nextIndex = Math.Min(index, _openDocuments.Count - 1);
            await ActivateDocumentAsync(_openDocuments[nextIndex]);
        }
        else
        {
            _fileWatcher?.Dispose();
            _fileWatcher = null;
            UpdateStatusBar();
            await EditorView.CoreWebView2.ExecuteScriptAsync("window.__nomuSetContent('')");
        }
    }

    private void SetupFileWatcher(string path)
    {
        _fileWatcher?.Dispose();
        _fileWatcher = null;

        var directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            return;
        }

        var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += FileWatcher_OnChanged;
        watcher.EnableRaisingEvents = true;
        _fileWatcher = watcher;
    }

    private void FileWatcher_OnChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (DateTime.UtcNow < _ignoreWatcherUntilUtc || e.FullPath != _activeDocument?.FilePath)
            {
                return;
            }

            _externalChangeTimer.Stop();
            _externalChangeTimer.Start();
        });
    }

    private void ExternalChangeTimer_OnTick(object? sender, EventArgs e)
    {
        _externalChangeTimer.Stop();
        _ = RunAndReportErrorsAsync(ReloadActiveDocumentIfCleanAsync);
    }

    private async Task ReloadActiveDocumentIfCleanAsync()
    {
        if (_activeDocument is not { IsDirty: false } document || !File.Exists(document.FilePath))
        {
            return;
        }

        var text = FileService.ReadAllText(document.FilePath);
        if (text == document.Text)
        {
            return;
        }

        document.Text = text;
        UpdateStatusBar();

        var script = $"window.__nomuSetContent({JsonSerializer.Serialize(document.Text)})";
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
        StatusPathText.Text = _activeDocument is null
            ? "ファイルが選択されていません"
            : (_activeDocument.IsDirty ? "* " : string.Empty) + Path.GetRelativePath(_rootDirectory, _activeDocument.FilePath);
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
        if (_activeDocument is null)
        {
            SaveAsMenuItem_OnClick(sender, e);
            return;
        }

        _ = RunAndReportErrorsAsync(() => SaveDocumentAsync(_activeDocument));
    }

    private void SaveAsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Markdown ファイル (*.md)|*.md|すべてのファイル (*.*)|*.*",
            InitialDirectory = _rootDirectory,
            FileName = _activeDocument is null ? "untitled.md" : Path.GetFileName(_activeDocument.FilePath),
        };

        if (dialog.ShowDialog(this) == true)
        {
            _ = RunAndReportErrorsAsync(() => SaveActiveDocumentAsAsync(dialog.FileName));
        }
    }

    private async Task SaveDocumentAsync(OpenDocument document)
    {
        var resultJson = await EditorView.CoreWebView2.ExecuteScriptAsync("window.__nomuGetContent()");
        var text = JsonSerializer.Deserialize<string>(resultJson) ?? document.Text;

        _ignoreWatcherUntilUtc = DateTime.UtcNow.Add(OwnWriteIgnoreWindow);
        FileService.WriteAllText(document.FilePath, text);
        document.Text = text;
        document.IsDirty = false;
        UpdateStatusBar();
    }

    private async Task SaveActiveDocumentAsAsync(string newPath)
    {
        var resultJson = await EditorView.CoreWebView2.ExecuteScriptAsync("window.__nomuGetContent()");
        var text = JsonSerializer.Deserialize<string>(resultJson) ?? string.Empty;

        _ignoreWatcherUntilUtc = DateTime.UtcNow.Add(OwnWriteIgnoreWindow);
        FileService.WriteAllText(newPath, text);

        if (_activeDocument is not null)
        {
            _activeDocument.UpdatePath(newPath);
            _activeDocument.Text = text;
            _activeDocument.IsDirty = false;
        }
        else
        {
            var document = new OpenDocument(newPath, text);
            _openDocuments.Add(document);
            _activeDocument = document;
            _isSwitchingTabProgrammatically = true;
            TabStrip.SelectedItem = document;
            _isSwitchingTabProgrammatically = false;
        }

        SetupFileWatcher(newPath);
        RecordNavigation(newPath);
        UpdateStatusBar();
        ReloadFileTree();
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool _closeConfirmed;

    private void MainWindow_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed || !_autoSaveTimer.IsEnabled && _activeDocument?.IsDirty != true)
        {
            return;
        }

        e.Cancel = true;
        _ = CloseAfterFlushAsync();
    }

    private async Task CloseAfterFlushAsync()
    {
        await RunAndReportErrorsAsync(FlushPendingAutoSaveAsync);
        _fileWatcher?.Dispose();
        _closeConfirmed = true;
        Close();
    }
}
