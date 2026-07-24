import { EditorView, keymap, Decoration, WidgetType, ViewPlugin, gutter, gutterLineClass, GutterMarker, lineNumbers } from "@codemirror/view";
import { EditorState, Compartment, StateEffect, RangeSet } from "@codemirror/state";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { markdown } from "@codemirror/lang-markdown";
import { GFM } from "@lezer/markdown";
import { syntaxTree, HighlightStyle, syntaxHighlighting } from "@codemirror/language";
import { tags, highlightTree } from "@lezer/highlight";
import { search, setSearchQuery, SearchQuery } from "@codemirror/search";
import { diffLines } from "diff";
import mermaid from "mermaid";
import { vim, getCM } from "@replit/codemirror-vim";
import { languageForFilename, languageForInfo } from "./languages.js";

mermaid.initialize({ startOnLoad: false, theme: "dark", securityLevel: "strict" });

// buildDecorations等はドキュメント変更/選択変更のたびに再実行されるため、同一エラーがそのまま
// 繰り返し発生するとMessageBox(モーダル)が連続表示されて操作不能になる。同じsource+messageの
// 組み合わせは1回だけ報告する。
const reportedErrorKeys = new Set();
function reportError(source, err) {
    const message = String(err && err.stack ? err.stack : err);
    // dedupキーはメッセージ本文の1行目だけを使う(スタックトレースの行:列は同じ不具合でも
    // 呼び出し経路によって微妙にずれることがあり、フルスタックだと同一エラーでも別扱いになってしまう)。
    const key = `${source} ${String(err && err.message ? err.message : err).split("\n")[0]}`;
    if (reportedErrorKeys.has(key)) return;
    reportedErrorKeys.add(key);

    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(JSON.stringify({ type: "error", source, message }));
    }
}
window.addEventListener("error", (e) => reportError("window.onerror", e.error || e.message));
window.addEventListener("unhandledrejection", (e) => reportError("unhandledrejection", e.reason));

const originalConsoleError = console.error.bind(console);
console.error = (...args) => {
    reportError("console.error", args.map((a) => (a && a.stack ? a.stack : String(a))).join(" "));
    originalConsoleError(...args);
};

// ---- カーソル/選択範囲がノードと重なっているか ----
function selectionOverlaps(state, from, to) {
    return state.selection.ranges.some((r) => r.from <= to && r.to >= from);
}

// ---- Widget: 箇条書きの "-"/"*"/"+" を "・" に差し替える ----
class BulletWidget extends WidgetType {
    toDOM() {
        const span = document.createElement("span");
        span.className = "cm-nomu-bullet";
        span.textContent = "•";
        return span;
    }
    ignoreEvent() {
        return false;
    }
}

// ---- Widget: タスクリストの "[ ]"/"[x]" を実際のチェックボックスに差し替える ----
class TaskCheckboxWidget extends WidgetType {
    constructor(checked, pos) {
        super();
        this.checked = checked;
        this.pos = pos;
    }
    eq(other) {
        return other.checked === this.checked && other.pos === this.pos;
    }
    toDOM(view) {
        const box = document.createElement("input");
        box.type = "checkbox";
        box.checked = this.checked;
        box.className = "cm-nomu-task";
        box.addEventListener("mousedown", (e) => e.preventDefault());
        box.addEventListener("change", () => {
            const replacement = this.checked ? "[ ]" : "[x]";
            view.dispatch({
                changes: { from: this.pos, to: this.pos + 3, insert: replacement },
            });
        });
        return box;
    }
    ignoreEvent(event) {
        return event.type !== "mousedown" && event.type !== "change";
    }
}

// ---- リンク/画像 ----
let vaultBasePath = "";

function requestOpenUrl(url) {
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(JSON.stringify({ type: "openUrl", url }));
    }
}

function requestOpenInternalLink(path) {
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(JSON.stringify({ type: "openInternalLink", path }));
    }
}

// スキーム(http:, mailto: など)を持つ絶対URLは外部リンク、それ以外はvault内の相対パスとして扱う
function followLink(url) {
    if (/^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(url)) {
        requestOpenUrl(url);
    } else {
        requestOpenInternalLink(url);
    }
}

// 相対パス（ノート自身のディレクトリ基準）を、vaultルートへの仮想ホストURLに変換する
function resolveImageSrc(url) {
    if (/^(https?:|data:|file:)/i.test(url)) return url;

    const joined = vaultBasePath ? `${vaultBasePath}/${url}` : url;
    const segments = [];
    for (const seg of joined.split("/")) {
        if (seg === "" || seg === ".") continue;
        if (seg === "..") segments.pop();
        else segments.push(seg);
    }
    return `https://nomu.vault/${segments.map(encodeURIComponent).join("/")}`;
}

