using System.Globalization;
using System.IO;
using System.Windows.Data;
using Nomsidian.Services;

namespace Nomsidian;

/// <summary>
/// ファイルツリーの各ノードに、ディレクトリかどうか・拡張子に応じたアイコン(絵文字)を割り当てる
/// (neovimのnvim-web-devicons相当)。Nerd Font等の追加フォント配布が不要な絵文字ベースで実装している。
/// </summary>
public sealed class FileIconConverter : IValueConverter
{
    public static readonly FileIconConverter Instance = new();

    private static readonly Dictionary<string, string> ExtensionIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "\U0001F4DD",
        [".markdown"] = "\U0001F4DD",
        [".py"] = "\U0001F40D",
        [".c"] = "\U0001F535",
        [".h"] = "\U0001F537",
        [".cpp"] = "\U0001F537",
        [".cc"] = "\U0001F537",
        [".cxx"] = "\U0001F537",
        [".hpp"] = "\U0001F537",
        [".hxx"] = "\U0001F537",
        [".cs"] = "\U0001F7E3",
        [".java"] = "☕",
        [".kt"] = "\U0001F7E0",
        [".go"] = "\U0001F439",
        [".rs"] = "\U0001F980",
        [".rb"] = "\U0001F48E",
        [".php"] = "\U0001F418",
        [".swift"] = "\U0001F426",
        [".js"] = "\U0001F7E8",
        [".jsx"] = "⚛️",
        [".ts"] = "\U0001F537",
        [".tsx"] = "⚛️",
        [".json"] = "\U0001F9FE",
        [".jsonc"] = "\U0001F9FE",
        [".html"] = "\U0001F310",
        [".htm"] = "\U0001F310",
        [".css"] = "\U0001F3A8",
        [".scss"] = "\U0001F3A8",
        [".less"] = "\U0001F3A8",
        [".xml"] = "\U0001F3F7️",
        [".yaml"] = "⚙️",
        [".yml"] = "⚙️",
        [".toml"] = "\U0001F527",
        [".ini"] = "\U0001F527",
        [".cfg"] = "\U0001F527",
        [".sh"] = "\U0001F4BB",
        [".bat"] = "\U0001F4BB",
        [".ps1"] = "\U0001F4BB",
        [".sql"] = "\U0001F5C4️",
        [".txt"] = "\U0001F4C4",
        [".png"] = "\U0001F5BC️",
        [".jpg"] = "\U0001F5BC️",
        [".jpeg"] = "\U0001F5BC️",
        [".gif"] = "\U0001F5BC️",
        [".bmp"] = "\U0001F5BC️",
        [".webp"] = "\U0001F5BC️",
        [".svg"] = "\U0001F5BC️",
        [".ico"] = "\U0001F5BC️",
        [".pdf"] = "\U0001F4D5",
    };

    private const string DirectoryIcon = "\U0001F4C1";
    private const string DefaultFileIcon = "\U0001F4C4";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FileNode node)
        {
            return DefaultFileIcon;
        }

        if (node.IsDirectory)
        {
            return DirectoryIcon;
        }

        var extension = Path.GetExtension(node.Name);
        return ExtensionIcons.TryGetValue(extension, out var icon) ? icon : DefaultFileIcon;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
