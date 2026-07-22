using System.IO;

namespace Nomsidian.Services;

public sealed class MarkdownFileNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public List<MarkdownFileNode> Children { get; } = new();
}

public static class DirectoryService
{
    public static MarkdownFileNode BuildTree(string rootDirectory)
    {
        var root = new MarkdownFileNode
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

    private static void PopulateChildren(MarkdownFileNode parent, string directory)
    {
        foreach (var dir in Directory.EnumerateDirectories(directory).OrderBy(d => d))
        {
            var dirNode = new MarkdownFileNode
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

        foreach (var file in Directory.EnumerateFiles(directory, "*.md").OrderBy(f => f))
        {
            parent.Children.Add(new MarkdownFileNode
            {
                Name = Path.GetFileName(file),
                FullPath = file,
                IsDirectory = false,
            });
        }
    }
}
