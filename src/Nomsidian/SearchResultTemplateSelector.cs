using System.Windows;
using System.Windows.Controls;
using Nomsidian.Services;

namespace Nomsidian;

/// <summary>検索結果リストの項目型(FileName検索のFileNode / InFile・CrossFile検索のSearchMatch)に応じてテンプレートを出し分ける。</summary>
public sealed class SearchResultTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FileNameTemplate { get; set; }
    public DataTemplate? MatchTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item switch
        {
            FileNode => FileNameTemplate,
            SearchMatch => MatchTemplate,
            _ => base.SelectTemplate(item, container),
        };
    }
}
