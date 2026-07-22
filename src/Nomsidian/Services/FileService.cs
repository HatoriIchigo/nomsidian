using System.IO;

namespace Nomsidian.Services;

public static class FileService
{
    public static string ReadAllText(string path) => File.ReadAllText(path);

    public static void WriteAllText(string path, string content) => File.WriteAllText(path, content);
}
