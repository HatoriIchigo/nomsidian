using System.IO;
using System.Text.Json;

namespace Nomsidian.Services;

/// <summary>
/// お気に入りファイルのパス一覧を %USERPROFILE%\.nomsidian\favorites.json に永続化する。
/// vaultをまたいでグローバルに共有する(nomu.lua の探索先と同じディレクトリを使う)。
/// </summary>
public static class FavoritesService
{
    private const string FavoritesFileName = "favorites.json";

    private static string FavoritesFilePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".nomsidian", FavoritesFileName);
    }

    public static List<string> Load()
    {
        var path = FavoritesFilePath();
        if (!File.Exists(path))
        {
            return new List<string>();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    /// <summary>既に登録済みなら何もしない。追加した場合は true を返す。</summary>
    public static bool Add(string filePath)
    {
        var favorites = Load();
        if (favorites.Contains(filePath, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        favorites.Add(filePath);
        Save(favorites);
        return true;
    }

    private static void Save(List<string> favorites)
    {
        var path = FavoritesFilePath();
        var directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(favorites, new JsonSerializerOptions { WriteIndented = true }));
    }
}
