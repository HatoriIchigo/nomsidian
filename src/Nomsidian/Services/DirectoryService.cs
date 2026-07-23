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
        foreach (var dir in Directory.EnumerateDirectories(directory).OrderBy(d => d))
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

        foreach (var file in Directory.EnumerateFiles(directory)
                     .Where(f => TextFileExtensions.Contains(Path.GetExtension(f)))
                     .OrderBy(f => f))
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