// ---- Widget: [text](url) を実際のリンク風テキストに差し替える ----
class LinkWidget extends WidgetType {
    constructor(text, url, pos) {
        super();
        this.text = text;
        this.url = url;
        this.pos = pos;
    }
    eq(other) {
        return other.text === this.text && other.url === this.url;
    }
    toDOM(view) {
        const el = document.createElement("span");
        el.className = "cm-nomu-link";
        el.textContent = this.text || this.url;
        el.title = `Ctrl+クリックで開く: ${this.url}`;
        el.addEventListener("mousedown", (e) => {
            e.preventDefault();
            if (e.ctrlKey || e.metaKey) {
                followLink(this.url);
                return;
            }
            view.dispatch({ selection: { anchor: this.pos }, scrollIntoView: true });
            view.focus();
        });
        return el;
    }
    ignoreEvent() {
        return false;
    }
}

// ---- Widget: ![alt](url) を実際の <img> に差し替える ----
class ImageWidget extends WidgetType {
    constructor(alt, url, pos) {
        super();
        this.alt = alt;
        this.url = url;
        this.pos = pos;
    }
    eq(other) {
        return other.alt === this.alt && other.url === this.url;
    }
    toDOM(view) {
        const img = document.createElement("img");
        img.className = "cm-nomu-image";
        img.src = resolveImageSrc(this.url);
        img.alt = this.alt;
        img.addEventListener("error", () => {
            img.classList.add("cm-nomu-image-error");
            img.alt = `画像が見つかりません: ${this.url}`;
        });
        img.addEventListener("mousedown", (e) => {
            e.preventDefault();
            view.dispatch({ selection: { anchor: this.pos }, scrollIntoView: true });
            view.focus();
        });
        return img;
    }
    ignoreEvent() {
        return false;
    }
}

// ---- テーブルの行/セルを構文木から取り出す（生テキストの手動パースより位置が正確） ----
// 各行はソースの実際の行(from/to)に対応させ、行番号ガターと1対1になるようにする。
function getTableRows(tableNode, state) {
    const rows = [];
    let rowNode = tableNode.node.firstChild;
    while (rowNode) {
        if (rowNode.type.name === "TableHeader" || rowNode.type.name === "TableRow") {
            const cells = [];
            let cellNode = rowNode.firstChild;
            while (cellNode) {
                if (cellNode.type.name === "TableCell") {
                    cells.push({
                        from: cellNode.from,
                        to: cellNode.to,
                        text: state.doc.sliceString(cellNode.from, cellNode.to).trim(),
                    });
                }
                cellNode = cellNode.nextSibling;
            }
            const line = state.doc.lineAt(rowNode.from);
            rows.push({ isHeader: rowNode.type.name === "TableHeader", cells, lineFrom: line.from, lineTo: line.to });
        }
        rowNode = rowNode.nextSibling;
    }
    return rows;
}

// ---- 区切り行 "| --- | --- |" の行を構文木から取り出す ----
function getTableDelimiterLine(tableNode, state) {
    let child = tableNode.node.firstChild;
    while (child) {
        if (child.type.name === "TableDelimiter") {
            return state.doc.lineAt(child.from);
        }
        child = child.nextSibling;
    }
    return null;
}

// ---- 区切り行 "| --- | :--: |" からカラムの寄せを読み取る ----
function parseTableAlignment(lineText) {
    let cells = lineText.trim();
    if (cells.startsWith("|")) cells = cells.slice(1);
    if (cells.endsWith("|")) cells = cells.slice(0, -1);
    return cells.split("|").map((c) => {
        c = c.trim();
        const left = c.startsWith(":");
        const right = c.endsWith(":");
        if (left && right) return "center";
        if (right) return "right";
        if (left) return "left";
        return "";
    });
}

// ---- セルのテキスト幅をCanvasで実測する(ch単位の概算だと太字ヘッダーとボディで
// 幅がずれてしまうため、両方に同じpx値を使えるようにする) ----
const TABLE_CELL_FONT = "700 15px 'Segoe UI', 'Yu Gothic UI', sans-serif";
const TABLE_CELL_PADDING_PX = 28; // .cm-nomu-table-cell の左右padding(24px) + border分の余裕
let tableMeasureContext = null;

function measureTextWidth(text) {
    if (!tableMeasureContext) {
        tableMeasureContext = document.createElement("canvas").getContext("2d");
        tableMeasureContext.font = TABLE_CELL_FONT;
    }
    return tableMeasureContext.measureText(text).width;
}

function computeColumnTemplate(rows) {
    const columnCount = rows.length > 0 ? rows[0].cells.length : 0;
    const widths = new Array(columnCount).fill(24);
    for (const row of rows) {
        row.cells.forEach((cell, i) => {
            if (i < columnCount) {
                widths[i] = Math.max(widths[i], measureTextWidth(cell.text) + TABLE_CELL_PADDING_PX);
            }
        });
    }
    return widths.map((w) => `${Math.ceil(w)}px`).join(" ");
}

