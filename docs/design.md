# nomsidian 設計書

## 概要

nomsidian は、Obsidian のような見た目・操作感を持つ Markdown プレビューアプリケーションである。
C# (.NET) で実装し、Windows ローカル環境で動作するデスクトップアプリとして提供する。

## ゴール（v1）

- CLI コマンド `nomu` から起動できる
- 指定したディレクトリ内の Markdown ファイルを一覧表示し、選択したファイルを編集できる
- Obsidian のライブプレビューのように、エディタとプレビューを分割せず、1つの編集画面内で書式（見出し・太字/斜体・リスト・タスクリスト・テーブル等）をその場で反映しながら編集できる
- ローカルファイルシステム上の Markdown ファイルを開く／保存する

## Non-Goals（v1では対応しない）

- ファイル間リンク（`[[WikiLink]]`）やバックリンク機能、グラフビュー
- プラグイン機構、テーマのカスタマイズ
- クラウド同期・サーバー連携（完全ローカル動作のみ）
- 複数ファイルの同時編集（タブ機能）。v1では一覧から選んだ1ファイルのみを編集対象とする

これらは将来拡張の候補として「今後の拡張」に記載する。

## CLI 仕様

`nomu` コマンドで起動する。裏側では WPF アプリを起動するランチャー（コンソール向けエントリポイント）として振る舞う。

| コマンド | 動作 |
|---|---|
| `nomu .` | カレントディレクトリを開く |
| `nomu <dir>` | 指定したディレクトリを開く（相対パス／絶対パス両対応） |
| `nomu --version` | バージョン番号を標準出力に表示して終了する（GUIは起動しない） |
| `nomu`（引数なし） | 使い方を表示するか、あるいは直近に開いたディレクトリを開く（要検討・v1では前者を採用） |

引数解析の方針:

- 第1引数がディレクトリパスとして存在しない場合、またはディレクトリでない場合はエラーメッセージを表示して終了する（GUIは起動しない）
- `--version` は他の引数より優先し、指定があれば即座にバージョンを表示して終了する

## 動作環境

- OS: Windows 10/11
- ランタイム: .NET 10（このマシンにインストール済みの SDK/ランタイムに合わせる。将来 .NET 8 LTS 環境で動かす場合は `net8.0-windows` に変更する）
- 配布形態: ローカル実行の自己完結型デスクトップアプリ（ネットワーク通信なし）
- ビルド時のみ Node.js（CodeMirror6 エディタ本体を esbuild でバンドルするため）。実行時に Node.js は不要

## 技術スタック

| 領域 | 採用technology | 理由 |
|---|---|---|
| 言語 / ランタイム | C# / .NET 10 | プロジェクト方針（CLAUDE.md）に準拠。ローカル実機の SDK に合わせて選定 |
| UI フレームワーク | WPF | .NET上で最も資料が豊富、XAMLでリッチなUIを組みやすい。Windowsローカル利用が前提のため十分 |
| エディタ本体 | CodeMirror 6（Obsidian と同じ核となるエディタエンジン） + WebView2 | Obsidian のライブプレビュー（書式を残したまま生Markdownを隠す・その場で反映する編集体験）を実現するには、装飾やインライン差し替えの仕組みが必要。AvalonEditで自作するより、CM6のDecoration APIを使う方が実装が早い。WebView2でCM6を動かすことでWPFアプリに埋め込む |
| ビルドツール | Node.js + esbuild（`editor-src/`） | CM6一式を単一の `bundle.js` に事前ビルドし、実行時はNode.js不要にするため |

`Markdig` や `AvalonEdit` は当初案では採用していたが、エディタとプレビューを統合したライブプレビュー方式に変更したため不採用となった（CM6側でMarkdownパース・装飾・レンダリングを一括して行うため）。

## アーキテクチャ

```
CLI引数 (nomu <dir> / nomu . / nomu --version)
        |
        v
Program.cs（エントリポイント／引数解析）
        |
        v
+--------------------------------------------------------------+
|                        MainWindow (WPF)                      |
| +-----------+  +-------------------------------------------+ |
| | File List |  |   WebView2 (EditorView)                    | |
| | Pane      |  |   editor.html + bundle.js (CodeMirror6)    | |
| | 指定dir配下|  |   Markdownの生テキストを保持しつつ、       | |
| | の.md一覧 |  |   見出し/太字/リスト/タスク/テーブル等を    | |
| |           |  |   その場で装飾描画するライブプレビュー      | |
| +-----------+  +-------------------------------------------+ |
|      |                    ^          |                        |
|      v                    |          v                        |
| 選択でファイル読込   ExecuteScriptAsync   WebMessageReceived    |
|      |               (__nomuSetContent)  (変更をpostMessageで通知)|
|      v                                                        |
| DirectoryService (System.IO)                                  |
| - 指定ディレクトリ配下の *.md を再帰的に列挙する               |
+--------------------------------------------------------------+
        |
        v
FileService (System.IO)
- ファイルを開く／保存する
```

