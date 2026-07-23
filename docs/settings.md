# nomsidian 設定ファイル（`nomu.lua`）

nomsidian は Lua で書いた設定ファイル `nomu.lua` で、見た目（配色）とフォントをカスタマイズできる。書き方は [WezTerm](https://wezfurlong.org/wezterm/) の `wezterm.lua` に倣っている。

## 置き場所

起動時に以下の順で探し、最初に見つかったものを使う。

1. 開いている vault ディレクトリ直下の `nomu.lua`（そのvault専用の設定にしたい場合）
2. `%USERPROFILE%\.nomsidian\nomu.lua`（どのvaultを開いても使う共通設定にしたい場合）
3. どちらも無ければ、既定値（ダークテーマ）で起動する

## 書き方

スクリプトの末尾で設定テーブルを `return` する。

```lua
local config = {}

config.theme = {
    editor_bg = "#12141a",
    sidebar_bg = "#0e1016",
    panel_bg = "#181b22",
    border = "#2a2e3a",
    text = "#e3e6ee",
    muted_text = "#7d8494",
    accent = "#57c7ff",
    accent_muted = "#2c5570",
    hover = "#232733",
}

config.font = {
    family = "Yu Gothic UI",
    size = 12,
}

return config
```

動作確認用の実例が `sample/nomu.lua` にある。

## 設定できる項目

### `config.theme`（配色。値は `#rrggbb` / `#aarrggbb` の16進数文字列）

| キー | 説明 | 既定値 |
|---|---|---|
| `editor_bg` | ウィンドウ全体の背景 | `#1e1e1e` |
| `sidebar_bg` | 左サイドバー（ファイル一覧）の背景 | `#181819` |
| `panel_bg` | タイトルバー・タブバー・ステータスバーの背景 | `#232326` |
| `border` | パネル間の境界線 | `#2f2f33` |
| `text` | 通常のテキスト色 | `#dcddde` |
| `muted_text` | 補助的なテキスト色（ファイル名以外のラベルなど） | `#8a8a90` |
| `accent` | アクセントカラー（アクティビティバーの選択インジケータなど） | `#8875ff` |
| `accent_muted` | 選択中のファイル/タブのハイライト背景 | `#4a3f8f` |
| `hover` | マウスホバー時のハイライト背景 | `#2a2a30` |

キーを省略した項目は既定値のまま使われる。一部だけ書き換えたい場合は、変えたいキーだけ `config.theme` に書けばよい。

### `config.font`

| キー | 説明 | 既定値 |
|---|---|---|
| `family` | フォントファミリー名 | `Segoe UI` |
| `size` | フォントサイズ（pt） | `12` |

### `config.statusline.mode_colors`（`config.editor.vim_mode = true` の時のみ使う、モードバッジの色）

| キー | 説明 | 既定値 |
|---|---|---|
| `normal` | ノーマルモード | `#4caf50` |
| `insert` | インサートモード | `#4a90d9` |
| `visual` | ビジュアルモード | `#b388ff` |
| `replace` | 置換モード | `#e06c75` |

## 反映される範囲

`config.theme` / `config.font` は、サイドバー・タブバー・ステータスバー・ファイル一覧など **WPFシェル側の見た目**に反映される。

エディタ本文（Markdownを編集する中央の領域）は CodeMirror6 の内蔵テーマを使っており、現時点では `nomu.lua` のテーマ設定と連動しない（今後の拡張候補）。

## エラー時の挙動

`nomu.lua` に構文エラーや実行時エラーがあっても、アプリの起動は失敗しない。エラー内容をダイアログで表示したうえで、既定値のまま起動を続ける。

## サンドボックス

`nomu.lua` はサンドボックス化された Lua 環境で実行され、`io` や `os` などファイル操作・外部プロセス起動を伴う標準ライブラリは使えない。

## 将来の拡張予定（現状はプレースホルダー）

グローバルの `nomu` テーブルには以下も用意されているが、`nomu.version` 以外は今のところ器だけで実際には動かない。

- `nomu.version` — nomsidian のバージョン文字列（これは参照できる）
- `nomu.on(event, fn)` — イベントフック。登録は受け付けるが、現時点ではどのイベントも発火しない
- `nomu.action` — キーバインド用アクション定数の器。現時点では空テーブル
