// One Markdown history across both editor modes. Vditor keeps separate per-mode
// histories, which cannot restore attachments reliably after a mode switch.
export class MarkdownHistory {
  constructor(value, limit = 100) {
    this.values = [value];
    this.cursor = 0;
    this.limit = limit;
    this.lastEdit = 0;
    this.groupOpen = false;
  }
  get value() {
    return this.values[this.cursor];
  }
  get canUndo() {
    return this.cursor > 0;
  }
  get canRedo() {
    return this.cursor < this.values.length - 1;
  }
  record(value, time = Date.now()) {
    if (value === this.value) return;
    this.values.splice(this.cursor + 1);
    if (this.groupOpen && time - this.lastEdit < 500 && this.cursor > 0)
      this.values[this.cursor] = value;
    else {
      this.values.push(value);
      this.cursor++;
    }
    if (this.values.length > this.limit) {
      this.values.shift();
      this.cursor--;
    }
    this.lastEdit = time;
    this.groupOpen = true;
  }
  normalizeCurrent(value) {
    this.values[this.cursor] = value;
    this.seal();
  }
  seal() {
    this.groupOpen = false;
  }
  undo() {
    this.seal();
    if (this.canUndo) this.cursor--;
    return this.value;
  }
  redo() {
    this.seal();
    if (this.canRedo) this.cursor++;
    return this.value;
  }
}