// ---- Widget: テーブルの1行を実際のソース行に対応させて描画する ----
// (複数行をまとめて1つのWidgetで描画すると行番号ガターとずれてしまうため、行ごとに分けている)
class TableRowWidget extends WidgetType {
    constructor(row, align, gridTemplateColumns) {
        super();
        this.row = row;
        this.align = align;
        this.gridTemplateColumns = gridTemplateColumns;
    }
    eq(other) {
        if (other.gridTemplateColumns !== this.gridTemplateColumns) return false;
        if (other.row.isHeader !== this.row.isHeader) return false;
        const a = this.row.cells;
        const b = other.row.cells;
        if (a.length !== b.length) return false;
        for (let i = 0; i < a.length; i++) {
            if (a[i].text !== b[i].text) return false;
        }
        return true;
    }
    toDOM(view) {
        const rowEl = document.createElement("div");
        rowEl.className = this.row.isHeader
            ? "cm-nomu-table-row cm-nomu-table-row-header"
            : "cm-nomu-table-row";
        rowEl.style.gridTemplateColumns = this.gridTemplateColumns;

        this.row.cells.forEach((cell, i) => {
            const cellEl = document.createElement("div");
            cellEl.className = "cm-nomu-table-cell";
            cellEl.textContent = cell.text;
            if (this.align[i]) {
                cellEl.style.textAlign = this.align[i];
            }
            cellEl.addEventListener("mousedown", (e) => {
                e.preventDefault();
                view.dispatch({ selection: { anchor: cell.from }, scrollIntoView: true });
                view.focus();
            });
            rowEl.appendChild(cellEl);
        });

        return rowEl;
    }
    ignoreEvent() {
        return false;
    }
}

// ---- Widget: ```mermaid コードブロックを実際の図(SVG)に差し替える ----
let mermaidRenderSeq = 0;
const mermaidCache = new Map();

class MermaidWidget extends WidgetType {
    constructor(source, pos) {
        super();
        this.source = source;
        this.pos = pos;
    }
    eq(other) {
        return other.source === this.source;
    }
    toDOM(view) {
        const wrapper = document.createElement("div");
        wrapper.className = "cm-nomu-mermaid";
        wrapper.addEventListener("mousedown", (e) => {
            e.preventDefault();
            view.dispatch({ selection: { anchor: this.pos }, scrollIntoView: true });
            view.focus();
        });

        const cached = mermaidCache.get(this.source);
        if (cached) {
            wrapper.innerHTML = cached;
        } else {
            wrapper.textContent = "図を描画中...";
            const id = `nomu-mermaid-${mermaidRenderSeq++}`;
            mermaid
                .render(id, this.source)
                .then(({ svg }) => {
                    mermaidCache.set(this.source, svg);
                    wrapper.innerHTML = svg;
                    view.requestMeasure();
                })
                .catch((err) => {
                    wrapper.className = "cm-nomu-mermaid cm-nomu-mermaid-error";
                    wrapper.textContent = `Mermaid 構文エラー: ${err && err.message ? err.message : err}`;
                    view.requestMeasure();
                });
        }
        return wrapper;
    }
    ignoreEvent() {
        return false;
    }
}

const HEADING_NODES = new Set([
    "ATXHeading1",
    "ATXHeading2",
    "ATXHeading3",
    "ATXHeading4",
    "ATXHeading5",
    "ATXHeading6",
]);

function headingLevel(name) {
    return Number(name.slice(-1));
}

// ---- ライブプレビュー装飾 ----
// ---- テーブル/mermaidの非表示行では行番号ガターも一緒に隠す ----
class HiddenGutterMarker extends GutterMarker {
    constructor() {
        super();
        this.elementClass = "cm-nomu-gutter-hidden";
    }
}
const hiddenGutterLineMarker = new HiddenGutterMarker();

