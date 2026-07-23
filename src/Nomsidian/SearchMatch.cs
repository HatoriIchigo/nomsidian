namespace Nomsidian;

/// <summary>ファイル内検索・ファイル横断検索の1件のマッチ結果。</summary>
public sealed class SearchMatch
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required int Line { get; init; }
    public required string LineText { get; init; }
    public required int MatchStart { get; init; }
    public required int MatchLength { get; init; }
    public required int From { get; init; }
    public required int To { get; init; }
}
