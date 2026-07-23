namespace Nomsidian.Config;

public sealed class NomuConfig
{
    public ThemeConfig Theme { get; init; } = ThemeConfig.Default;
    public FontConfig Font { get; init; } = FontConfig.Default;
    public EditorConfig Editor { get; init; } = EditorConfig.Default;
}

public sealed class ThemeConfig
{
    public string EditorBg { get; init; } = "#1e1e1e";
    public string SidebarBg { get; init; } = "#181819";
    public string PanelBg { get; init; } = "#232326";
    public string Border { get; init; } = "#2f2f33";
    public string Text { get; init; } = "#dcddde";
    public string MutedText { get; init; } = "#8a8a90";
    public string Accent { get; init; } = "#8875ff";
    public string AccentMuted { get; init; } = "#4a3f8f";
    public string Hover { get; init; } = "#2a2a30";

    public static ThemeConfig Default { get; } = new();
}

public sealed class FontConfig
{
    public string Family { get; init; } = "Segoe UI";
    public double Size { get; init; } = 12;

    public static FontConfig Default { get; } = new();
}

public sealed class EditorConfig
{
    public bool VimMode { get; init; }

    public static EditorConfig Default { get; } = new();
}
