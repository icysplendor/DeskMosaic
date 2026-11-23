// Vditor's scrollable editable surface is inside its padded IR wrapper. Wheel
// events on that wrapper cannot naturally scroll a child, so route only the
// side gutters to the page. Code bodies keep their own native scrolling.
export function installPageGutterScrolling(editor, onUserScroll) {
  document.getElementById("editor").addEventListener(
    "wheel",
    (event) => {
      if (
        event.ctrlKey ||
        event.metaKey ||
        event.shiftKey ||
        !event.deltaY ||
        Math.abs(event.deltaX) > Math.abs(event.deltaY)
      )
        return;
      const sourceMode = editor.vditor.currentMode === "sv";
      const root = sourceMode
        ? editor.vditor.sv.element
        : editor.vditor.ir.element;
      const frame = sourceMode ? root : root.parentElement;
      const rect = frame.getBoundingClientRect();
      if (
        event.clientX < rect.left ||
        event.clientX > rect.right ||
        event.clientY < rect.top ||
        event.clientY > rect.bottom
      )
        return;
      const surface = root.getBoundingClientRect();
      const style = getComputedStyle(root);
      const left =
        surface.left +
        root.clientLeft +
        (sourceMode ? parseFloat(style.paddingLeft) || 0 : 0);
      const right =
        surface.left +
        root.clientLeft +
        root.clientWidth -
        (sourceMode ? parseFloat(style.paddingRight) || 0 : 0);
      if (event.clientX >= left && event.clientX < right) return;
      const unit =
        event.deltaMode === 1
          ? parseFloat(style.lineHeight) || 20
          : event.deltaMode === 2
            ? root.clientHeight
            : 1;
      event.preventDefault();
      event.stopPropagation();
      onUserScroll();
      root.scrollTop += event.deltaY * unit;
    },
    { capture: true, passive: false },
  );
}