function buildDecorations(view) {
    const decos = [];
    const hiddenGutterLines = [];
    const state = view.state;

    for (const { from, to } of view.visibleRanges) {
        syntaxTree(state).iterate({
            from,
            to,
            enter: (node) => {
              try {
                const name = node.type.name;

                if (name === "Table") {
                    if (!selectionOverlaps(state, node.from, node.to)) {
                        const rows = getTableRows(node, state);
                        const delimiterLine = getTableDelimiterLine(node, state);
                        const align = delimiterLine ? parseTableAlignment(delimiterLine.text) : [];
                        const gridTemplateColumns = computeColumnTemplate(rows);

                        // 行ごとに実際のソース行へ1つずつWidgetを対応させる。1つの巨大なWidgetに
                        // まとめると行番号ガターと表示がずれてしまうため、あえて行単位にしている。
                        for (const row of rows) {
                            decos.push(
                                Decoration.replace({
                                    widget: new TableRowWidget(row, align, gridTemplateColumns),
                                }).range(row.lineFrom, row.lineTo)
                            );
                        }
                        if (delimiterLine) {
                            decos.push(Decoration.replace({}).range(delimiterLine.from, delimiterLine.to));
                            decos.push(Decoration.line({ class: "cm-nomu-block-hidden-line" }).range(delimiterLine.from));
                            hiddenGutterLines.push(hiddenGutterLineMarker.range(delimiterLine.from));
                        }
                    }
                    return false;
                }

                if (HEADING_NODES.has(name)) {
                    const line = state.doc.lineAt(node.from);
                    decos.push(Decoration.line({ class: `cm-nomu-h cm-nomu-h${headingLevel(name)}` }).range(line.from));
                    return;
                }

                if (name === "HeaderMark") {
                    const parent = node.node.parent;
                    if (parent && HEADING_NODES.has(parent.type.name) && !selectionOverlaps(state, parent.from, parent.to)) {
                        // "# " (マーク+直後の空白) を隠す
                        let end = node.to;
                        if (state.doc.sliceString(end, end + 1) === " ") end += 1;
                        decos.push(Decoration.replace({}).range(node.from, end));
                    }
                    return;
                }

                if (name === "StrongEmphasis" || name === "Emphasis" || name === "Strikethrough") {
                    const cls = name === "StrongEmphasis" ? "cm-nomu-strong" : name === "Emphasis" ? "cm-nomu-em" : "cm-nomu-strike";
                    const overlapped = selectionOverlaps(state, node.from, node.to);
                    if (!overlapped) {
                        decos.push(Decoration.mark({ class: cls }).range(node.from, node.to));
                    }
                    return;
                }

                if (name === "EmphasisMark" || name === "StrikethroughMark") {
                    const parent = node.node.parent;
                    if (parent && !selectionOverlaps(state, parent.from, parent.to)) {
                        decos.push(Decoration.replace({}).range(node.from, node.to));
                    }
                    return;
                }

                if (name === "InlineCode") {
                    decos.push(Decoration.mark({ class: "cm-nomu-code" }).range(node.from, node.to));
                    return;
                }

                if (name === "CodeMark") {
                    const parent = node.node.parent;
                    if (parent && parent.type.name === "InlineCode" && !selectionOverlaps(state, parent.from, parent.to)) {
                        decos.push(Decoration.replace({}).range(node.from, node.to));
                    }
                    return;
                }

                if (name === "Link" || name === "Image") {
                    if (!selectionOverlaps(state, node.from, node.to)) {
                        const marks = [];
                        let urlNode = null;
                        for (let child = node.node.firstChild; child; child = child.nextSibling) {
                            if (child.type.name === "LinkMark") marks.push(child);
                            else if (child.type.name === "URL") urlNode = child;
                        }
                        if (marks.length >= 2 && urlNode) {
                            const text = state.doc.sliceString(marks[0].to, marks[1].from);
                            const url = state.doc.sliceString(urlNode.from, urlNode.to);
                            const widget =
                                name === "Image"
                                    ? new ImageWidget(text, url, node.from)
                                    : new LinkWidget(text, url, node.from);
                            decos.push(Decoration.replace({ widget }).range(node.from, node.to));
                        }
                    }
                    return false;
                }

                if (name === "Blockquote") {
                    const startLine = state.doc.lineAt(node.from).number;
                    const endLine = state.doc.lineAt(node.to).number;
                    for (let ln = startLine; ln <= endLine; ln++) {
                        decos.push(Decoration.line({ class: "cm-nomu-quote" }).range(state.doc.line(ln).from));
                    }
                    return;
                }

                if (name === "QuoteMark") {
                    const line = state.doc.lineAt(node.from);
                    if (!selectionOverlaps(state, line.from, line.to)) {
                        // ">" (マーク+直後の空白) を隠す。カーソルがその行にある時だけ生の ">" を見せる
                        let end = node.to;
                        if (state.doc.sliceString(end, end + 1) === " ") end += 1;
                        decos.push(Decoration.replace({}).range(node.from, end));
                    }
                    return;
                }

                if (name === "FencedCode") {
                    const fenceNode = node.node;
                    let infoText = "";
                    let codeFrom = -1;
                    let codeTo = -1;
                    let openMark = null;
                    let closeMark = null;
                    for (let child = fenceNode.firstChild; child; child = child.nextSibling) {
                        if (child.type.name === "CodeMark") {
                            if (!openMark) openMark = child;
                            else closeMark = child;
                        } else if (child.type.name === "CodeInfo") {
                            infoText = state.doc.sliceString(child.from, child.to).trim();
                        } else if (child.type.name === "CodeText") {
                            if (codeFrom === -1) codeFrom = child.from;
                            codeTo = child.to;
                        }
                    }

                    if (infoText.toLowerCase() === "mermaid" && !selectionOverlaps(state, node.from, node.to)) {
                        const source = codeFrom === -1 ? "" : state.doc.sliceString(codeFrom, codeTo);
                        const firstLine = state.doc.lineAt(node.from);
                        const lastLine = state.doc.lineAt(node.to);

                        decos.push(
                            Decoration.replace({ widget: new MermaidWidget(source, firstLine.from) }).range(firstLine.from, firstLine.to)
                        );
                        for (let ln = firstLine.number + 1; ln <= lastLine.number; ln++) {
                            const line = state.doc.line(ln);
                            decos.push(Decoration.line({ class: "cm-nomu-block-hidden-line" }).range(line.from));
                            // 空行(line.from === line.to)にDecoration.replace({})を使うと、CM6が
                            // 「Invalid range for replacement decoration」を投げる(replace系decorationの
                            // ゼロ幅rangeはwidget付きでない限り許可されないため)。隠す文字が無いので単純にスキップする。
                            if (line.to > line.from) {
                                decos.push(Decoration.replace({}).range(line.from, line.to));
                            }
                            hiddenGutterLines.push(hiddenGutterLineMarker.range(line.from));
                        }
                        return false;
                    }

                    // 通常のコードブロック: ```lang / ``` のフェンス行は、カーソルがその行にない時だけ隠す
                    for (const mark of [openMark, closeMark]) {
                        if (!mark) continue;
                        const line = state.doc.lineAt(mark.from);
                        if (!selectionOverlaps(state, line.from, line.to)) {
                            decos.push(Decoration.replace({}).range(line.from, line.to));
                            decos.push(Decoration.line({ class: "cm-nomu-block-hidden-line" }).range(line.from));
                            hiddenGutterLines.push(hiddenGutterLineMarker.range(line.from));
                        }
                    }

                    // info文字列に対応する言語があれば、そのコード本文だけを個別にパースしてハイライトする。
                    // lezer/markdownのcodeLanguages(parseMixed)機構は、mermaid等の複数行装飾のための
                    // 独自range計算と衝突して「Invalid range for replacement decoration」を起こすため使わず、
                    // ここで直接Decoration.markを組み立てる(languages.jsのlanguageForInfoを再利用)。
                    if (codeFrom !== -1) {
                        const lang = languageForInfo(infoText);
                        if (lang) {
                            try {
                                const codeStr = state.doc.sliceString(codeFrom, codeTo);
                                const tree = lang.parser.parse(codeStr);
                                highlightTree(tree, nomuHighlightStyle, (from, to, classes) => {
                                    decos.push(Decoration.mark({ class: classes }).range(codeFrom + from, codeFrom + to));
                                });
                            } catch (err) {
                                reportError("codeBlockHighlight", err);
                            }
                        }
                    }
                }

                if (name === "FencedCode" || name === "CodeBlock") {
                    const startLine = state.doc.lineAt(node.from).number;
                    const endLine = state.doc.lineAt(node.to).number;
                    for (let ln = startLine; ln <= endLine; ln++) {
                        decos.push(Decoration.line({ class: "cm-nomu-codeblock" }).range(state.doc.line(ln).from));
                    }
                    return;
                }

                if (name === "ListMark") {
                    const parent = node.node.parent;
                    const grandparent = parent && parent.parent;
                    if (grandparent && grandparent.type.name === "BulletList") {
                        const line = state.doc.lineAt(node.from);
                        if (!selectionOverlaps(state, line.from, line.to)) {
                            decos.push(Decoration.replace({ widget: new BulletWidget() }).range(node.from, node.to));
                        }
                    }
                    return;
                }

                if (name === "TaskMarker") {
                    const text = state.doc.sliceString(node.from, node.to);
                    const checked = /x/i.test(text);
                    decos.push(Decoration.replace({ widget: new TaskCheckboxWidget(checked, node.from) }).range(node.from, node.to));
                    return;
                }
              } catch (err) {
                // 1ノードの装飾計算で例外が起きても文書全体の装飾(見出し・太字等)を巻き添えにしない。
                // 無効なrange計算などの想定外ケースはこのノードだけ諦めて他のノードの処理を続ける。
                reportError("buildDecorations:enter", err);
                return false;
              }
            },
        });
    }

    decos.sort((a, b) => a.from - b.from || a.to - b.to);
    hiddenGutterLines.sort((a, b) => a.from - b.from);
    return { decorations: Decoration.set(decos, true), gutterHidden: RangeSet.of(hiddenGutterLines) };
}

