namespace Nomsidian;

/// <summary>右側アウトラインペインに表示する見出し1件（neovimのaerial相当）。</summary>
public sealed class HeadingItem
{
    public required int Level { get; init; }
    public required string Text { get; init; }
    public required int Line { get; init; }
    public required int From { get; init; }

    public double IndentWidth => (Level - 1) * 14;
}
