using System.IO;

namespace Nomsidian.Services;

public sealed class FileNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public List<FileNode> Children { get; } = new();
}

public static class DirectoryService
{
    private static readonly HashSet<string> TextFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt",
        ".py", ".c", ".h", ".cpp", ".cc", ".cxx", ".hpp", ".hxx",
        ".cs", ".java", ".kt", ".go", ".rs", ".rb", ".php", ".swift",
        ".js", ".jsx", ".ts", ".tsx", ".json", ".jsonc",
        ".html", ".htm", ".css", ".scss", ".less",
        ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg",
        ".sh", ".ps1", ".bat", ".sql",
    };

    private static readonly HashSet<string> ImageFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico",
    };

    private static readonly HashSet<string> PdfFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
    };

    public static bool IsTextFile(string path) => TextFileExtensions.Contains(Path.GetExtension(path)) || IsDotfile(path);

    /// <summary>
    /// .gitignore や .editorconfig のように、ファイル名がドットで始まり他に拡張子を持たないファイル。
    /// Path.GetExtension はこれらをファイル名全体として返すため、別途テキスト扱いにする。
    /// </summary>
    private static bool IsDotfile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 1 && name[0] == '.' && name.IndexOf('.', 1) < 0;
    }

    public static bool IsImageFile(string path) => ImageFileExtensions.Contains(Path.GetExtension(path));

    public static bool IsPdfFile(string path) => PdfFileExtensions.Contains(Path.GetExtension(path));

    public static FileNode BuildTree(string rootDirectory)
    {
        var root = new FileNode
        {
            Name = Path.GetFileName(rootDirectory.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
                ? name
                : rootDirectory,
            FullPath = rootDirectory,
            IsDirectory = true,
        };
        PopulateChildren(root, rootDirectory);
        return root;
    }

    private static void PopulateChildren(FileNode parent, string directory)
    {
        List<string> subdirectories;
        List<string> files;
        try
        {
            subdirectories = Directory.EnumerateDirectories(directory).OrderBy(d => d).ToList();
            files = Directory.EnumerateFiles(directory).OrderBy(f => f).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            // AppData\Local\Application Data 等、権限が拒否されたジャンクションはスキップする
            return;
        }
        catch (IOException)
        {
            return;
        }

        foreach (var dir in subdirectories)
        {
            var dirNode = new FileNode
            {
                Name = Path.GetFileName(dir),
                FullPath = dir,
                IsDirectory = true,
            };
            PopulateChildren(dirNode, dir);
            if (dirNode.Children.Count > 0)
            {
                parent.Children.Add(dirNode);
            }
        }

        foreach (var file in files)
        {
            parent.Children.Add(new FileNode
            {
                Name = Path.GetFileName(file),
                FullPath = file,
                IsDirectory = false,
            });
        }
    }
}