function safeBuildDecorations(view) {
    try {
        return buildDecorations(view);
    } catch (err) {
        reportError("buildDecorations", err);
        return { decorations: Decoration.none, gutterHidden: RangeSet.empty };
    }
}

const livePreviewPlugin = ViewPlugin.fromClass(
    class {
        constructor(view) {
            this.decorations = safeBuildDecorations(view).decorations;
        }
        update(update) {
            if (update.docChanged || update.selectionSet || update.viewportChanged) {
                this.decorations = safeBuildDecorations(update.view).decorations;
            }
        }
    },
    {
        decorations: (v) => v.decorations,
    }
);

// テーブル/mermaidの非表示行では行番号ガターの表示も一緒に隠す（メイン装飾とは別に、
// ドキュメント全体を対象として state から直接計算する）。
const hiddenGutterLinesField = gutterLineClass.compute(["doc", "selection"], (state) => {
    try {
        return buildDecorations({ state, visibleRanges: [{ from: 0, to: state.doc.length }] }).gutterHidden;
    } catch (err) {
        reportError("hiddenGutterLinesField", err);
        return RangeSet.empty;
    }
});

const nomuTheme = EditorView.theme(
    {
        "&": {
            color: "#dcddde",
            backgroundColor: "#1e1e1e",
            height: "100%",
            fontSize: "15px",
        },
        ".cm-content": {
            fontFamily: "'Segoe UI', 'Yu Gothic UI', sans-serif",
            caretColor: "#dcddde",
            padding: "12px 0",
        },
        ".cm-scroller": {
            overflow: "auto",
        },
        ".cm-scroller::-webkit-scrollbar": {
            width: "4px",
            height: "4px",
        },
        ".cm-scroller::-webkit-scrollbar-track": {
            background: "transparent",
        },
        ".cm-scroller::-webkit-scrollbar-thumb": {
            backgroundColor: "rgba(138, 138, 144, 0.25)",
            borderRadius: "2px",
        },
        ".cm-scroller::-webkit-scrollbar-thumb:hover": {
            backgroundColor: "rgba(138, 138, 144, 0.45)",
        },
        ".cm-scroller::-webkit-scrollbar-thumb:active": {
            backgroundColor: "rgba(138, 138, 144, 0.6)",
        },
        ".cm-gutters": {
            backgroundColor: "#1e1e1e",
            color: "#5a5a5a",
            border: "none",
        },
        ".cm-nomu-git-gutter": {
            width: "6px",
            minWidth: "6px",
        },
        ".cm-nomu-git-marker": {
            width: "3px",
            height: "100%",
            marginLeft: "1px",
        },
        ".cm-nomu-git-add": {
            backgroundColor: "#4caf50",
        },
        ".cm-nomu-git-modify": {
            backgroundColor: "#4a90d9",
        },
        ".cm-nomu-git-delete-before": {
            backgroundColor: "transparent",
            position: "relative",
        },
        ".cm-nomu-git-delete-before::before": {
            content: "''",
            position: "absolute",
            top: "0",
            left: "1px",
            width: "0",
            height: "0",
            borderStyle: "solid",
            borderWidth: "0 0 5px 5px",
            borderColor: "transparent transparent #e06c75 transparent",
        },
        "&.cm-focused .cm-cursor": {
            borderLeftColor: "#dcddde",
        },
        "&.cm-focused .cm-selectionBackground, .cm-selectionBackground": {
            backgroundColor: "#3a4a6b !important",
        },
        ".cm-nomu-h": {
            fontWeight: "600",
            color: "#ffffff",
        },
        ".cm-nomu-h1": { fontSize: "1.8em" },
        ".cm-nomu-h2": { fontSize: "1.5em" },
        ".cm-nomu-h3": { fontSize: "1.3em" },
        ".cm-nomu-h4": { fontSize: "1.15em" },
        ".cm-nomu-h5": { fontSize: "1.05em" },
        ".cm-nomu-h6": { fontSize: "1em" },
        ".cm-nomu-strong": {
            fontWeight: "700",
            color: "#ffffff",
        },
        ".cm-nomu-em": {
            fontStyle: "italic",
        },
        ".cm-nomu-strike": {
            textDecoration: "line-through",
            opacity: "0.7",
        },
        ".cm-nomu-code": {
            fontFamily: "Consolas, 'Cascadia Code', monospace",
            backgroundColor: "#2a2a2a",
            borderRadius: "4px",
            padding: "0 3px",
        },
        ".cm-nomu-codeblock": {
            backgroundColor: "#2a2a2a",
        },
        ".cm-nomu-link": {
            color: "#6ea8fe",
            textDecoration: "underline",
            cursor: "pointer",
        },
        ".cm-nomu-image": {
            maxWidth: "100%",
            borderRadius: "4px",
            verticalAlign: "middle",
            cursor: "text",
        },
        ".cm-nomu-image-error": {
            display: "inline-block",
            minWidth: "120px",
            minHeight: "24px",
            border: "1px dashed #5a5a5a",
            color: "#a9abae",
            fontSize: "12px",
        },
        ".cm-nomu-quote": {
            borderLeft: "3px solid #4a4a4a",
            paddingLeft: "10px",
            color: "#a9abae",
        },
        ".cm-nomu-bullet": {
            display: "inline-block",
            width: "1.1em",
            color: "#a9abae",
        },
        ".cm-nomu-task": {
            marginRight: "0.4em",
            verticalAlign: "middle",
        },
        ".cm-widgetBuffer": {
            height: "0",
        },
        ".cm-nomu-table-row": {
            display: "inline-grid",
            verticalAlign: "top",
            border: "1px solid #3a3a3a",
            borderTop: "none",
            backgroundColor: "#242424",
        },
        ".cm-nomu-table-row-header": {
            borderTop: "1px solid #3a3a3a",
            backgroundColor: "#2a2a2a",
            color: "#ffffff",
            fontWeight: "700",
        },
        ".cm-nomu-table-cell": {
            padding: "3px 12px",
            borderRight: "1px solid #3a3a3a",
            cursor: "text",
            overflow: "hidden",
            whiteSpace: "nowrap",
            textOverflow: "ellipsis",
        },
        ".cm-nomu-table-cell:last-child": {
            borderRight: "none",
        },
        ".cm-nomu-block-hidden-line": {
            fontSize: "1px",
            lineHeight: "1px",
        },
        ".cm-nomu-gutter-hidden": {
            fontSize: "1px",
            lineHeight: "1px",
            opacity: "0",
        },
        ".cm-nomu-mermaid": {
            margin: "0.5em 0",
            padding: "12px",
            backgroundColor: "#242424",
            borderRadius: "6px",
            display: "flex",
            justifyContent: "center",
            cursor: "text",
        },
        ".cm-nomu-mermaid svg": {
            maxWidth: "100%",
        },
        ".cm-nomu-mermaid-error": {
            color: "#e06c75",
            fontFamily: "Consolas, 'Cascadia Code', monospace",
            justifyContent: "flex-start",
            whiteSpace: "pre-wrap",
        },
        ".cm-searchMatch": {
            backgroundColor: "rgba(136, 117, 255, 0.35)",
        },
        ".cm-searchMatch-selected": {
            backgroundColor: "rgba(136, 117, 255, 0.65)",
        },
    },
    { dark: true }
);

