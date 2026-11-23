import test from "node:test";
import assert from "node:assert/strict";
import { detectCodeLanguage } from "../src/code.mjs";
test("code detection uses txt for empty and ordinary text", () => {
  assert.equal(detectCodeLanguage(""), "txt");
  assert.equal(detectCodeLanguage("你好，这是一段随记"), "txt");
});
test("common syntax is detected without asking for a language", () => {
  for (const [source, language] of [
    ["const answer = 42;", "javascript"],
    ['def greet():\n    return "hello"', "python"],
    ['{"answer":42}', "json"],
    ["SELECT name FROM users", "sql"],
    ["docker compose up -d", "bash"],
  ])
    assert.equal(detectCodeLanguage(source), language);
});
