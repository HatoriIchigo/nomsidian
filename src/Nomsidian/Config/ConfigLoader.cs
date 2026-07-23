using System.IO;
using MoonSharp.Interpreter;

namespace Nomsidian.Config;

/// <summary>
/// nomu.lua の構文/実行時エラーを表す。呼び出し側はメッセージをユーザに提示した上で
/// デフォルト設定にフォールバックする。
/// </summary>
public sealed class ConfigLoadException : Exception
{
    public ConfigLoadException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// nomu.lua (WezTerm の wezterm.lua に倣った設定ファイル) を読み込む。
/// スクリプトはサンドボックス化した MoonSharp 上で実行し、末尾で return したテーブルから
/// 既知のキーだけを取り出して <see cref="NomuConfig"/> にマッピングする。
/// 未知のキー・型不一致は無視してデフォルト値にフォールバックする(将来のキー追加で壊れないように)。
/// </summary>
public static class ConfigLoader
{
    private const string ConfigFileName = "nomu.lua";

    public static NomuConfig Load(string vaultDirectory)
    {
        var path = ResolveConfigPath(vaultDirectory);
        if (path is null)
        {
            return new NomuConfig();
        }

        try
        {
            return LoadFromFile(path);
        }
        catch (Exception ex) when (ex is SyntaxErrorException or ScriptRuntimeException or InterpreterException)
        {
            throw new ConfigLoadException($"設定ファイルの読み込みに失敗しました: {path}\n\n{ex.Message}", ex);
        }
    }

    /// <summary>
    /// vault 直下の nomu.lua を優先し、無ければ %USERPROFILE%\.nomsidian\nomu.lua を見る
    /// (WezTerm がカレント設定→ホームディレクトリの順で探すのに倣った)。
    /// </summary>
    private static string? ResolveConfigPath(string vaultDirectory)
    {
        var vaultLocal = Path.Combine(vaultDirectory, ConfigFileName);
        if (File.Exists(vaultLocal))
        {
            return vaultLocal;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userGlobal = Path.Combine(userProfile, ".nomsidian", ConfigFileName);
        return File.Exists(userGlobal) ? userGlobal : null;
    }

    private static NomuConfig LoadFromFile(string path)
    {
        var script = new Script(CoreModules.Preset_SoftSandbox);
        RegisterNomuApi(script);

        var result = script.DoFile(path);
        var config = result.Type == DataType.Table ? result.Table : null;

        return new NomuConfig
        {
            Theme = ReadTheme(config?.Get("theme") is { Type: DataType.Table } themeValue ? themeValue.Table : null),
            Font = ReadFont(config?.Get("font") is { Type: DataType.Table } fontValue ? fontValue.Table : null),
            Editor = ReadEditor(config?.Get("editor") is { Type: DataType.Table } editorValue ? editorValue.Table : null),
            Statusline = ReadStatusline(config?.Get("statusline") is { Type: DataType.Table } statuslineValue ? statuslineValue.Table : null),
        };
    }

    /// <summary>
    /// スクリプトへ注入するホスト API。今回はテーマ/フォント設定を読み取るだけだが、
    /// 将来のプラグイン機構(イベントフック・キーバインドアクション)を見据えて器だけ用意しておく。
    /// </summary>
    private static void RegisterNomuApi(Script script)
    {
        var nomu = new Table(script);
        nomu["version"] = Program.DisplayVersion();
        // 将来のイベントフック用(ファイルを開いた時/保存前など)。今回は登録を受け付けるだけで発火しない。
        nomu["on"] = DynValue.NewCallback((_, _) => DynValue.Nil);
        // 将来のキーバインド用アクション定数の器。
        nomu["action"] = new Table(script);
        script.Globals["nomu"] = nomu;
    }

    private static ThemeConfig ReadTheme(Table? table)
    {
        var d = ThemeConfig.Default;
        if (table is null)
        {
            return d;
        }

        return new ThemeConfig
        {
            EditorBg = ReadColor(table, "editor_bg", d.EditorBg),
            SidebarBg = ReadColor(table, "sidebar_bg", d.SidebarBg),
            PanelBg = ReadColor(table, "panel_bg", d.PanelBg),
            Border = ReadColor(table, "border", d.Border),
            Text = ReadColor(table, "text", d.Text),
            MutedText = ReadColor(table, "muted_text", d.MutedText),
            Accent = ReadColor(table, "accent", d.Accent),
            AccentMuted = ReadColor(table, "accent_muted", d.AccentMuted),
            Hover = ReadColor(table, "hover", d.Hover),
        };
    }

    private static FontConfig ReadFont(Table? table)
    {
        var d = FontConfig.Default;
        if (table is null)
        {
            return d;
        }

        var family = table.Get("family");
        var size = table.Get("size");

        return new FontConfig
        {
            Family = family.Type == DataType.String && family.String.Length > 0 ? family.String : d.Family,
            Size = size.Type == DataType.Number && size.Number > 0 ? size.Number : d.Size,
        };
    }

    private static EditorConfig ReadEditor(Table? table)
    {
        if (table is null)
        {
            return EditorConfig.Default;
        }

        var vimMode = table.Get("vim_mode");
        return new EditorConfig
        {
            VimMode = vimMode.Type == DataType.Boolean && vimMode.Boolean,
        };
    }

    private static StatuslineConfig ReadStatusline(Table? table)
    {
        var d = StatuslineConfig.Default;
        if (table is null)
        {
            return d;
        }

        var modeColors = table.Get("mode_colors") is { Type: DataType.Table } modeColorsValue ? modeColorsValue.Table : null;
        if (modeColors is null)
        {
            return d;
        }

        return new StatuslineConfig
        {
            ModeNormal = ReadColor(modeColors, "normal", d.ModeNormal),
            ModeInsert = ReadColor(modeColors, "insert", d.ModeInsert),
            ModeVisual = ReadColor(modeColors, "visual", d.ModeVisual),
            ModeReplace = ReadColor(modeColors, "replace", d.ModeReplace),
        };
    }

    private static string ReadColor(Table table, string key, string fallback)
    {
        var value = table.Get(key);
        return value.Type == DataType.String && IsValidHexColor(value.String) ? value.String : fallback;
    }

    private static bool IsValidHexColor(string text)
    {
        if (text.Length is not (7 or 9) || text[0] != '#')
        {
            return false;
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (!Uri.IsHexDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}