### CodeMirror6 エディタ本体（editor-src/）

- `editor-src/src/main.js` に CM6 のセットアップと自前のライブプレビュー装飾ロジックを実装する
- `@codemirror/lang-markdown` + `@lezer/markdown`（GFM拡張: テーブル・タスクリスト・取り消し線）で構文木を取得し、`ViewPlugin` でカーソル位置に応じた装飾（Decoration）を毎回再計算する
  - 見出し（ATXHeading1〜6）: 行装飾でフォントサイズを拡大し、`#`＋直後の空白をカーソルがその行に当たっていない時だけ非表示にする
  - 太字/斜体/取り消し線: マーク装飾でスタイルを当て、カーソルが当たっていない時だけ `*`/`_`/`~~` を非表示にする
  - 箇条書き: `-`/`*`/`+` マーカーを、カーソルがその行に当たっていない時だけ `•` のウィジェットに差し替える（カーソルがある行では生の `-` が見える。見出しと同じ考え方）
  - タスクリスト: `[ ]`/`[x]` を実際にクリックできる `<input type="checkbox">` ウィジェットに常時差し替える
  - テーブル: カーソルがテーブル範囲外にある時、構文木から行/セルの位置を取得し実際の `<table>` 要素（ヘッダー太字・罫線付き）に差し替える。セルをクリックするとそのセルの生テキスト位置にカーソルを移動し、テーブルは自動的に解除されて生のパイプ記法を直接編集できる（カーソルがテーブル内に入ると `selectionOverlaps` が真になり、次回の装飾再計算でウィジェットが外れる）
- `esbuild` で `bundle.js` に単一ファイル化し、`src/Nomsidian/Assets/webui/` にコピーして WPF プロジェクトの `Content` として同梱する
- ビルドコマンド: `cd editor-src && npx esbuild src/main.js --bundle --minify --outfile="../Assets/webui/bundle.js"`

### C# ⇔ JavaScript のブリッジ

- C# → JS: `CoreWebView2.ExecuteScriptAsync("window.__nomuSetContent(...)")` でファイル内容を注入する
- JS → C#: エディタの内容が変わるたびに `window.chrome.webview.postMessage(JSON.stringify({type:"change", text}))` で通知し、C#側は `WebMessageReceived` で受け取ってダーティフラグとステータスバーを更新する
- 保存時は `window.__nomuGetContent()` を `ExecuteScriptAsync` で呼び出し、最新の内容を取得してから書き込む
- JS側の例外は `window.onerror` / `unhandledrejection` / `console.error` をフックして `{type:"error", ...}` メッセージとしてC#に転送し、`MessageBox` に表示する（DevToolsを開かなくても不具合に気づけるようにするため）

### 既知の落とし穴（実装時にハマった点）

- WPF SDK（net10.0-windows）の XAML マークアップコンパイラは、`App.xaml` を `ApplicationDefinition` として持つ限り `Main` を強制生成し、`DisableXamlGeneratedMain` 等のフラグは効かなかった。カスタムの `Program.cs` に `Main` を持たせるため、`App.xaml` 自体を削除し `App` をコード継承のみのクラスにすることで回避した
- `EditorView.setState(EditorState.create(...))` で文書を丸ごと差し替えると、CodeMirror内部のスクロール位置復元処理が新しい文書の高さ情報と噛み合わず `Error: No tile at position ...` を起こして描画が壊れることがあった。文書差し替えは `view.dispatch({changes: {from:0, to: doc.length, insert: text}, selection:{anchor:0}})` という通常のトランザクションで行うことで解消した
- `Decoration.replace({widget, block:true})` で複数行（テーブル全体など）を一度に別のDOM要素へ差し替えると、境界（改行の含め方）を調整しても同様の heightmap 不整合（`No tile at position ...`）が再発した。複数行にまたがる単一の decoration は使わず、「先頭行だけを実際の `<table>` ウィジェットに差し替える単一行の replace」＋「残りの行を1行ずつ個別に非表示にする単一行の replace」を積み重ねる方式に変更して解消した（単一行の replace/line 装飾は既に安全性が確認済みのパターンのため）。あわせて `WidgetType.estimatedHeight` を実際の行数から概算して返すようにし、CodeMirrorが装飾直後にスクロール可能領域を正しく認識できるようにした
- テーブルのセルクリックで `view.dispatch({selection:{anchor:...}})` するだけではスクロール位置が追従しないことがあったため、`scrollIntoView: true` を必ず付与する
- CM6内部で捕捉された例外は `console.error` に出るだけで `window.onerror` には来ない。デバッグ用に `console.error` もフックして拾う必要があった

