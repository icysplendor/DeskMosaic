// Stable note IDs keep a move/recycle destination and its old location in the
// same item. Files are checkpoints; a note is the user-visible success unit.
export function syncItem(path) {
  const parts = path.split("/");
  const bookId = parts[0] === ".deleted" ? null : parts[1];
  if (
    parts[0] !== ".deleted" &&
    parts.length === 3 &&
    ["book.json", "deleted.json"].includes(parts[2])
  )
    return { key: `book:${bookId}`, id: bookId, type: "book", bookId };
  const noteId = parts[0] === ".deleted" ? parts[1] : parts[2];
  return { key: `note:${noteId}`, id: noteId, type: "note", bookId };
}
export function syncIssue(error, path) {
  return {
    path: error.path || path,
    code: error.code || "SYNC_ITEM_ERROR",
    status: error.status || 0,
    message: error.message,
    retryable: ["DAV_LOCKED", "SYNC_RETRY", "LOCAL_CHANGED"].includes(
      error.code,
    ),
    retryAfter: error.retryAfter || 0,
  };
}
