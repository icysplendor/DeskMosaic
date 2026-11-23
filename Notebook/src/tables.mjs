// Mutate the editor's native table DOM, retaining inline Markdown markers.
// Markdown requires exactly one header row; preserve that rule on row changes.
export function changeTable(table, rowIndex, columnIndex, command) {
  const rows = [...table.rows];
  const width = rows[0].cells.length;
  const emptyCell = (header, alignment = "") => {
    const cell = document.createElement(header ? "th" : "td");
    if (alignment) cell.setAttribute("align", alignment);
    cell.append(document.createElement("br"));
    return cell;
  };
  const alignments = [...rows[0].cells].map(
    (cell) => cell.getAttribute("align") || "",
  );
  if (
    command === "delete-table" ||
    (command === "delete-row" && rows.length === 1) ||
    (command === "delete-column" && width === 1)
  ) {
    const paragraph = document.createElement("p");
    paragraph.dataset.block = "0";
    paragraph.append(document.createElement("br"));
    table.replaceWith(paragraph);
    return null;
  }
  if (command === "row-before" || command === "row-after") {
    const position = rowIndex + (command === "row-after" ? 1 : 0);
    const row = document.createElement("tr");
    for (const alignment of alignments) row.append(emptyCell(false, alignment));
    // Body rows live in tbody even when inserting immediately below the header.
    const body = table.tBodies[0] || table.createTBody();
    if (position === 0) {
      // Move the previous header's contents into the first body row.
      body.prepend(rows[0]);
      table.tHead.append(row);
    } else if (rows[position]) rows[position].before(row);
    else body.append(row);
    rowIndex = position;
  } else if (command === "delete-row") {
    rows[rowIndex].remove();
    if (rowIndex === 0) table.tHead.append(table.rows[0]);
    rowIndex = Math.min(rowIndex, table.rows.length - 1);
  } else if (command === "column-before" || command === "column-after") {
    const position = columnIndex + (command === "column-after" ? 1 : 0);
    rows.forEach((row, index) => {
      const cell = emptyCell(index === 0);
      if (row.cells[position]) row.cells[position].before(cell);
      else row.append(cell);
    });
    columnIndex = position;
  } else if (command === "delete-column") {
    rows.forEach((row) => row.cells[columnIndex].remove());
    columnIndex = Math.min(columnIndex, width - 2);
  } else if (command.startsWith("align-")) {
    rows.forEach((row) =>
      row.cells[columnIndex].setAttribute("align", command.slice(6)),
    );
  } else if (command === "clear-cell") {
    rows[rowIndex].cells[columnIndex].replaceChildren(
      document.createElement("br"),
    );
  }
  // Preserve the cell's child nodes, including links, emphasis and images.
  [...table.rows].forEach((row, index) => {
    [...row.cells].forEach((cell) => {
      const tag = index === 0 ? "TH" : "TD";
      if (cell.tagName === tag) return;
      const replacement = document.createElement(tag);
      for (const attribute of cell.attributes)
        replacement.setAttribute(attribute.name, attribute.value);
      replacement.append(...cell.childNodes);
      cell.replaceWith(replacement);
    });
  });
  return { row: rowIndex, column: columnIndex };
}

export function tableMarkdown(rows, columns) {
  const line = "|" + "   |".repeat(columns) + "\n";
  return (
    "\n" +
    line +
    "|" +
    " --- |".repeat(columns) +
    "\n" +
    line.repeat(rows - 1) +
    "\n"
  );
}

export function chooseTableSize(anchor) {
  const popup = document.createElement("div");
  popup.id = "table-picker";
  popup.className = "editor-popover";
  popup.setAttribute("role", "dialog");
  popup.setAttribute("aria-label", "选择表格大小");
  popup.innerHTML = `<div class="table-picker-grid" aria-label="表格行列网格"></div>
    <div class="table-picker-footer"><output aria-live="polite">1 × 1</output><button type="button" data-custom>自定义…</button></div>
    <form hidden><label>行数（含表头）<input name="rows" type="number" min="1" max="100" value="3" required></label>
    <label>列数<input name="columns" type="number" min="1" max="20" value="3" required></label>
    <button class="primary" type="submit">插入表格</button></form>`;
  const grid = popup.querySelector(".table-picker-grid");
  let rows = 1,
    columns = 1;
  const paint = (row, column) => {
    rows = row;
    columns = column;
    popup.querySelector("output").textContent = `${columns} × ${rows}`;
    for (const button of grid.children)
      button.classList.toggle(
        "selected",
        +button.dataset.row <= rows && +button.dataset.column <= columns,
      );
  };
  const position = () => {
    const rect = anchor.getBoundingClientRect();
    popup.style.left =
      Math.max(8, Math.min(rect.left, innerWidth - popup.offsetWidth - 8)) +
      "px";
    popup.style.top =
      Math.max(
        8,
        Math.min(rect.bottom + 6, innerHeight - popup.offsetHeight - 8),
      ) + "px";
  };
  document.body.append(popup);
  return new Promise((resolve) => {
    const finish = (size) => {
      document.removeEventListener("pointerdown", outside, true);
      document.removeEventListener("keydown", escape, true);
      window.removeEventListener("resize", cancel);
      popup.remove();
      resolve(size);
    };
    const cancel = () => finish(null);
    const outside = (event) => {
      if (!popup.contains(event.target)) cancel();
    };
    const escape = (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        cancel();
        anchor.focus();
      }
    };
    for (let row = 1; row <= 10; row++)
      for (let column = 1; column <= 10; column++) {
        const button = document.createElement("button");
        button.type = "button";
        button.dataset.row = row;
        button.dataset.column = column;
        button.setAttribute("aria-label", `${row} 行 × ${column} 列`);
        button.onpointerenter = button.onfocus = () => paint(row, column);
        button.onclick = () => finish({ rows: row, columns: column });
        button.onkeydown = (event) => {
          const moves = {
            ArrowRight: [0, 1],
            ArrowLeft: [0, -1],
            ArrowUp: [-1, 0],
            ArrowDown: [1, 0],
          };
          if (!moves[event.key]) return;
          event.preventDefault();
          const [dy, dx] = moves[event.key];
          grid.children[
            (Math.min(10, Math.max(1, row + dy)) - 1) * 10 +
              Math.min(10, Math.max(1, column + dx)) -
              1
          ].focus();
        };
        grid.append(button);
      }
    popup.querySelector("[data-custom]").onclick = () => {
      grid.hidden = true;
      popup.querySelector("form").hidden = false;
      popup.querySelector("input").focus();
      position();
    };
    popup.querySelector("form").onsubmit = (event) => {
      event.preventDefault();
      const fields = event.currentTarget.elements;
      finish({ rows: +fields.rows.value, columns: +fields.columns.value });
    };
    document.addEventListener("pointerdown", outside, true);
    document.addEventListener("keydown", escape, true);
    window.addEventListener("resize", cancel);
    paint(1, 1);
    position();
    grid.firstChild.focus();
  });
}
