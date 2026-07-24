using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Nomsidian.Config;
using Nomsidian.Services;

namespace Nomsidian;

public partial class MainWindow : Window
{
    private enum SearchMode
    {
        FileName,
        InFile,
        CrossFile,
    }

    private const string VirtualHostName = "nomu.local";
    private const string VaultVirtualHostName = "nomu.vault";
    private static readonly TimeSpan ExternalChangeDebounce = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan OwnWriteIgnoreWindow = TimeSpan.FromMilliseconds(750);
    // JS側はcamelCase(from/to/lineText等)で送ってくるが、C#のプロパティはPascalCaseのため、
    // 大文字小文字を区別しないSystem.Text.Jsonの既定動作(区別する)のままだと全滅する。
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private string _rootDirectory;
    private NomuConfig _config;
    private readonly ConfigOverrides _configOverrides;
    private readonly TaskCompletionSource _editorReadyTcs = new();
    private readonly DispatcherTimer _externalChangeTimer;
    private readonly List<FileNode> _allFiles = new();
    private readonly ObservableCollection<OpenDocument> _openDocuments = new();
    private FileSystemWatcher? _fileWatcher;
    private DateTime _ignoreWatcherUntilUtc = DateTime.MinValue;
    private OpenDocument? _activeDocument;
    private bool _sidebarVisible = true;
    private bool _isSwitchingTabProgrammatically;
    private double _lastSidebarWidth = 240;
    private SearchMode _searchMode = SearchMode.FileName;
    private bool _searchMarkdownOnly;
    private CancellationTokenSource? _crossFileSearchCts;
    private bool _outlineVisible = true;
    private double _lastOutlineWidth = 200;
    private readonly ObservableCollection<HeadingItem> _headings = new();
    private readonly ObservableCollection<FileNode> _favorites = new();
    private FileNode? _contextMenuNode;
    private string? _gitBranch;
    private string? _vimMode;
    private int _cursorLine = 1;
    private int _cursorCol = 1;
    private string _filetype = string.Empty;

    public MainWindow(string rootDirectory, NomuConfig config, ConfigOverrides configOverrides)
    {
        InitializeComponent();
        ApplyConfig(config);

        _rootDirectory = rootDirectory;
        _config = config;
        _configOverrides = configOverrides;
        _externalChangeTimer = new DispatcherTimer { Interval = ExternalChangeDebounce };
        _externalChangeTimer.Tick += ExternalChangeTimer_OnTick;
        TabStrip.ItemsSource = _openDocuments;
        OutlineList.ItemsSource = _headings;
        FavoritesList.ItemsSource = _favorites;
        LoadFavorites();
        SourceInitialized += (_, _) => TryEnableDarkTitleBar();
        Loaded += async (_, _) => await RunAndReportErrorsAsync(InitializeEditorAsync);
        ReloadFileTree();
    }

    /// <summary>
    /// nomu.lua で指定されたテーマ/フォントを適用する。XAML の各コントロールは
    /// StaticResource でこれらのブラシインスタンスを直接参照しているため、
    /// (キーの差し替えでなく) 既存ブラシの Color を書き換えることで反映される。
    /// </summary>
    private void ApplyConfig(NomuConfig config)
    {
        ApplyBrush("EditorBgBrush", config.Theme.EditorBg);
        ApplyBrush("SidebarBgBrush", config.Theme.SidebarBg);
        ApplyBrush("PanelBgBrush", config.Theme.PanelBg);
        ApplyBrush("BorderBrush", config.Theme.Border);
        ApplyBrush("TextBrush", config.Theme.Text);
        ApplyBrush("MutedTextBrush", config.Theme.MutedText);
        ApplyBrush("AccentBrush", config.Theme.Accent);
        ApplyBrush("AccentMutedBrush", config.Theme.AccentMuted);
        ApplyBrush("HoverBrush", config.Theme.Hover);

        FontFamily = new FontFamily(config.Font.Family);
        FontSize = config.Font.Size;
    }

    private void ApplyBrush(string resourceKey, string hexColor)
    {
        // 既存ブラシは WPF が自動 Freeze しており Color を直接書き換えられないため、
        // DynamicResource 参照側から見えるようエントリ自体を新しいブラシに差し替える。
        var color = (Color)ColorConverter.ConvertFromString(hexColor);
        Resources[resourceKey] = new SolidColorBrush(color);
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

    // タイトルバー右端の最大化ボタンのアイコン（四角＝最大化 / 二重四角＝元に戻す）。
    private const string MaximizeGlyph = "M0.5,0.5 H9.5 V9.5 H0.5 Z";
    private const string RestoreGlyph = "M2.5,2.5 V0.5 H9.5 V7.5 H7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z";

    private void MainWindow_OnStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        RootBorder.Margin = maximized
            ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left)
            : new Thickness(0);

