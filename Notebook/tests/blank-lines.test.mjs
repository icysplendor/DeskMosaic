import test from "node:test";
import assert from "node:assert/strict";
import {
  preserveBlankLines,
  normalizeTableSeparators,
} from "../src/blank-lines.mjs";

test("serializer table padding does not grow into user-authored blank paragraphs", () => {
  const table = "| a | b |\n| --- | --- |\n| 1 | 2 |\n";
  assert.equal(normalizeTableSeparators("A\n\n\n" + table), "A\n\n" + table);
  const actualSpacing = "A\n\n<br />\n\n<br />\n\n" + table;
  assert.equal(
    normalizeTableSeparators(actualSpacing.replace("\n\n|", "\n\n\n|")),
    actualSpacing,
  );
  const code = "```txt\nA\n\n\n" + table + "```\n";
  assert.equal(normalizeTableSeparators(code), code);
  assert.equal(normalizeTableSeparators("A\n\n\nB\n"), "A\n\n\nB\n");
});

test("normal paragraphs and trailing Markdown separator do not add spacers", () => {
  for (const value of ["", "A\n", "A\n\n", "A\n\nB\n", "# Title\n\nText\n"])
    assert.equal(preserveBlankLines(value), value);
});
test("extra top-level blank lines become portable break paragraphs", () => {
  assert.equal(
    preserveBlankLines("A\n\n\n\nB\n"),
    "A\n\n<br />\n\n<br />\n\nB\n",
  );
  const saved = "A\n\n<br />\n\n<br />\n\nB\n";
  assert.equal(preserveBlankLines(saved), saved);
});
test("code, nested lists, HTML and tables preserve internal bytes", () => {
  for (const value of [
    "```text\na\n\n\nb\n```\n",
    "    a\n\n\n    b\n",
    "- A\n\n\n- B\n",
    "> A\n>\n>\n> B\n",
    "<div>\na\n\n\nb\n</div>\n",
    "| A |\n| --- |\n| B |\n",
    "A\n\n\n[link]: https://example.test\n\nB\n",
  ])
    assert.equal(preserveBlankLines(value), value);
});
