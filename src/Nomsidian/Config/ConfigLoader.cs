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
/// 設定を2層で読み込む。
/// 1. GUI設定パネル(<see cref="GuiSettingsService"/>、%USERPROFILE%\.nomsidian\settings.json)をベースにする。
/// 2. nomu.lua (WezTerm の wezterm.lua に倣った玄人向けの設定ファイル) で明示的に指定されたキーだけを
///    上書きする。スクリプトはサンドボックス化した MoonSharp 上で実行する。
/// nomu.luaで上書きされたキーは <see cref="ConfigOverrides"/> として返し、GUI設定パネル側で
/// 「nomu.luaが優先されている」ことを示すために使う。
/// 未知のキー・型不一致は無視してベース値にフォールバックする(将来のキー追加で壊れないように)。
/// </summary>
public static class ConfigLoader
{
    private const string ConfigFileName = "nomu.lua";

    public static LoadedConfig Load(string vaultDirectory)
    {
        var baseConfig = GuiSettingsService.Load();

        var path = ResolveConfigPath(vaultDirectory);
        if (path is null)
        {
            return new LoadedConfig { Config = baseConfig, Overrides = ConfigOverrides.None };
        }

        try
        {
            return LoadFromFile(path, baseConfig);
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

    private static LoadedConfig LoadFromFile(string path, NomuConfig baseConfig)
    {
        var script = new Script(CoreModules.Preset_SoftSandbox);
        RegisterNomuApi(script);

        var result = script.DoFile(path);
        var config = result.Type == DataType.Table ? result.Table : null;

        var themeTable = config?.Get("theme") is { Type: DataType.Table } themeValue ? themeValue.Table : null;
        var fontTable = config?.Get("font") is { Type: DataType.Table } fontValue ? fontValue.Table : null;
        var editorTable = config?.Get("editor") is { Type: DataType.Table } editorValue ? editorValue.Table : null;
        var statuslineTable = config?.Get("statusline") is { Type: DataType.Table } statuslineValue ? statuslineValue.Table : null;
        var modeColorsTable = statuslineTable?.Get("mode_colors") is { Type: DataType.Table } modeColorsValue ? modeColorsValue.Table : null;

        var (theme, themeOverrides) = ReadTheme(themeTable, baseConfig.Theme);
        var (font, fontFamilyOverridden, fontSizeOverridden) = ReadFont(fontTable, baseConfig.Font);
        var (editor, vimModeOverridden) = ReadEditor(editorTable, baseConfig.Editor);
        var (statusline, statuslineOverrides) = ReadStatusline(modeColorsTable, baseConfig.Statusline);

        return new LoadedConfig
        {
            Config = new NomuConfig { Theme = theme, Font = font, Editor = editor, Statusline = statusline },
            Overrides = new ConfigOverrides
            {
                Theme = themeOverrides,
                FontFamily = fontFamilyOverridden,
                FontSize = fontSizeOverridden,
                EditorVimMode = vimModeOverridden,
                Statusline = statuslineOverrides,
            },
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

    private static (ThemeConfig, ThemeOverrides) ReadTheme(Table? table, ThemeConfig baseTheme)
    {
        if (table is null)
        {
            return (baseTheme, new ThemeOverrides());
        }

        var (editorBg, editorBgOv) = ReadColor(table, "editor_bg", baseTheme.EditorBg);
        var (sidebarBg, sidebarBgOv) = ReadColor(table, "sidebar_bg", baseTheme.SidebarBg);
        var (panelBg, panelBgOv) = ReadColor(table, "panel_bg", baseTheme.PanelBg);
        var (border, borderOv) = ReadColor(table, "border", baseTheme.Border);
        var (text, textOv) = ReadColor(table, "text", baseTheme.Text);
        var (mutedText, mutedTextOv) = ReadColor(table, "muted_text", baseTheme.MutedText);
        var (accent, accentOv) = ReadColor(table, "accent", baseTheme.Accent);
        var (accentMuted, accentMutedOv) = ReadColor(table, "accent_muted", baseTheme.AccentMuted);
        var (hover, hoverOv) = ReadColor(table, "hover", baseTheme.Hover);

        return (
            new ThemeConfig
            {
                EditorBg = editorBg,
                SidebarBg = sidebarBg,
                PanelBg = panelBg,
                Border = border,
                Text = text,
                MutedText = mutedText,
                Accent = accent,
                AccentMuted = accentMuted,
                Hover = hover,
            },
            new ThemeOverrides
            {
                EditorBg = editorBgOv,
                SidebarBg = sidebarBgOv,
                PanelBg = panelBgOv,
                Border = borderOv,
                Text = textOv,
                MutedText = mutedTextOv,
                Accent = accentOv,
                AccentMuted = accentMutedOv,
                Hover = hoverOv,
            });
    }

    private static (FontConfig, bool familyOverridden, bool sizeOverridden) ReadFont(Table? table, FontConfig baseFont)
    {
        if (table is null)
        {
            return (baseFont, false, false);
        }

        var family = table.Get("family");
        var size = table.Get("size");

        var familyOverridden = family.Type == DataType.String && family.String.Length > 0;
        var sizeOverridden = size.Type == DataType.Number && size.Number > 0;

        return (
            new FontConfig
            {
                Family = familyOverridden ? family.String : baseFont.Family,
                Size = sizeOverridden ? size.Number : baseFont.Size,
            },
            familyOverridden,
            sizeOverridden);
    }

    private static (EditorConfig, bool vimModeOverridden) ReadEditor(Table? table, EditorConfig baseEditor)
    {
        if (table is null)
        {
            return (baseEditor, false);
        }

        var vimMode = table.Get("vim_mode");
        var overridden = vimMode.Type == DataType.Boolean;

        return (
            new EditorConfig { VimMode = overridden ? vimMode.Boolean : baseEditor.VimMode },
            overridden);
    }

    private static (StatuslineConfig, StatuslineOverrides) ReadStatusline(Table? modeColors, StatuslineConfig baseStatusline)
    {
        if (modeColors is null)
        {
            return (baseStatusline, new StatuslineOverrides());
        }

        var (normal, normalOv) = ReadColor(modeColors, "normal", baseStatusline.ModeNormal);
        var (insert, insertOv) = ReadColor(modeColors, "insert", baseStatusline.ModeInsert);
        var (visual, visualOv) = ReadColor(modeColors, "visual", baseStatusline.ModeVisual);
        var (replace, replaceOv) = ReadColor(modeColors, "replace", baseStatusline.ModeReplace);

        return (
            new StatuslineConfig { ModeNormal = normal, ModeInsert = insert, ModeVisual = visual, ModeReplace = replace },
            new StatuslineOverrides { ModeNormal = normalOv, ModeInsert = insertOv, ModeVisual = visualOv, ModeReplace = replaceOv });
    }

    private static (string value, bool overridden) ReadColor(Table table, string key, string fallback)
    {
        var value = table.Get(key);
        return value.Type == DataType.String && IsValidHexColor(value.String)
            ? (value.String, true)
            : (fallback, false);
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