        MaximizeRestoreIcon.Data = Geometry.Parse(maximized ? RestoreGlyph : MaximizeGlyph);
        MaximizeRestoreButton.ToolTip = maximized ? "元に戻す" : "最大化";
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_OnClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
        => Close();

    private async Task SaveActiveDocumentIfDirtyAsync()
    {
        if (_activeDocument is { IsDirty: true } document)
        {
            await SaveDocumentAsync(document);
        }
    }

    private void SaveCommand_OnCanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _activeDocument is not null;
    }

    private void SaveCommand_OnExecuted(object sender, RoutedEventArgs e)
    {
        _ = RunAndReportErrorsAsync(SaveActiveDocumentIfDirtyAsync);
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

        // Markdown内の相対パス画像(![alt](./img.png)など)を解決するため、vaultルートも仮想ホストとして公開する
        EditorView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VaultVirtualHostName, _rootDirectory, CoreWebView2HostResourceAccessKind.Allow);

        EditorView.CoreWebView2.WebMessageReceived += CoreWebView2_OnWebMessageReceived;

        // bundle.js(__nomuInit)実行前に読める必要があるため、ナビゲーション前にスクリプトとして注入する。
        var vimModeFlag = _config.Editor.VimMode ? "true" : "false";
        await EditorView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync($"window.__nomuVimMode = {vimModeFlag};");

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

        if (type == "openUrl")
        {
            var url = root.GetProperty("url").GetString();
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "mailto"))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString(), "nomsidian エラー");
                }
            }
            return;
        }

        if (type == "cursor")
        {
            _cursorLine = root.GetProperty("line").GetInt32();
            _cursorCol = root.GetProperty("col").GetInt32();
            UpdateStatusBar();
            return;
        }

        if (type == "vimMode")
        {
            _vimMode = root.GetProperty("mode").GetString();
            UpdateStatusBar();
            return;
        }

        if (type == "openInternalLink")
        {
            var linkPath = root.GetProperty("path").GetString();
            if (linkPath is not null && _activeDocument is not null)
            {
                // "#見出し" のようなアンカー部分は現状未対応のため取り除く
                var hashIndex = linkPath.IndexOf('#');
                if (hashIndex >= 0)
                {
                    linkPath = linkPath[..hashIndex];
                }

                if (linkPath.Length > 0)
                {
                    var baseDirectory = Path.GetDirectoryName(_activeDocument.FilePath) ?? _rootDirectory;
                    var resolvedPath = Path.GetFullPath(Path.Combine(baseDirectory, linkPath));
                    var rootFullPath = Path.GetFullPath(_rootDirectory);

                    if (resolvedPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(resolvedPath))
                    {
                        _ = RunAndReportErrorsAsync(() => OpenFileAsync(resolvedPath));
                    }
                }
            }
            return;
        }

        if (_activeDocument is null || type is not ("change" or "save"))
        {
            return;
        }

        var text = root.GetProperty("text").GetString() ?? string.Empty;
        _activeDocument.Text = text;
        UpdateHeadings();

        if (type == "save")
        {
            _ = RunAndReportErrorsAsync(() => WriteDocumentAsync(_activeDocument, text));
            return;
        }

        _activeDocument.IsDirty = true;
        UpdateStatusBar();
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

    private static IEnumerable<FileNode> FlattenFiles(FileNode node)
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

    private void OpenFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "開くディレクトリを選択",
            InitialDirectory = _rootDirectory,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _ = RunAndReportErrorsAsync(() => OpenDirectoryAsync(dialog.FolderName));
        }
    }

    /// <summary>
    /// サイドバーヘッダーの「別のディレクトリを開く」から、選択したディレクトリを新たなvaultルートとして開き直す。
    /// vault仮想ホスト(nomu.vault)のマッピング先も合わせて張り替えるため、内部リンク/画像解決の
    /// 基準ディレクトリ(basePath計算・セキュリティ境界チェック)は常に現在の_rootDirectoryと一致する。
    /// </summary>
    private async Task OpenDirectoryAsync(string newRoot)
    {
        await _editorReadyTcs.Task;

        _rootDirectory = newRoot;
        EditorView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VaultVirtualHostName, newRoot, CoreWebView2HostResourceAccessKind.Allow);
        ReloadFileTree();
    }

    private void FileTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FileNode { IsDirectory: false } node)
        {
            _ = RunAndReportErrorsAsync(() => OpenFileAsync(node.FullPath));
        }
    }

    /// <summary>
    /// タブを閉じた後もファイルツリーの選択状態は残ったままのため、閉じたタブと同じファイルを
    /// 再度クリックしても選択に変化がなく SelectedItemChanged が発火しない。既に選択中のノードを
    /// クリックした場合はここで直接開き直す。
    /// </summary>
    private void FileTree_OnPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not System.Windows.Controls.TreeViewItem)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        if (source is System.Windows.Controls.TreeViewItem { DataContext: FileNode { IsDirectory: false } node } &&
            ReferenceEquals(FileTree.SelectedItem, node))
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

        await SaveActiveDocumentIfDirtyAsync();

        _activeDocument = document;
        UpdateStatusBar();
        SetupFileWatcher(document.FilePath);

        _isSwitchingTabProgrammatically = true;
        TabStrip.SelectedItem = document;
        _isSwitchingTabProgrammatically = false;

        await SetEditorContentAsync(document.Text, document.FilePath);
        UpdateHeadings();
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
            await SaveActiveDocumentIfDirtyAsync();
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
            _gitBranch = null;
            _filetype = string.Empty;
            _cursorLine = 1;
            _cursorCol = 1;
            UpdateStatusBar();
            _headings.Clear();
            await EditorView.CoreWebView2.ExecuteScriptAsync("window.__nomuSetContent('', true)");
        }
    }

    private static bool IsMarkdownFile(string path) =>
        Path.GetExtension(path) is ".md" or ".markdown";

    // ステータスラインの filetype 表示用。未知の拡張子は neovim statusline の空欄表示に倣い空文字。
    private static string GetFiletypeLabel(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => "markdown",
        ".txt" => "text",
        ".py" => "python",
        ".c" or ".h" => "c",
        ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" => "cpp",
        ".cs" => "csharp",
        ".java" => "java",
        ".kt" => "kotlin",
        ".go" => "go",
        ".rs" => "rust",
        ".rb" => "ruby",
        ".php" => "php",
        ".swift" => "swift",
        ".js" or ".jsx" => "javascript",
        ".ts" or ".tsx" => "typescript",
        ".json" or ".jsonc" => "json",
        ".html" or ".htm" => "html",
        ".css" or ".scss" or ".less" => "css",
        ".xml" => "xml",
        ".yaml" or ".yml" => "yaml",
        ".toml" => "toml",
        ".ini" or ".cfg" => "ini",
        ".sh" or ".bat" or ".ps1" => "shell",
        ".sql" => "sql",
        _ => string.Empty,
    };

    private async Task SetEditorContentAsync(string text, string filePath)
    {
        var isMarkdown = IsMarkdownFile(filePath) ? "true" : "false";
        var script = $"window.__nomuSetContent({JsonSerializer.Serialize(text)}, {isMarkdown}, {JsonSerializer.Serialize(filePath)})";
        await EditorView.CoreWebView2.ExecuteScriptAsync(script);

        var headContent = await Task.Run(() => GitService.TryGetHeadContent(filePath));
        var baseScript = $"window.__nomuSetGitBase({JsonSerializer.Serialize(headContent)})";
        await EditorView.CoreWebView2.ExecuteScriptAsync(baseScript);

        _gitBranch = await Task.Run(() => GitService.TryGetCurrentBranch(filePath));
        _filetype = GetFiletypeLabel(filePath);
        UpdateStatusBar();

        var directory = Path.GetDirectoryName(filePath) ?? _rootDirectory;
        var basePath = Path.GetRelativePath(_rootDirectory, directory).Replace(Path.DirectorySeparatorChar, '/');
        if (basePath == ".")
        {
            basePath = string.Empty;
        }
        var basePathScript = $"window.__nomuSetBasePath({JsonSerializer.Serialize(basePath)})";
        await EditorView.CoreWebView2.ExecuteScriptAsync(basePathScript);
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

        await SetEditorContentAsync(document.Text, document.FilePath);
        UpdateHeadings();
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
        FavoritesToggleButton.IsChecked = false;
        FileTree.Visibility = Visibility.Visible;
        SearchPanel.Visibility = Visibility.Collapsed;
        FavoritesPanel.Visibility = Visibility.Collapsed;
        OpenFolderButton.Visibility = Visibility.Visible;
        SearchScopeToggleButton.Visibility = Visibility.Collapsed;
        SidebarHeaderText.Text = Path.GetFileName(_rootDirectory.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
            ? name.ToUpperInvariant()
            : "FILES";
    }

    private void SearchToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowSidebar();
        SearchToggleButton.IsChecked = true;
        ExplorerToggleButton.IsChecked = false;
        FavoritesToggleButton.IsChecked = false;
        FileTree.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Visible;
        FavoritesPanel.Visibility = Visibility.Collapsed;
        OpenFolderButton.Visibility = Visibility.Collapsed;
        SearchScopeToggleButton.Visibility = Visibility.Visible;
        SidebarHeaderText.Text = "SEARCH";
        SearchBox.Focus();
    }

    private void FavoritesToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowSidebar();
        FavoritesToggleButton.IsChecked = true;
        ExplorerToggleButton.IsChecked = false;
        SearchToggleButton.IsChecked = false;
        FileTree.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Collapsed;
        OpenFolderButton.Visibility = Visibility.Collapsed;
        SearchScopeToggleButton.Visibility = Visibility.Collapsed;
        FavoritesPanel.Visibility = Visibility.Visible;
        SidebarHeaderText.Text = "FAVORITES";
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_config, _configOverrides) { Owner = this };
        if (window.ShowDialog() != true || window.Result is not { } newConfig)
        {
            return;
        }

        var vimModeChanged = newConfig.Editor.VimMode != _config.Editor.VimMode;

        _config = newConfig;
        ApplyConfig(_config);

        if (vimModeChanged)
        {
            MessageBox.Show("vimモードの変更を反映するには nomsidian を再起動してください。", "nomsidian 設定");
        }
    }

    private void SearchScopeToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        _searchMarkdownOnly = SearchScopeToggleButton.IsChecked == true;
        RunSearch(SearchBox.Text.Trim());
    }

    private IEnumerable<FileNode> SearchScopedFiles() =>
        _searchMarkdownOnly ? _allFiles.Where(f => IsMarkdownFile(f.FullPath)) : _allFiles;

    private void LoadFavorites()
    {
        _favorites.Clear();
        foreach (var path in FavoritesService.Load())
        {
            if (File.Exists(path))
            {
                _favorites.Add(new FileNode { Name = Path.GetFileName(path), FullPath = path, IsDirectory = false });
            }
        }
    }

    private void FileTree_OnContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not System.Windows.Controls.TreeViewItem)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        _contextMenuNode = (source as System.Windows.Controls.TreeViewItem)?.DataContext as FileNode;

        if (_contextMenuNode is null or { IsDirectory: true })
        {
            e.Handled = true;
            return;
        }

        AddToFavoritesMenuItem.Header = $"「{_contextMenuNode.Name}」をお気に入りに追加";
    }

    private void AddToFavoritesMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_contextMenuNode is not { IsDirectory: false } node)
        {
            return;
        }

        if (FavoritesService.Add(node.FullPath) &&
            !_favorites.Any(f => string.Equals(f.FullPath, node.FullPath, StringComparison.OrdinalIgnoreCase)))
        {
            _favorites.Add(node);
        }
    }

    private void FavoritesList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FavoritesList.SelectedItem is FileNode node)
        {
            _ = RunAndReportErrorsAsync(() => OpenFileAsync(node.FullPath));
        }
    }

    private void SearchModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var mode = (SearchMode)Enum.Parse(typeof(SearchMode), (string)((FrameworkElement)sender).Tag);
        _searchMode = mode;

        SearchModeFileNameButton.IsChecked = mode == SearchMode.FileName;
        SearchModeInFileButton.IsChecked = mode == SearchMode.InFile;
        SearchModeCrossFileButton.IsChecked = mode == SearchMode.CrossFile;

        RunSearch(SearchBox.Text.Trim());
        SearchBox.Focus();
    }

    private void SearchBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RunSearch(SearchBox.Text.Trim());
    }

    private void RunSearch(string query)
    {
        switch (_searchMode)
        {
            case SearchMode.FileName:
                RunFileNameSearch(query);
                break;
            case SearchMode.InFile:
                _ = RunAndReportErrorsAsync(() => RunInFileSearchAsync(query));
                break;
            case SearchMode.CrossFile:
                RunCrossFileSearch(query);
                break;
        }
    }

    private void RunFileNameSearch(string query)
    {
        SearchResultsList.ItemsSource = query.Length == 0
            ? Array.Empty<FileNode>()
            : SearchScopedFiles().Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task RunInFileSearchAsync(string query)
    {
        if (query.Length == 0 || _activeDocument is null)
        {
            SearchResultsList.ItemsSource = Array.Empty<SearchMatch>();
            return;
        }

        var script = $"window.__nomuSetInFileSearchQuery({JsonSerializer.Serialize(query)})";
        var resultJson = await EditorView.CoreWebView2.ExecuteScriptAsync(script);
        var raw = JsonSerializer.Deserialize<string>(resultJson) ?? "[]";
        var jsMatches = JsonSerializer.Deserialize<List<JsSearchMatch>>(raw, CaseInsensitiveJsonOptions) ?? new List<JsSearchMatch>();

        var filePath = _activeDocument.FilePath;
        var fileName = _activeDocument.FileName;
        SearchResultsList.ItemsSource = jsMatches.Select(m => new SearchMatch
        {
            FilePath = filePath,
            FileName = fileName,
            Line = m.Line,
            LineText = m.LineText,
            MatchStart = m.MatchStart,
            MatchLength = m.MatchLength,
            From = m.From,
            To = m.To,
        }).ToList();
    }

    private void RunCrossFileSearch(string query)
    {
        _crossFileSearchCts?.Cancel();

        if (query.Length == 0)
        {
            SearchResultsList.ItemsSource = Array.Empty<SearchMatch>();
            return;
        }

        var cts = new CancellationTokenSource();
        _crossFileSearchCts = cts;
        _ = RunAndReportCrossFileSearchAsync(query, cts);
    }

    private async Task RunAndReportCrossFileSearchAsync(string query, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(150, cts.Token);

            var files = SearchScopedFiles().ToList();
            var matches = await Task.Run(() => SearchAllFiles(files, query, cts.Token), cts.Token);

            if (!cts.Token.IsCancellationRequested)
            {
                SearchResultsList.ItemsSource = matches;
            }
        }
        catch (OperationCanceledException)
        {
            // 新しい入力によってキャンセルされた場合は何もしない
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "nomsidian エラー");
        }
        finally
        {
            if (_crossFileSearchCts == cts)
            {
                _crossFileSearchCts = null;
            }
        }
    }

    private static List<SearchMatch> SearchAllFiles(List<FileNode> files, string query, CancellationToken token)
    {
        var results = new List<SearchMatch>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();

            string text;
            try
            {
                text = File.ReadAllText(file.FullPath);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var (lineNumber, lineStart, lineText) in EnumerateLines(text))
            {
                var matchStart = lineText.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (matchStart < 0)
                {
                    continue;
                }

                results.Add(new SearchMatch
                {
                    FilePath = file.FullPath,
                    FileName = file.Name,
                    Line = lineNumber,
                    LineText = lineText,
                    MatchStart = matchStart,
                    MatchLength = query.Length,
                    From = lineStart + matchStart,
                    To = lineStart + matchStart + query.Length,
                });
            }
        }

        return results;
    }

    // CM6のdocは生テキストの文字インデックスをそのまま位置として扱うため、
    // ここで計算する行頭オフセットはエディタ側の from/to とそのまま対応する。
    private static IEnumerable<(int LineNumber, int LineStart, string LineText)> EnumerateLines(string text)
    {
        var offset = 0;
        var lineNumber = 1;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            yield return (lineNumber, offset, line);
            offset += rawLine.Length + 1;
            lineNumber++;
        }
    }

    private void SearchResultsList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        switch (SearchResultsList.SelectedItem)
        {
            case FileNode node:
                _ = RunAndReportErrorsAsync(() => OpenFileAsync(node.FullPath));
                break;
            case SearchMatch match:
                _ = RunAndReportErrorsAsync(() => OpenSearchMatchAsync(match));
                break;
        }
    }

    private async Task OpenSearchMatchAsync(SearchMatch match)
    {
        if (_activeDocument?.FilePath != match.FilePath)
        {
            await OpenFileAsync(match.FilePath);
        }

        await EditorView.CoreWebView2.ExecuteScriptAsync($"window.__nomuGotoRange({match.From}, {match.To})");
        EditorView.Focus();
    }

    private sealed class JsSearchMatch
    {
        public int From { get; set; }
        public int To { get; set; }
        public int Line { get; set; }
        public string LineText { get; set; } = string.Empty;
        public int MatchStart { get; set; }
        public int MatchLength { get; set; }
    }

    private static readonly System.Text.RegularExpressions.Regex HeadingRegex =
        new(@"^(#{1,6})\s+(.+?)\s*#*\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void UpdateHeadings()
    {
        _headings.Clear();

        if (_activeDocument is null || !IsMarkdownFile(_activeDocument.FilePath))
        {
            return;
        }

        foreach (var (lineNumber, lineStart, lineText) in EnumerateLines(_activeDocument.Text))
        {
            var match = HeadingRegex.Match(lineText);
            if (!match.Success)
            {
                continue;
            }

            _headings.Add(new HeadingItem
            {
                Level = match.Groups[1].Value.Length,
                Text = match.Groups[2].Value,
                Line = lineNumber,
                From = lineStart,
            });
        }
    }

    private void OutlineToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        _outlineVisible = !_outlineVisible;

        if (_outlineVisible)
        {
            OutlineColumn.Width = new GridLength(_lastOutlineWidth);
            OutlineColumn.MinWidth = 140;
        }
        else
        {
            _lastOutlineWidth = OutlineColumn.ActualWidth > 0 ? OutlineColumn.ActualWidth : _lastOutlineWidth;
            OutlineColumn.MinWidth = 0;
            OutlineColumn.Width = new GridLength(0);
        }

        OutlineSplitterBorder.Visibility = _outlineVisible ? Visibility.Visible : Visibility.Collapsed;
        OutlineSplitter.Visibility = _outlineVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OutlineList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (OutlineList.SelectedItem is not HeadingItem heading)
        {
            return;
        }

        _ = RunAndReportErrorsAsync(async () =>
        {
            await EditorView.CoreWebView2.ExecuteScriptAsync($"window.__nomuGotoRange({heading.From}, {heading.From})");
            EditorView.Focus();
        });
    }

    private void UpdateStatusBar()
    {
        StatusPathText.Text = _activeDocument is null
            ? "ファイルが選択されていません"
            : (_activeDocument.IsDirty ? "* " : string.Empty) + Path.GetRelativePath(_rootDirectory, _activeDocument.FilePath);

        BranchText.Text = _gitBranch is null ? string.Empty : $"\U0001F33F {_gitBranch}";
        BranchText.Visibility = _gitBranch is null ? Visibility.Collapsed : Visibility.Visible;

        FiletypeText.Text = _filetype;
        CursorPositionText.Text = _activeDocument is null ? string.Empty : $"{_cursorLine}:{_cursorCol}";

        if (_config.Editor.VimMode && _vimMode is not null)
        {
            ModeBadge.Visibility = Visibility.Visible;
            ModeBadgeText.Text = _vimMode.ToUpperInvariant();
            ModeBadge.Background = new SolidColorBrush(ModeColor(_vimMode));
        }
        else
        {
            ModeBadge.Visibility = Visibility.Collapsed;
        }
    }

    private Color ModeColor(string vimMode)
    {
        var hex = vimMode.Split(' ')[0] switch
        {
            "normal" => _config.Statusline.ModeNormal,
            "insert" => _config.Statusline.ModeInsert,
            "visual" => _config.Statusline.ModeVisual,
            "replace" => _config.Statusline.ModeReplace,
            _ => _config.Statusline.ModeNormal,
        };
        return (Color)ColorConverter.ConvertFromString(hex);
    }

    private async Task SaveDocumentAsync(OpenDocument document)
    {
        var resultJson = await EditorView.CoreWebView2.ExecuteScriptAsync("window.__nomuGetContent()");
        var text = JsonSerializer.Deserialize<string>(resultJson) ?? document.Text;
        await WriteDocumentAsync(document, text);
    }

    private Task WriteDocumentAsync(OpenDocument document, string text)
    {
        _ignoreWatcherUntilUtc = DateTime.UtcNow.Add(OwnWriteIgnoreWindow);
        FileService.WriteAllText(document.FilePath, text);
        document.Text = text;
        document.IsDirty = false;
        UpdateStatusBar();
        return Task.CompletedTask;
    }

    private bool _closeConfirmed;

    private void MainWindow_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed || _activeDocument?.IsDirty != true)
        {
            return;
        }

        e.Cancel = true;
        _ = CloseAfterFlushAsync();
    }

    private async Task CloseAfterFlushAsync()
    {
        await RunAndReportErrorsAsync(SaveActiveDocumentIfDirtyAsync);
        _fileWatcher?.Dispose();
        _closeConfirmed = true;
        Close();
    }
}
