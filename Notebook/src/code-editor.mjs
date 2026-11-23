import { basicSetup } from "codemirror";
import { EditorView, keymap } from "@codemirror/view";
import { EditorState, Compartment, Prec } from "@codemirror/state";
import {
  HighlightStyle,
  syntaxHighlighting,
  StreamLanguage,
  indentUnit,
  foldedRanges,
  unfoldEffect,
} from "@codemirror/language";
import {
  indentWithTab,
  undo,
  redo,
  undoDepth,
  redoDepth,
} from "@codemirror/commands";
import { openSearchPanel, gotoLine, searchPanelOpen } from "@codemirror/search";
import { tags } from "@lezer/highlight";
import { javascript } from "@codemirror/lang-javascript";
import { json } from "@codemirror/lang-json";
import { python } from "@codemirror/lang-python";
import { html } from "@codemirror/lang-html";
import { css } from "@codemirror/lang-css";
import { sql } from "@codemirror/lang-sql";
import { markdown } from "@codemirror/lang-markdown";
import { yaml } from "@codemirror/lang-yaml";
import { cpp } from "@codemirror/lang-cpp";
import { rust } from "@codemirror/lang-rust";
import { shell } from "@codemirror/legacy-modes/mode/shell";
import { go } from "@codemirror/legacy-modes/mode/go";
import { swift } from "@codemirror/legacy-modes/mode/swift";
import { java, csharp } from "@codemirror/legacy-modes/mode/clike";
import { CODE_LANGUAGES, detectCodeLanguage } from "./code.mjs";

const $ = (id) => document.getElementById(id);
const languageConfig = new Compartment(),
  wrapConfig = new Compartment();
let view,
  originalDoc,
  originalLanguage,
  currentLanguage,
  total = 0,
  dirty = false,
  sequence = 0,
  detectionTimer;
