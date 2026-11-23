import test from "node:test";
import assert from "node:assert/strict";
import { MarkdownHistory } from "../src/history.mjs";
test("first edit is undoable immediately", () => {
  const h = new MarkdownHistory("initial");
  h.record("first", 10);
  assert.equal(h.undo(), "initial");
  assert.equal(h.redo(), "first");
});
test("rapid typing groups without replacing initial state", () => {
  const h = new MarkdownHistory("");
  h.record("a", 10);
  h.record("ab", 100);
  h.record("abc", 300);
  assert.equal(h.undo(), "");
  assert.equal(h.redo(), "abc");
});
test("mode switch seals group and keeps undo history", () => {
  const h = new MarkdownHistory("initial");
  h.record("preview edit", 100);
  h.seal();
  h.record("source edit", 200);
  assert.equal(h.undo(), "preview edit");
  assert.equal(h.undo(), "initial");
});
test("new edit after undo removes abandoned redo branch", () => {
  const h = new MarkdownHistory("a");
  h.record("b", 100);
  h.seal();
  h.record("c", 200);
  h.undo();
  h.record("d", 300);
  assert.equal(h.canRedo, false);
  assert.equal(h.undo(), "b");
});
test("history is bounded while preserving cursor", () => {
  const h = new MarkdownHistory("a", 3);
  for (let n = 0; n < 5; n++) {
    h.seal();
    h.record(String(n), 1000 * n);
  }
  assert.equal(h.values.length, 3);
  assert.equal(h.undo(), "3");
  assert.equal(h.undo(), "2");
  assert.equal(h.canUndo, false);
});

test("render normalization adds no user edit and preserves redo", () => {
  const h = new MarkdownHistory("with attachment");
  h.record("removed attachment", 100);
  h.normalizeCurrent("removed attachment\n");
  assert.equal(h.undo(), "with attachment");
  h.normalizeCurrent("with attachment\n");
  assert.equal(h.redo(), "removed attachment\n");
});
