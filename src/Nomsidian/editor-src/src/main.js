import { EditorView, keymap, Decoration, WidgetType, ViewPlugin } from "@codemirror/view";
import { EditorState } from "@codemirror/state";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { markdown } from "@codemirror/lang-markdown";
import { GFM } from "@lezer/markdown";
import { syntaxTree } from "@codemirror/language";

function reportError(source, err) {
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(
            JSON.stringify({ type: "error", source, message: String(err && err.stack ? err.stack : err) })
        );
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

// ---- テーブルの行/セルを構文木から取り出す（生テキストの手動パースより位置が正確） ----
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
            rows.push({ isHeader: rowNode.type.name === "TableHeader", cells });
        }
        rowNode = rowNode.nextSibling;
    }
    return rows;
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

// ---- Widget: パイプ記法のテーブルを実際の <table> に差し替える ----
class TableWidget extends WidgetType {
    constructor(rows, align) {
        super();
        this.rows = rows;
        this.align = align;
    }
    eq(other) {
        if (other.rows.length !== this.rows.length) return false;
        for (let i = 0; i < this.rows.length; i++) {
            const a = this.rows[i].cells;
            const b = other.rows[i].cells;
            if (a.length !== b.length) return false;
            for (let j = 0; j < a.length; j++) {
                if (a[j].text !== b[j].text) return false;
            }
        }
        return true;
    }
    get estimatedHeight() {
        return this.rows.length * 31 + 8;
    }
    toDOM(view) {
        const wrapper = document.createElement("div");
        wrapper.className = "cm-nomu-table-wrap";
        const table = document.createElement("table");
        table.className = "cm-nomu-table";
        const thead = document.createElement("thead");
        const tbody = document.createElement("tbody");

        this.rows.forEach((row) => {
            const tr = document.createElement("tr");
            row.cells.forEach((cell, i) => {
                const el = document.createElement(row.isHeader ? "th" : "td");
                el.textContent = cell.text;
                if (this.align[i]) {
                    el.style.textAlign = this.align[i];
                }
                el.addEventListener("mousedown", (e) => {
                    e.preventDefault();
                    view.dispatch({ selection: { anchor: cell.from }, scrollIntoView: true });
                    view.focus();
                });
                tr.appendChild(el);
            });
            (row.isHeader ? thead : tbody).appendChild(tr);
        });

        table.appendChild(thead);
        table.appendChild(tbody);
        wrapper.appendChild(table);
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
function buildDecorations(view) {
    const decos = [];
    const state = view.state;

    for (const { from, to } of view.visibleRanges) {
        syntaxTree(state).iterate({
            from,
            to,
            enter: (node) => {
                const name = node.type.name;

                if (name === "Table") {
                    if (!selectionOverlaps(state, node.from, node.to)) {
                        const firstLine = state.doc.lineAt(node.from);
                        const lastLine = state.doc.lineAt(node.to);
                        const rows = getTableRows(node, state);
                        const align =
                            lastLine.number > firstLine.number
                                ? parseTableAlignment(state.doc.line(firstLine.number + 1).text)
                                : [];

                        // block:true で複数行を一括置換すると CodeMirror の内部高さ計算(heightmap)が
                        // 壊れて描画不能になる不具合を確認したため、1行ずつの安全な置換/非表示を積み重ねる。
                        // 先頭行だけを実際の <table> に差し替え、残りの行は個別に非表示にする。
                        decos.push(
                            Decoration.replace({ widget: new TableWidget(rows, align) }).range(firstLine.from, firstLine.to)
                        );
                        for (let ln = firstLine.number + 1; ln <= lastLine.number; ln++) {
                            const line = state.doc.line(ln);
                            decos.push(Decoration.line({ class: "cm-nomu-table-hidden-line" }).range(line.from));
                            decos.push(Decoration.replace({}).range(line.from, line.to));
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

                if (name === "Blockquote") {
                    const startLine = state.doc.lineAt(node.from).number;
                    const endLine = state.doc.lineAt(node.to).number;
                    for (let ln = startLine; ln <= endLine; ln++) {
                        decos.push(Decoration.line({ class: "cm-nomu-quote" }).range(state.doc.line(ln).from));
                    }
                    return;
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
            },
        });
    }

    decos.sort((a, b) => a.from - b.from || a.to - b.to);
    return Decoration.set(decos, true);
}

function safeBuildDecorations(view) {
    try {
        return buildDecorations(view);
    } catch (err) {
        reportError("buildDecorations", err);
        return Decoration.none;
    }
}

const livePreviewPlugin = ViewPlugin.fromClass(
    class {
        constructor(view) {
            this.decorations = safeBuildDecorations(view);
        }
        update(update) {
            if (update.docChanged || update.selectionSet || update.viewportChanged) {
                this.decorations = safeBuildDecorations(update.view);
            }
        }
    },
    {
        decorations: (v) => v.decorations,
    }
);

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
        ".cm-gutters": {
            backgroundColor: "#1e1e1e",
            color: "#5a5a5a",
            border: "none",
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
        ".cm-nomu-table-wrap": {
            margin: "0.5em 0",
        },
        ".cm-nomu-table": {
            borderCollapse: "collapse",
        },
        ".cm-nomu-table th, .cm-nomu-table td": {
            border: "1px solid #3a3a3a",
            padding: "6px 12px",
            cursor: "text",
        },
        ".cm-nomu-table th": {
            backgroundColor: "#2a2a2a",
            color: "#ffffff",
            fontWeight: "700",
        },
        ".cm-nomu-table td": {
            backgroundColor: "#242424",
        },
        ".cm-nomu-table-hidden-line": {
            fontSize: "1px",
            lineHeight: "1px",
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

function createEditorState(doc) {
    return EditorState.create({
        doc,
        extensions: [
            history(),
            keymap.of([...defaultKeymap, ...historyKeymap, indentWithTab]),
            markdown({ extensions: [GFM] }),
            livePreviewPlugin,
            nomuTheme,
            EditorView.lineWrapping,
            EditorView.updateListener.of((update) => {
                if (update.docChanged) {
                    notifyHostChanged();
                }
            }),
        ],
    });
}

window.__nomuInit = function () {
    view = new EditorView({
        state: createEditorState(""),
        parent: document.getElementById("editor"),
    });
};

window.__nomuSetContent = function (text) {
    view.dispatch({
        changes: { from: 0, to: view.state.doc.length, insert: text },
        selection: { anchor: 0 },
    });
};

window.__nomuGetContent = function () {
    return view.state.doc.toString();
};

window.__nomuInit();
