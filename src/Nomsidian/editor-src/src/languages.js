// ---- TreeSitter相当のシンタックスハイライト ----
// 本物のtree-sitter(wasm文法)ではなく、既存アーキテクチャと同じLezer/CodeMirror6の
// 言語パッケージ(公式lang-*パッケージ + legacy-modesのStreamLanguage)を使う。
// nomu自体がCM6(=Lezerベースの増分パーサ)を採用しているため、パーサエンジンを
// 二重に持たずに済み、単一ファイルbundleへの同梱もシンプルになる。
import { javascript } from "@codemirror/lang-javascript";
import { python } from "@codemirror/lang-python";
import { rust } from "@codemirror/lang-rust";
import { html } from "@codemirror/lang-html";
import { css } from "@codemirror/lang-css";
import { json } from "@codemirror/lang-json";
import { xml } from "@codemirror/lang-xml";
import { yaml } from "@codemirror/lang-yaml";
import { sql } from "@codemirror/lang-sql";
import { php } from "@codemirror/lang-php";
import { StreamLanguage, LanguageSupport } from "@codemirror/language";
import { clike } from "@codemirror/legacy-modes/mode/clike";
import { go } from "@codemirror/legacy-modes/mode/go";
import { ruby } from "@codemirror/legacy-modes/mode/ruby";
import { swift } from "@codemirror/legacy-modes/mode/swift";
import { shell } from "@codemirror/legacy-modes/mode/shell";
import { toml } from "@codemirror/legacy-modes/mode/toml";
import { properties } from "@codemirror/legacy-modes/mode/properties";
import { powerShell } from "@codemirror/legacy-modes/mode/powershell";

// clike()はCとC++で共通のためインスタンスを使い回す。C#/Javaは専用キーワード定義がある。
const cLike = clike({ name: "clike" });
const csharpLike = StreamLanguage.define(clike({ name: "csharp" }));
const javaLike = StreamLanguage.define(clike({ name: "java" }));
const kotlinLike = StreamLanguage.define(clike({ name: "kotlin" }));
const cppLike = StreamLanguage.define(cLike);

// 拡張子(ドット無し・小文字) -> LanguageSupport | StreamLanguage を返す関数
const extensionFactories = {
    js: () => javascript({ jsx: false, typescript: false }),
    jsx: () => javascript({ jsx: true, typescript: false }),
    ts: () => javascript({ jsx: false, typescript: true }),
    tsx: () => javascript({ jsx: true, typescript: true }),
    py: () => python(),
    rs: () => rust(),
    html: () => html(),
    htm: () => html(),
    css: () => css(),
    scss: () => css(),
    less: () => css(),
    json: () => json(),
    jsonc: () => json(),
    xml: () => xml(),
    yaml: () => yaml(),
    yml: () => yaml(),
    sql: () => sql(),
    php: () => php(),
    c: () => cppLike,
    h: () => cppLike,
    cpp: () => cppLike,
    cc: () => cppLike,
    cxx: () => cppLike,
    hpp: () => cppLike,
    hxx: () => cppLike,
    cs: () => csharpLike,
    java: () => javaLike,
    kt: () => kotlinLike,
    go: () => StreamLanguage.define(go),
    rb: () => StreamLanguage.define(ruby),
    swift: () => StreamLanguage.define(swift),
    sh: () => StreamLanguage.define(shell),
    bash: () => StreamLanguage.define(shell),
    ps1: () => StreamLanguage.define(powerShell),
    toml: () => StreamLanguage.define(toml),
    ini: () => StreamLanguage.define(properties),
    cfg: () => StreamLanguage.define(properties),
};

// fenced code block の info文字列(```js など)で使うエイリアス
const infoAliases = {
    javascript: "js",
    typescript: "ts",
    "c++": "cpp",
    "c#": "cs",
    csharp: "cs",
    golang: "go",
    ruby: "rb",
    shell: "sh",
    bash: "sh",
    yml: "yaml",
    htm: "html",
};

function resolveExtension(key) {
    const normalized = key.toLowerCase().trim();
    const aliased = infoAliases[normalized] || normalized;
    return extensionFactories[aliased] ? aliased : null;
}

/// ファイルパスの拡張子から対応する言語拡張(CM6のextensionsにそのまま渡せる形)を取得する(非Markdownファイル用)。
export function languageForFilename(path) {
    const dot = path.lastIndexOf(".");
    if (dot < 0) return null;
    const ext = resolveExtension(path.slice(dot + 1));
    return ext ? extensionFactories[ext]() : null;
}

/// フェンスドコードブロックの info 文字列(```js の "js" 部分)から Language を取得する。
/// @lezer/markdown の codeLanguages コールバックは Language(.parserを持つ)を要求するため、
/// LanguageSupport の場合は .language を取り出す。
export function languageForInfo(info) {
    if (!info) return null;
    const firstWord = info.split(/\s+/)[0];
    const ext = resolveExtension(firstWord);
    if (!ext) return null;
    const support = extensionFactories[ext]();
    return support instanceof LanguageSupport ? support.language : support;
}
