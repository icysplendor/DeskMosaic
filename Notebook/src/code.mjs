export const CODE_LANGUAGES = [
  "txt",
  "javascript",
  "typescript",
  "python",
  "json",
  "bash",
  "sql",
  "html",
  "css",
  "yaml",
  "java",
  "c",
  "cpp",
  "csharp",
  "go",
  "rust",
  "swift",
  "markdown",
];
export function detectCodeLanguage(text, highlighter) {
  if (!text.trim()) return "txt";
  try {
    JSON.parse(text);
    if (/^[\[{]/.test(text.trim())) return "json";
  } catch {}
  if (
    /^\s*(?:def\s+\w+\(|from\s+\w+\s+import\b|import\s+\w+|class\s+\w+.*:)/m.test(
      text,
    )
  )
    return "python";
  if (/\b(?:const|let)\s+\w+\s*=|\bfunction\s+\w+\s*\(|=>/.test(text))
    return /\binterface\s+\w+|:\s*(?:string|number|boolean)\b/.test(text)
      ? "typescript"
      : "javascript";
  if (
    /^\s*(?:SELECT\b.*\bFROM|CREATE\s+TABLE|INSERT\s+INTO|UPDATE\b.*\bSET)/im.test(
      text,
    )
  )
    return "sql";
  if (
    /^#!.*(?:bash|sh)|^\s*(?:docker\s|sudo\s|npm\s|curl\s|export\s+\w+=)/m.test(
      text,
    )
  )
    return "bash";
  if (/<(?:!DOCTYPE html|html|div|body|head|script)\b/i.test(text))
    return "html";
  try {
    const result = highlighter?.highlightAuto(
      text,
      CODE_LANGUAGES.filter((x) => x !== "txt" && highlighter.getLanguage(x)),
    );
    if (
      result?.language &&
      result.relevance >= 5 &&
      result.relevance > (result.secondBest?.relevance ?? 0) + 1
    )
      return result.language;
  } catch {}
  return "txt";
}