let view = null;
let changeCallbackTimer = null;

function notifyHostChanged() {
    if (changeCallbackTimer) clearTimeout(changeCallbackTimer);
    changeCallbackTimer = setTimeout(() => {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(
                JSON.stringify({ type: "change", text: view.state.doc.toString() })
            );
        }
    }, 150);
}

function requestSave() {
    if (changeCallbackTimer) {
        clearTimeout(changeCallbackTimer);
        changeCallbackTimer = null;
    }
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(
            JSON.stringify({ type: "save", text: view.state.doc.toString() })
        );
    }
}

const saveKeymap = keymap.of([
    {
        key: "Mod-s",
        run: () => {
            requestSave();
            return true;
        },
        preventDefault: true,
    },
]);

// ---- git gutter: 直近コミット(HEAD)との行単位diffをガターに表示 ----
const setGitBaseEffect = StateEffect.define();
let gitBaseText = null;

function computeGitMarkers(state) {
    const markers = new Map();
    const currentText = state.doc.toString();
    if (gitBaseText == null || gitBaseText === currentText) {
        return markers;
    }

    const totalLines = state.doc.lines;
    const hunks = diffLines(gitBaseText, currentText);
    let line = 1;
    for (let i = 0; i < hunks.length; i++) {
        const hunk = hunks[i];
        if (hunk.added) {
            const type = i > 0 && hunks[i - 1].removed ? "modify" : "add";
            for (let n = 0; n < hunk.count; n++) {
                markers.set(line, type);
                line++;
            }
        } else if (hunk.removed) {
            const next = hunks[i + 1];
            if (!(next && next.added)) {
                markers.set(Math.min(line, totalLines), "delete-before");
            }
        } else {
            line += hunk.count;
        }
    }
    return markers;
}

