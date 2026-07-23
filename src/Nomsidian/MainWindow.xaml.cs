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

    private readonly string _rootDirectory;
    private readonly NomuConfig _config;
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
    private CancellationTokenSource? _crossFileSearchCts;

    public MainWindow(string rootDirectory, NomuConfig config)
    {
        InitializeComponent();
        ApplyConfig(config);

        _rootDirectory = rootDirectory;
        _config = config;
        _externalChangeTimer = new DispatcherTimer { Interval = ExternalChangeDebounce };
        _externalChangeTimer.Tick += ExternalChangeTimer_OnTick;
        TabStrip.ItemsSource = _openDocuments;
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

    private void FileTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FileNode { IsDirectory: false } node)
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
            UpdateStatusBar();
            await EditorView.CoreWebView2.ExecuteScriptAsync("window.__nomuSetContent('', true)");
        }
    }

    private static bool IsMarkdownFile(string path) =>
        Path.GetExtension(path) is ".md" or ".markdown";

    private async Task SetEditorContentAsync(string text, string filePath)
    {
        var isMarkdown = IsMarkdownFile(filePath) ? "true" : "false";
        var script = $"window.__nomuSetContent({JsonSerializer.Serialize(text)}, {isMarkdown}, {JsonSerializer.Serialize(filePath)})";
        await EditorView.CoreWebView2.ExecuteScriptAsync(script);

        var headContent = await Task.Run(() => GitService.TryGetHeadContent(filePath));
        var baseScript = $"window.__nomuSetGitBase({JsonSerializer.Serialize(headContent)})";
        await EditorView.CoreWebView2.ExecuteScriptAsync(baseScript);

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
            : _allFiles.Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
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
        var jsMatches = JsonSerializer.Deserialize<List<JsSearchMatch>>(raw) ?? new List<JsSearchMatch>();

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

            var files = _allFiles.ToList();
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

    private void UpdateStatusBar()
    {
        StatusPathText.Text = _activeDocument is null
            ? "ファイルが選択されていません"
            : (_activeDocument.IsDirty ? "* " : string.Empty) + Path.GetRelativePath(_rootDirectory, _activeDocument.FilePath);
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
