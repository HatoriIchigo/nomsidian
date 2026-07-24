using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nomsidian.Config;

namespace Nomsidian;

/// <summary>
/// GUI設定パネル。%USERPROFILE%\.nomsidian\settings.json に保存する値を編集する。
/// nomu.luaが明示的に指定している項目は<see cref="ConfigOverrides"/>を見て編集不可(グレーアウト)にし、
/// 「nomu.luaで上書き中」であることを表示する(GUIとLuaの二重管理・食い違いを防ぐため)。
/// 左ペインで「外観/エディタ/ステータスライン」のカテゴリを切り替える。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly TextBox _editorBgBox, _sidebarBgBox, _panelBgBox, _borderBox, _textBox, _mutedTextBox, _accentBox, _accentMutedBox, _hoverBox;
    private readonly TextBox _fontFamilyBox, _fontSizeBox;
    private readonly CheckBox _vimModeCheckBox;
    private readonly TextBox _modeNormalBox, _modeInsertBox, _modeVisualBox, _modeReplaceBox;

    public NomuConfig? Result { get; private set; }

    public SettingsWindow(NomuConfig config, ConfigOverrides overrides)
    {
        InitializeComponent();

        AddSectionHeader(AppearancePanel, "テーマ");
        _editorBgBox = AddColorRow(AppearancePanel, "エディタ背景", config.Theme.EditorBg, overrides.Theme.EditorBg);
        _sidebarBgBox = AddColorRow(AppearancePanel, "サイドバー背景", config.Theme.SidebarBg, overrides.Theme.SidebarBg);
        _panelBgBox = AddColorRow(AppearancePanel, "パネル背景(タブ/ステータスバー)", config.Theme.PanelBg, overrides.Theme.PanelBg);
        _borderBox = AddColorRow(AppearancePanel, "境界線", config.Theme.Border, overrides.Theme.Border);
        _textBox = AddColorRow(AppearancePanel, "テキスト", config.Theme.Text, overrides.Theme.Text);
        _mutedTextBox = AddColorRow(AppearancePanel, "補助テキスト", config.Theme.MutedText, overrides.Theme.MutedText);
        _accentBox = AddColorRow(AppearancePanel, "アクセント", config.Theme.Accent, overrides.Theme.Accent);
        _accentMutedBox = AddColorRow(AppearancePanel, "選択ハイライト", config.Theme.AccentMuted, overrides.Theme.AccentMuted);
        _hoverBox = AddColorRow(AppearancePanel, "ホバー", config.Theme.Hover, overrides.Theme.Hover);

        AddSectionHeader(AppearancePanel, "フォント");
        _fontFamilyBox = AddTextRow(AppearancePanel, "フォントファミリー", config.Font.Family, overrides.FontFamily);
        _fontSizeBox = AddTextRow(AppearancePanel, "フォントサイズ", config.Font.Size.ToString(CultureInfo.InvariantCulture), overrides.FontSize);

        AddSectionHeader(EditorPanel, "エディタ");
        _vimModeCheckBox = AddCheckBoxRow(EditorPanel, "vimモードを有効にする", config.Editor.VimMode, overrides.EditorVimMode);

        AddSectionHeader(StatuslinePanel, "ステータスライン (vimモードの色)");
        _modeNormalBox = AddColorRow(StatuslinePanel, "NORMAL", config.Statusline.ModeNormal, overrides.Statusline.ModeNormal);
        _modeInsertBox = AddColorRow(StatuslinePanel, "INSERT", config.Statusline.ModeInsert, overrides.Statusline.ModeInsert);
        _modeVisualBox = AddColorRow(StatuslinePanel, "VISUAL", config.Statusline.ModeVisual, overrides.Statusline.ModeVisual);
        _modeReplaceBox = AddColorRow(StatuslinePanel, "REPLACE", config.Statusline.ModeReplace, overrides.Statusline.ModeReplace);

        // XAMLのIsChecked="True"指定はRadioButtonのGroupName登録タイミングと噛み合わず
        // 意図しない項目が選択された状態で表示されることがあったため、初期状態はここで確定させる。
        NavAppearanceButton.IsChecked = true;
        ShowNavPanel("Appearance");
    }

    private void ShowNavPanel(string tag)
    {
        AppearancePanel.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        EditorPanel.Visibility = tag == "Editor" ? Visibility.Visible : Visibility.Collapsed;
        StatuslinePanel.Visibility = tag == "Statusline" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowNavPanel((string)((FrameworkElement)sender).Tag);
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void AddSectionHeader(Panel target, string text)
    {
        target.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("SectionHeaderStyle") });
    }

    private TextBox AddColorRow(Panel target, string label, string value, bool overridden)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("MutedTextBrush"),
        };
        Grid.SetColumn(labelBlock, 0);

        var swatch = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0),
        };
        ApplySwatchColor(swatch, value);
        Grid.SetColumn(swatch, 1);

        var hexBox = new TextBox { Text = value, IsEnabled = !overridden };
        hexBox.TextChanged += (_, _) => ApplySwatchColor(swatch, hexBox.Text);
        Grid.SetColumn(hexBox, 2);

        row.Children.Add(labelBlock);
        row.Children.Add(swatch);
        row.Children.Add(hexBox);

        AddRowWithOverrideNote(target, row, overridden);
        return hexBox;
    }

    private TextBox AddTextRow(Panel target, string label, string value, bool overridden)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("MutedTextBrush"),
        };
        Grid.SetColumn(labelBlock, 0);

        var textBox = new TextBox { Text = value, IsEnabled = !overridden };
        Grid.SetColumn(textBox, 1);

        row.Children.Add(labelBlock);
        row.Children.Add(textBox);

        AddRowWithOverrideNote(target, row, overridden);
        return textBox;
    }

    private CheckBox AddCheckBoxRow(Panel target, string label, bool value, bool overridden)
    {
        var checkBox = new CheckBox
        {
            Content = label,
            IsChecked = value,
            IsEnabled = !overridden,
            Margin = new Thickness(0, 3, 0, 3),
        };

        AddRowWithOverrideNote(target, checkBox, overridden);
        return checkBox;
    }

    private void AddRowWithOverrideNote(Panel target, UIElement row, bool overridden)
    {
        var container = new StackPanel();
        container.Children.Add(row);
        if (overridden)
        {
            container.Children.Add(new TextBlock
            {
                Text = "nomu.lua で上書き中のため、ここでは編集できません",
                Style = (Style)FindResource("OverrideNoteStyle"),
                Margin = new Thickness(0, 0, 0, 6),
            });
        }
        target.Children.Add(container);
    }

    private static void ApplySwatchColor(Border swatch, string hex)
    {
        swatch.Background = IsValidHexColor(hex)
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex))
            : Brushes.Transparent;
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

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var invalidFields = new List<(TextBox Box, string Label)>
        {
            (_editorBgBox, "エディタ背景"), (_sidebarBgBox, "サイドバー背景"), (_panelBgBox, "パネル背景"),
            (_borderBox, "境界線"), (_textBox, "テキスト"), (_mutedTextBox, "補助テキスト"),
            (_accentBox, "アクセント"), (_accentMutedBox, "選択ハイライト"), (_hoverBox, "ホバー"),
            (_modeNormalBox, "NORMAL"), (_modeInsertBox, "INSERT"), (_modeVisualBox, "VISUAL"), (_modeReplaceBox, "REPLACE"),
        }.Where(f => f.Box.IsEnabled && !IsValidHexColor(f.Box.Text)).ToList();

        if (invalidFields.Count > 0)
        {
            var names = string.Join("、", invalidFields.Select(f => f.Label));
            MessageBox.Show($"色の形式が不正です(#rrggbb形式で入力してください): {names}", "nomsidian 設定");
            return;
        }

        if (_fontSizeBox.IsEnabled && (!double.TryParse(_fontSizeBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var size) || size <= 0))
        {
            MessageBox.Show("フォントサイズは正の数で入力してください。", "nomsidian 設定");
            return;
        }

        Result = new NomuConfig
        {
            Theme = new ThemeConfig
            {
                EditorBg = _editorBgBox.Text,
                SidebarBg = _sidebarBgBox.Text,
                PanelBg = _panelBgBox.Text,
                Border = _borderBox.Text,
                Text = _textBox.Text,
                MutedText = _mutedTextBox.Text,
                Accent = _accentBox.Text,
                AccentMuted = _accentMutedBox.Text,
                Hover = _hoverBox.Text,
            },
            Font = new FontConfig
            {
                Family = _fontFamilyBox.Text,
                Size = double.TryParse(_fontSizeBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedSize) ? parsedSize : 12,
            },
            Editor = new EditorConfig { VimMode = _vimModeCheckBox.IsChecked == true },
            Statusline = new StatuslineConfig
            {
                ModeNormal = _modeNormalBox.Text,
                ModeInsert = _modeInsertBox.Text,
                ModeVisual = _modeVisualBox.Text,
                ModeReplace = _modeReplaceBox.Text,
            },
        };

        GuiSettingsService.Save(Result);
        DialogResult = true;
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
