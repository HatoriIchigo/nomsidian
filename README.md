# nomsidian

Obsidian のような見た目・操作感を持つ、ローカル動作の Markdown プレビュー／編集アプリケーション。

C# (.NET 10) + WPF で実装し、エディタ本体には CodeMirror 6 を採用しています。エディタとプレビューを分割せず、**1つの編集画面内で書式をその場で反映しながら編集できるライブプレビュー方式**（Obsidian 相当）が特徴です。

## 特徴

- **CLI から起動** — `nomu .` / `nomu <dir>` で指定ディレクトリを開く
- **ライブプレビュー編集** — 見出し・太字/斜体・取り消し線・箇条書き・タスクリスト・テーブルをその場で装飾しながら編集
- **タスクチェックボックス** — `[ ]` / `[x]` を実際のチェックボックスとして表示し、クリックでトグル
- **タブによる複数ファイル編集** — 複数の `.md` ファイルをタブで開ける
- **外部変更の自動読み込み** — ファイルが外部で変更された際に自動で再読み込み
- **カスタムウィンドウフレーム** — 独自のタイトルバー（最小化/最大化/閉じる）とサイドバー切替・戻る/進む操作
- **自己更新** — `nomu --update` でソースから最新版を取得・再ビルドして自身を更新
- **完全ローカル動作** — 通常動作ではネットワーク通信を行わない（`--update` 時のみ GitHub からソースを取得）

## 動作環境

- OS: Windows 10 / 11
- ランタイム: .NET 10（`net10.0-windows`）
- ビルド時のみ: Node.js（CodeMirror 6 を esbuild でバンドルするため。通常の実行時は不要）

## ビルド

```sh
# 1. エディタ本体（CodeMirror6）をバンドル
cd src/Nomsidian/editor-src
npm ci
npx esbuild src/main.js --bundle --minify --outfile="../Assets/webui/bundle.js"

# 2. アプリをビルド
cd ..
dotnet build -c Release
```

ビルド成果物は `nomu.exe` になります（`AssemblyName=nomu`）。`nomu` コマンドとして使うには、出力先（`bin/Release/net10.0-windows/`）を PATH に追加するか、任意の PATH 配下に `nomu.exe` を配置してください。

## 使い方

| コマンド | 動作 |
|---|---|
| `nomu .` | カレントディレクトリを開く |
| `nomu <dir>` | 指定したディレクトリを開く（相対／絶対パス両対応） |
| `nomu --version` | バージョン番号を表示して終了 |
| `nomu --update` | ソースから最新版を取得・再ビルドして自身を更新 |

起動後、左のサイドバーに指定ディレクトリ配下の `.md` ファイルがツリー表示されます。ファイルを選択すると編集領域に読み込まれ、ライブプレビューで編集できます。

## アーキテクチャ概要

- **エントリポイント** (`Program.cs`) — CLI 引数を解析し、WPF アプリを起動
- **MainWindow (WPF)** — ファイル一覧サイドバー ＋ WebView2（CodeMirror6 エディタ）
- **CodeMirror6** (`editor-src/`) — `@codemirror/lang-markdown` + `@lezer/markdown`（GFM 拡張）で構文木を取得し、カーソル位置に応じて装飾を再計算するライブプレビュー
- **C# ⇔ JS ブリッジ** — `ExecuteScriptAsync` でファイル内容を注入し、`WebMessageReceived` で変更を受け取る

詳細は [docs/design.md](docs/design.md) を参照してください。

## 今後の方針

- **デザインの洗練** — UI/UX 全体のブラッシュアップ、テーマ（ダーク／ライト）対応
- **mermaid 対応** — コードブロック内の mermaid 記法を図としてレンダリング
- **git status 表示** — Vault 内の Git 変更状態（変更／追加ファイル等）の可視化
- **コードブロック自動実行対応** — コードブロックをその場で実行し、結果を表示

その他の拡張候補（`[[WikiLink]]`・バックリンク・グラフビュー、数式表示、プラグイン機構など）は [docs/design.md](docs/design.md) の「今後の拡張」を参照してください。