### 処理フロー（起動時のディレクトリオープン）

1. `Program.cs` が CLI引数を解析し、対象ディレクトリの絶対パスを確定する（`--version` 指定時はここでバージョンを表示して終了）
2. `DirectoryService` が対象ディレクトリ配下を再帰的に走査し、`*.md` ファイルの一覧（相対パス付き）を取得する
3. `MainWindow` 起動時にファイル一覧をサイドバーへツリー表示する
4. ユーザーが一覧からファイルを選択すると、そのファイルを読み込みエディタ／プレビューに反映する（v1では同時に開けるのは1ファイルのみ）

### 処理フロー（ファイル操作）

1. サイドバーの一覧からファイルを選択する、または「開く」メニュー／ショートカットでファイル選択ダイアログ（`Microsoft.Win32.OpenFileDialog`）を表示する
2. 選択された `.md` ファイルを `File.ReadAllText` で読み込み、エディタに反映する
3. 「保存」メニュー／ショートカットでエディタの内容を `File.WriteAllText` で同一パスに書き込む
4. 未保存の変更がある場合はタイトルバーに変更マーク（例: `*`）を表示する

## 画面構成

- メインウィンドウ
  - 上部: メニューバー（ファイル: 開く／保存／名前を付けて保存）
  - 左: ファイル一覧サイドバー（起動時に指定したディレクトリ配下の `.md` ファイルツリー、`TreeView`）
  - 右: 編集領域（`WebView2` 1枚。エディタとプレビューは分割せず統合）
  - 下部: ステータスバー（現在のファイルパス、未保存マーク）

## 主要機能一覧

| 機能 | 内容 |
|---|---|
| CLIからの起動 | `nomu .` / `nomu <dir>` でディレクトリを指定して起動、`nomu --version` でバージョン表示 |
| ファイル一覧表示 | 起動時に指定したディレクトリ配下の `.md` ファイルをサイドバーにツリー表示 |
| ファイルを開く | 一覧からの選択、またはファイルダイアログからローカルの `.md` ファイルを読み込む |
| ファイルを保存 | 編集中の内容を上書き保存／名前を付けて保存 |
| ライブプレビュー編集 | 見出し・太字/斜体・取り消し線・箇条書き・タスクリスト・テーブルをその場で装飾しながら編集（Obsidianのライブプレビュー相当） |
| タスクチェックボックス | `[ ]`/`[x]` を実際のチェックボックスとして表示し、クリックでテキストをトグル |

## プロジェクト構成（実装済み）

```
nomsidian/
├── docs/
│   └── design.md
├── sample/                          # 動作確認用のサンプルVault
├── src/
│   └── Nomsidian/
│       ├── Nomsidian.csproj         # AssemblyName=nomu, net10.0-windows
│       ├── Program.cs               # エントリポイント、CLI引数解析（nomu本体）
│       ├── App.cs                   # Application継承のみ（App.xamlは持たない）
│       ├── MainWindow.xaml
│       ├── MainWindow.xaml.cs
│       ├── Services/
│       │   ├── FileService.cs       # ファイル読み書き
│       │   └── DirectoryService.cs  # ディレクトリ配下の.md一覧取得
│       ├── Assets/
│       │   └── webui/               # ビルド成果物の配置先（Content, CopyToOutputDirectory）
│       │       ├── editor.html
│       │       └── bundle.js        # editor-src からビルド
│       └── editor-src/              # CodeMirror6 エディタのNodeプロジェクト（開発時のみ）
│           ├── package.json
│           └── src/
│               ├── main.js          # CM6セットアップ＋ライブプレビュー装飾ロジック
│               └── editor.html
└── CLAUDE.md
```

`Nomsidian.csproj` の `AssemblyName` を `nomu` にしているため、ビルド成果物は `nomu.exe` になる。`nomu` というコマンド名で使うには、出力先（`bin/Debug(or Release)/net10.0-windows/`）をPATHに追加するか、任意のPATH配下に `nomu.exe` をコピー／配置する。

## 今後の拡張（v1以降の候補）

- `[[WikiLink]]` 形式のファイル間リンクとバックリンク表示、グラフビュー
- 複数ファイルのタブ表示・同時編集
- ダーク／ライトテーマ切り替え
- 数式表示（KaTeX/MathJax連携）
- プラグイン機構