class GitMarkerElement extends GutterMarker {
    constructor(type) {
        super();
        this.type = type;
    }
    eq(other) {
        return other.type === this.type;
    }
    toDOM() {
        const el = document.createElement("div");
        el.className = `cm-nomu-git-marker cm-nomu-git-${this.type}`;
        return el;
    }
}

const gitGutterPlugin = ViewPlugin.fromClass(
    class {
        constructor(view) {
            this.markers = computeGitMarkers(view.state);
        }
        update(update) {
            const baseChanged = update.transactions.some((tr) =>
                tr.effects.some((e) => e.is(setGitBaseEffect))
            );
            if (update.docChanged || baseChanged) {
                this.markers = computeGitMarkers(update.state);
            }
        }
    }
);

const gitGutterExtension = gutter({
    class: "cm-nomu-git-gutter",
    lineMarker(view, line) {
        const plugin = view.plugin(gitGutterPlugin);
        if (!plugin) return null;
        const lineNumber = view.state.doc.lineAt(line.from).number;
        const type = plugin.markers.get(lineNumber);
        return type ? new GitMarkerElement(type) : null;
    },
    lineMarkerChange: (update) =>
        update.docChanged || update.transactions.some((tr) => tr.effects.some((e) => e.is(setGitBaseEffect))),
});

const modeCompartment = new Compartment();

const plainTextTheme = EditorView.theme({
    ".cm-content": {
        fontFamily: "Consolas, 'Cascadia Code', monospace",
    },
});

// ---- シンタックスハイライト配色(tree-sitter/Lezerのタグに対する色付け) ----
// 「特定の単語(ifなど)を色付けする」という要望に対応するため、キーワードは強めの赤系にしている。
const nomuHighlightStyle = HighlightStyle.define([
    { tag: tags.keyword, color: "#e06c75", fontWeight: "600" },
    { tag: tags.controlKeyword, color: "#e06c75", fontWeight: "600" },
    { tag: tags.operatorKeyword, color: "#e06c75" },
    { tag: [tags.string, tags.special(tags.string)], color: "#98c379" },
    { tag: tags.number, color: "#d19a66" },
    { tag: tags.bool, color: "#d19a66" },
    { tag: tags.comment, color: "#7f848e", fontStyle: "italic" },
    { tag: [tags.function(tags.variableName), tags.function(tags.propertyName)], color: "#61afef" },
    { tag: tags.definition(tags.variableName), color: "#e5c07b" },
    { tag: tags.typeName, color: "#e5c07b" },
    { tag: tags.className, color: "#e5c07b" },
    { tag: tags.propertyName, color: "#61afef" },
    { tag: tags.atom, color: "#d19a66" },
    { tag: tags.tagName, color: "#e06c75" },
    { tag: tags.attributeName, color: "#d19a66" },
    { tag: tags.angleBracket, color: "#7f848e" },
    { tag: tags.punctuation, color: "#a9abae" },
    { tag: tags.invalid, color: "#e06c75", textDecoration: "underline wavy" },
]);
const nomuSyntaxHighlighting = syntaxHighlighting(nomuHighlightStyle);

