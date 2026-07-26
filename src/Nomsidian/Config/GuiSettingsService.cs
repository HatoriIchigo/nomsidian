using System.IO;
using System.Text.Json;

namespace Nomsidian.Config;

/// <summary>
/// 設定パネル(GUI)が編集する値の保存先。%USERPROFILE%\.nomsidian\settings.json に保存する。
/// nomu.lua(玄人向けのコードとしての設定)とは別レイヤーとして扱い、読み込み時は
/// GUI設定 → nomu.lua の順で重ね掛けする(nomu.luaが明示的に指定した項目が最終的に勝つ)。
/// </summary>
public static class GuiSettingsService
{
    private const string FileName = "settings.json";

    private static string FilePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".nomsidian", FileName);
    }

    public static NomuConfig Load()
    {
        var path = FilePath();
        if (!File.Exists(path))
        {
            return new NomuConfig();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<NomuConfig>(json) ?? new NomuConfig();
        }
        catch (JsonException)
        {
            return new NomuConfig();
        }
    }

    public static void Save(NomuConfig config)
    {
        var path = FilePath();
        var directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
}