const requests = new Map();
const count = (text) => {
  let n = 0;
  for (const _ of text) n++;
  return n;
};
function send(op, args = {}, id) {
  const message = { op, args, ...(id ? { id } : {}) };
  if (window.webkit?.messageHandlers.codeEditor)
    window.webkit.messageHandlers.codeEditor.postMessage(message);
  else if (window.chrome?.webview) window.chrome.webview.postMessage(message);
}
function request(op, args) {
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    requests.set(id, { resolve, reject });
    send(op, args, id);
  });
}
window.nativeReply = (id, value, error) => {
  const call = requests.get(id);
  if (!call) return;
  requests.delete(id);
  error ? call.reject(new Error(error)) : call.resolve(value);
};
const parsers = {
  javascript: () => javascript(),
  typescript: () => javascript({ typescript: true }),
  json,
  python,
  html,
  css,
  sql,
  markdown,
  yaml,
  c: cpp,
  cpp,
  rust,
  bash: () => StreamLanguage.define(shell),
  go: () => StreamLanguage.define(go),
  swift: () => StreamLanguage.define(swift),
  java: () => StreamLanguage.define(java),
  csharp: () => StreamLanguage.define(csharp),
};
function languageExtension(value, text) {
  return parsers[value || detectCodeLanguage(text)]?.() || [];
}
function status() {
  const state = view.state,
    head = state.selection.main.head;
  const line = state.doc.lineAt(head);
  $("cursor-position").textContent =
    `行 ${line.number}，列 ${count(state.sliceDoc(line.from, head)) + 1}`;
  $("line-count").textContent = `${state.doc.lines} 行`;
  $("char-count").textContent = `${total} 字符`;
  const selected = state.selection.ranges.reduce(
    (n, range) => n + count(state.sliceDoc(range.from, range.to)),
    0,
  );
  $("selection-count").textContent = `选中 ${selected} 字符`;
  dirty = !state.doc.eq(originalDoc) || currentLanguage !== originalLanguage;
  $("edit-state").textContent = dirty ? "● 尚未保存" : "未修改";
  $("edit-state").dataset.dirty = String(dirty);
  $("undo").disabled = undoDepth(state) === 0;
  $("redo").disabled = redoDepth(state) === 0;
}
function save() {
  if (!view) return;
  send("save", { text: view.state.doc.toString(), language: currentLanguage });
}
function requestClose() {
  if (!view || !dirty) send("discard");
  else if (!$("discard-dialog").open) $("discard-dialog").showModal();
}
window.codeEditorRequestClose = requestClose;
window.codeEditorSave = save;
window.codeEditorInitialize = (args) => {
  if (view) return;
  document.documentElement.dataset.theme = args.dark ? "dark" : "light";
  originalLanguage = currentLanguage = args.language || "";
  for (const value of [
    "",
    ...CODE_LANGUAGES,
    ...(currentLanguage && !CODE_LANGUAGES.includes(currentLanguage)
      ? [currentLanguage]
      : []),
  ]) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = value || "自动识别";
    $("language").append(option);
  }
  $("language").value = currentLanguage;
  $("wrap").checked = args.wrap !== false;
  const text = (args.text || "").replace(/\r\n?/g, "\n");
  const highlight = HighlightStyle.define([
    { tag: tags.keyword, color: "var(--syntax-keyword)" },
    {
      tag: [tags.string, tags.special(tags.string)],
      color: "var(--syntax-string)",
    },
    { tag: [tags.number, tags.bool, tags.null], color: "var(--syntax-number)" },
    { tag: tags.comment, color: "var(--syntax-comment)", fontStyle: "italic" },
    { tag: tags.function(tags.variableName), color: "var(--syntax-function)" },
  ]);
  view = new EditorView({
    parent: $("code-editor"),
    state: EditorState.create({
      doc: text,
      extensions: [
        Prec.highest(
          keymap.of([
            {
              key: "Mod-s",
              run: () => {
                save();
                return true;
              },
            },
            { key: "Mod-g", run: gotoLine },
            indentWithTab,
          ]),
        ),
        basicSetup,
        indentUnit.of("    "),
        EditorState.phrases.of({
          Find: "查找",
          Replace: "替换",
          next: "下一个",
          previous: "上一个",
          all: "全部",
          replace: "替换",
          "replace all": "全部替换",
          "match case": "区分大小写",
          regexp: "正则表达式",
          "by word": "全词匹配",
          close: "关闭",
          "Go to line": "跳转到行",
          go: "跳转",
          "Fold line": "折叠代码",
          "Unfold line": "展开代码",
        }),
        EditorView.contentAttributes.of({
          "aria-label": "代码正文",
          spellcheck: "false",
        }),
        EditorView.theme({}, { dark: !!args.dark }),
        syntaxHighlighting(highlight),
        languageConfig.of(languageExtension(currentLanguage, text)),
        wrapConfig.of(args.wrap !== false ? EditorView.lineWrapping : []),
        EditorView.updateListener.of((update) => {
          if (update.docChanged) {
            update.changes.iterChanges((from, to, _fromB, _toB, inserted) => {
              total +=
                count(inserted.toString()) -
                count(update.startState.sliceDoc(from, to));
            });
            if (!currentLanguage) {
              clearTimeout(detectionTimer);
              detectionTimer = setTimeout(
                () =>
                  view.dispatch({
                    effects: languageConfig.reconfigure(
                      languageExtension("", view.state.doc.toString()),
                    ),
                  }),
                250,
              );
            }
          }
          if (originalDoc) status();
        }),
      ],
    }),
  });
  originalDoc = view.state.doc;
  total = count(text);
  status();
  view.focus();
  // WebKit can suppress click on a non-editable widget inside editable code.
  // Handle the fold placeholder before its default text-selection behavior.
  $("code-editor").addEventListener(
    "pointerdown",
    (event) => {
      const placeholder = event.target.closest(".cm-foldPlaceholder");
      if (!placeholder || event.button !== 0) return;
      const pos = view.posAtDOM(placeholder);
      const effects = [];
      foldedRanges(view.state).between(pos, pos, (from, to) =>
        effects.push(unfoldEffect.of({ from, to })),
      );
      if (effects.length) {
        event.preventDefault();
        event.stopPropagation();
        view.dispatch({ effects });
        view.focus();
      }
    },
    true,
  );
  $("language").onchange = () => {
    clearTimeout(detectionTimer);
    currentLanguage = $("language").value;
    view.dispatch({
      effects: languageConfig.reconfigure(
        languageExtension(currentLanguage, view.state.doc.toString()),
      ),
    });
    status();
    view.focus();
  };
  $("wrap").onchange = () => {
    view.dispatch({
      effects: wrapConfig.reconfigure(
        $("wrap").checked ? EditorView.lineWrapping : [],
      ),
    });
    view.focus();
  };
  $("font-size").onchange = () => {
    document.documentElement.style.setProperty(
      "--editor-font-size",
      $("font-size").value + "px",
    );
    view.requestMeasure();
    view.focus();
  };
  $("find").onclick = () => openSearchPanel(view);
  $("go-line").onclick = () => gotoLine(view);
  $("undo").onclick = () => {
    undo(view);
    view.focus();
  };
  $("redo").onclick = () => {
    redo(view);
    view.focus();
  };
  $("save").onclick = save;
  $("cancel").onclick = requestClose;
  $("copy-all").onclick = async () => {
    try {
      await request("clipboard", { text: view.state.doc.toString() });
      $("editor-message").textContent = "已复制全部代码";
    } catch (error) {
      $("editor-message").textContent = error.message;
    }
  };
};
$("keep-editing").onclick = () => {
  $("discard-dialog").close();
  view?.focus();
};
$("discard").onclick = () => send("discard");
document.addEventListener("keydown", (event) => {
  if (
    (event.metaKey || event.ctrlKey) &&
    !event.defaultPrevented &&
    !event.altKey &&
    event.key.toLowerCase() === "s" &&
    !event.isComposing
  ) {
    event.preventDefault();
    save();
    return;
  }
  if (
    event.key === "Escape" &&
    !event.defaultPrevented &&
    !event.isComposing &&
    !$("discard-dialog").open &&
    view &&
    !searchPanelOpen(view.state)
  ) {
    event.preventDefault();
    requestClose();
  }
});
send("ready");
