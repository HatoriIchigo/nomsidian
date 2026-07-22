using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Nomsidian.Services;

namespace Nomsidian;

public partial class MainWindow : Window
{
    private const string VirtualHostName = "nomu.local";

    private readonly string _rootDirectory;
    private readonly TaskCompletionSource _editorReadyTcs = new();
    private string? _currentFilePath;
    private string _currentText = string.Empty;
    private bool _isDirty;

    public MainWindow(string rootDirectory)
    {
        InitializeComponent();

        _rootDirectory = rootDirectory;
        Loaded += async (_, _) => await RunAndReportErrorsAsync(InitializeEditorAsync);
        ReloadFileTree();
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
    }

    private void ReloadFileTree()
    {
        var root = DirectoryService.BuildTree(_rootDirectory);
        FileTree.ItemsSource = root.Children;
        Title = $"nomsidian - {_rootDirectory}";
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

        _currentText = FileService.ReadAllText(path);
        _currentFilePath = path;
        _isDirty = false;
        UpdateStatusBar();

        var script = $"window.__nomuSetContent({JsonSerializer.Serialize(_currentText)})";
        await EditorView.CoreWebView2.ExecuteScriptAsync(script);
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
}