function modeExtensions(isMarkdown, filePath) {
    if (isMarkdown) {
        return [markdown({ extensions: [GFM] }), livePreviewPlugin];
    }

    const lang = languageForFilename(filePath || "");
    return lang ? [lang, plainTextTheme] : [plainTextTheme];
}

function createEditorState(doc, isMarkdown, filePath) {
    return EditorState.create({
        doc,
        extensions: [
            // vim() は defaultKeymap よりノーマルモードの捕捉を優先させるため必ず先頭に置く
            // (nomu.lua の config.editor.vim_mode で有効化。C#側が __nomuInit() 実行前に
            // window.__nomuVimMode をセットしておく)
            ...(window.__nomuVimMode ? [vim()] : []),
            history(),
            saveKeymap,
            keymap.of([...defaultKeymap, ...historyKeymap, indentWithTab]),
            modeCompartment.of(modeExtensions(isMarkdown, filePath)),
            gitGutterExtension,
            gitGutterPlugin,
            lineNumbers(),
            hiddenGutterLinesField,
            search({ top: true }),
            nomuTheme,
            nomuSyntaxHighlighting,
            EditorView.lineWrapping,
            EditorView.updateListener.of((update) => {
                if (update.docChanged) {
                    notifyHostChanged();
                }
                if (update.docChanged || update.selectionSet) {
                    reportCursorPosition(update.view);
                }
            }),
        ],
    });
}

// ---- ステータスライン(下部)用ブリッジ: カーソル位置とvimモード ----
function reportCursorPosition(v) {
    const pos = v.state.selection.main.head;
    const line = v.state.doc.lineAt(pos);
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(
            JSON.stringify({ type: "cursor", line: line.number, col: pos - line.from + 1 })
        );
    }
}

function reportVimMode(mode) {
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(JSON.stringify({ type: "vimMode", mode }));
    }
}

window.__nomuInit = function () {
    view = new EditorView({
        state: createEditorState("", true),
        parent: document.getElementById("editor"),
    });

    if (window.__nomuVimMode) {
        // vim()拡張のViewPlugin(vimPlugin)はEditorView構築時に同期的に初期化され、
        // view.cm(CM5互換シム)を生やす。getCM()経由でCM5スタイルのイベントAPIを使い、
        // ノーマル/インサート/ビジュアル等のモード切り替えをステータスラインへ橋渡しする。
        const cm = getCM(view);
        if (cm) {
            cm.on("vim-mode-change", (e) => {
                reportVimMode(e.subMode ? `${e.mode} ${e.subMode}` : e.mode);
            });
        }
        reportVimMode("normal");
    }

    reportCursorPosition(view);
};

window.__nomuSetContent = function (text, isMarkdown, filePath) {
    gitBaseText = null;
    view.dispatch({
        changes: { from: 0, to: view.state.doc.length, insert: text },
        selection: { anchor: 0 },
        effects: [modeCompartment.reconfigure(modeExtensions(isMarkdown, filePath)), setGitBaseEffect.of(null)],
    });
};

window.__nomuGetContent = function () {
    return view.state.doc.toString();
};

window.__nomuSetGitBase = function (text) {
    gitBaseText = text;
    view.dispatch({ effects: setGitBaseEffect.of(text) });
};

window.__nomuSetBasePath = function (path) {
    vaultBasePath = path || "";
};

// ---- ファイル内検索(サイドバーの検索パネル用ブリッジ) ----
// @codemirror/search の SearchQuery をハイライト表示(search()拡張)とマッチ列挙の両方に使う。
window.__nomuSetInFileSearchQuery = function (query) {
    if (!query) {
        view.dispatch({ effects: setSearchQuery.of(new SearchQuery({ search: "" })) });
        return "[]";
    }

    const searchQuery = new SearchQuery({ search: query, caseSensitive: false });
    view.dispatch({ effects: setSearchQuery.of(searchQuery) });

    const matches = [];
    const cursor = searchQuery.getCursor(view.state);
    let result = cursor.next();
    while (!result.done) {
        const { from, to } = result.value;
        const line = view.state.doc.lineAt(from);
        matches.push({
            from,
            to,
            line: line.number,
            lineText: line.text,
            matchStart: from - line.from,
            matchLength: to - from,
        });
        result = cursor.next();
    }
    return JSON.stringify(matches);
};

window.__nomuGotoRange = function (from, to) {
    view.dispatch({ selection: { anchor: from, head: to }, scrollIntoView: true });
    view.focus();
};

window.__nomuInit();
