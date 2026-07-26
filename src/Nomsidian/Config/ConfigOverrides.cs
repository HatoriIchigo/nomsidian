namespace Nomsidian.Config;

public sealed class ThemeOverrides
{
    public bool EditorBg { get; init; }
    public bool SidebarBg { get; init; }
    public bool PanelBg { get; init; }
    public bool Border { get; init; }
    public bool Text { get; init; }
    public bool MutedText { get; init; }
    public bool Accent { get; init; }
    public bool AccentMuted { get; init; }
    public bool Hover { get; init; }
}

public sealed class StatuslineOverrides
{
    public bool ModeNormal { get; init; }
    public bool ModeInsert { get; init; }
    public bool ModeVisual { get; init; }
    public bool ModeReplace { get; init; }
}

/// <summary>
/// nomu.luaで明示的に指定されている項目(=GUI設定パネルより優先される項目)。
/// GUI側でその項目をグレーアウトし、「nomu.luaで上書き中」であることを示すために使う。
/// </summary>
public sealed class ConfigOverrides
{
    public ThemeOverrides Theme { get; init; } = new();
    public bool FontFamily { get; init; }
    public bool FontSize { get; init; }
    public bool EditorVimMode { get; init; }
    public StatuslineOverrides Statusline { get; init; } = new();

    public static ConfigOverrides None { get; } = new();
}

/// <summary>ConfigLoader.Loadの戻り値。実際に適用する設定と、そのうちnomu.luaが上書きした項目の両方を返す。</summary>
public sealed class LoadedConfig
{
    public required NomuConfig Config { get; init; }
    public required ConfigOverrides Overrides { get; init; }
}
