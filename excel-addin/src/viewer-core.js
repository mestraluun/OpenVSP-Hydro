/* global Office, Excel */
/*
 * Cell Viewer — Excel access and messaging between the task pane and the
 * pop-out window.
 *
 * The pop-out window (an Office dialog) cannot call the Excel API itself, so it
 * sends requests to the task pane, which runs them and sends the results back.
 * Long messages are split into chunks.
 */
"use strict";

const MAX_CELL_CHARS = 32767;

const toText = (v) => (v === null || v === undefined ? "" : String(v));
const isFormula = (f) => typeof f === "string" && f.startsWith("=");

function colToIndex(col) {
  let n = 0;
  for (const ch of col.toUpperCase()) n = n * 26 + (ch.charCodeAt(0) - 64);
  return n - 1;
}

function indexToCol(i) {
  let s = "";
  for (i += 1; i > 0; i = Math.floor((i - 1) / 26)) s = String.fromCharCode(65 + ((i - 1) % 26)) + s;
  return s;
}

function sheetOf(ctx, name) {
  return name ? ctx.workbook.worksheets.getItem(name) : ctx.workbook.worksheets.getActiveWorksheet();
}

function lastRowOf(used) {
  return used.isNullObject ? 1 : used.rowIndex + used.rowCount;
}

/** Excel operations. Every method takes one plain-object argument and returns JSON-safe data. */
const ExcelBackend = {
  async getRow({ sheet, cols, row, header }) {
    return Excel.run(async (ctx) => {
      const ws = sheetOf(ctx, sheet);
      ws.load("name");
      const used = ws.getUsedRangeOrNullObject(true);
      used.load("rowIndex,rowCount");
      const items = cols.map((col) => {
        const r = ws.getRange(`${col}${row}`);
        r.load("values,formulas");
        let h = null;
        if (header) {
          h = ws.getRange(`${col}1`);
          h.load("values");
        }
        return { col, r, h };
      });
      await ctx.sync();
      return {
        sheet: ws.name,
        row,
        lastRow: lastRowOf(used),
        cells: items.map(({ col, r, h }) => ({
          col,
          value: toText(r.values[0][0]),
          isFormula: isFormula(r.formulas[0][0]),
          formula: toText(r.formulas[0][0]),
          header: h ? toText(h.values[0][0]) : "",
        })),
      };
    });
  },

  /** Writes text to one cell. Returns {conflict} if the cell changed since `expected` was read. */
  async writeCell({ sheet, col, row, text, expected, force }) {
    if (text.length > MAX_CELL_CHARS) {
      throw new Error(`Text is ${text.length} characters; an Excel cell holds at most ${MAX_CELL_CHARS}.`);
    }
    return Excel.run(async (ctx) => {
      const r = sheetOf(ctx, sheet).getRange(`${col}${row}`);
      r.load("values,formulas");
      await ctx.sync();
      if (isFormula(r.formulas[0][0])) throw new Error(`${col}${row} contains a formula and was not changed.`);
      const current = toText(r.values[0][0]);
      if (!force && expected !== undefined && current !== expected) return { conflict: true, current };
      r.values = [[text]];
      r.load("values");
      await ctx.sync();
      return { ok: true, value: toText(r.values[0][0]) };
    });
  },

  async getColumn({ sheet, col, header }) {
    return Excel.run(async (ctx) => {
      const ws = sheetOf(ctx, sheet);
      const used = ws.getUsedRangeOrNullObject(true);
      used.load("rowIndex,rowCount");
      await ctx.sync();
      const startRow = header ? 2 : 1;
      const last = lastRowOf(used);
      if (last < startRow) return { col, startRow, values: [] };
      const r = ws.getRange(`${col}${startRow}:${col}${last}`);
      r.load("values");
      await ctx.sync();
      return { col, startRow, values: r.values.map((x) => toText(x[0])) };
    });
  },

  async selectCell({ sheet, col, row }) {
    return Excel.run(async (ctx) => {
      const ws = sheetOf(ctx, sheet);
      ws.activate();
      ws.getRange(`${col}${row}`).select();
      await ctx.sync();
      return true;
    });
  },

  /** Active sheet and row, plus every column in the selection (Ctrl+click areas count too). */
  async getSelection() {
    return Excel.run(async (ctx) => {
      const ranges = ctx.workbook.getSelectedRanges();
      ranges.areas.load("items/columnIndex,items/columnCount");
      const active = ctx.workbook.getActiveCell();
      active.load("rowIndex,columnIndex");
      const ws = ctx.workbook.worksheets.getActiveWorksheet();
      ws.load("name");
      await ctx.sync();
      const idx = new Set();
      for (const a of ranges.areas.items) {
        for (let c = a.columnIndex; c < a.columnIndex + a.columnCount && idx.size <= 16; c++) idx.add(c);
      }
      return {
        sheet: ws.name,
        row: active.rowIndex + 1,
        activeCol: indexToCol(active.columnIndex),
        cols: [...idx].sort((a, b) => a - b).map(indexToCol),
      };
    });
  },
};

/* ---------- messaging ---------- */

const CHUNK = 16000;

function makeSender(sendRaw) {
  let seq = 0;
  return (msg) => {
    const s = JSON.stringify(msg);
    if (s.length <= CHUNK) return sendRaw(s);
    const cid = `${Date.now()}_${++seq}`;
    const n = Math.ceil(s.length / CHUNK);
    for (let i = 0; i < n; i++) sendRaw(JSON.stringify({ k: "chunk", cid, i, n, s: s.slice(i * CHUNK, (i + 1) * CHUNK) }));
  };
}

function makeReceiver(onMsg) {
  const parts = new Map();
  return (raw) => {
    let m;
    try { m = JSON.parse(raw); } catch { return; }
    if (m && m.k === "chunk") {
      let p = parts.get(m.cid);
      if (!p) parts.set(m.cid, (p = { n: m.n, got: 0, a: [] }));
      if (p.a[m.i] === undefined) { p.a[m.i] = m.s; p.got++; }
      if (p.got === p.n) {
        parts.delete(m.cid);
        try { onMsg(JSON.parse(p.a.join(""))); } catch { /* corrupt message */ }
      }
      return;
    }
    onMsg(m);
  };
}

/** Used in the task pane: calls Excel directly. */
function makeDirectBackend() {
  const listeners = [];
  Office.context.document.addHandlerAsync(Office.EventType.DocumentSelectionChanged, async () => {
    try {
      const sel = await ExcelBackend.getSelection();
      listeners.forEach((f) => f("selection", sel));
    } catch { /* chart or shape selected */ }
  });
  return {
    call: (method, args) => ExcelBackend[method](args || {}),
    on: (f) => listeners.push(f),
    emit: () => {},
  };
}

/** Used in the pop-out window: sends every Excel call to the task pane. */
function makeDialogBackend() {
  const pending = new Map();
  const listeners = [];
  let seq = 0;
  const send = makeSender((s) => Office.context.ui.messageParent(s));
  const recv = makeReceiver((m) => {
    if (!m) return;
    if (m.k === "res") {
      const p = pending.get(m.id);
      if (!p) return;
      pending.delete(m.id);
      m.ok ? p.resolve(m.result) : p.reject(new Error(m.error));
    } else if (m.k === "evt") {
      listeners.forEach((f) => f(m.name, m.data));
    }
  });
  Office.context.ui.addHandlerAsync(Office.EventType.DialogParentMessageReceived, (arg) => recv(arg.message));
  return {
    call: (method, args) =>
      new Promise((resolve, reject) => {
        const id = ++seq;
        const timer = setTimeout(() => {
          pending.delete(id);
          reject(new Error("Excel did not answer. Is the Cell Viewer task pane still open?"));
        }, 30000);
        pending.set(id, {
          resolve: (v) => { clearTimeout(timer); resolve(v); },
          reject: (e) => { clearTimeout(timer); reject(e); },
        });
        send({ k: "req", id, method, args: args || {} });
      }),
    on: (f) => listeners.push(f),
    emit: (name, data) => send({ k: "evt", name, data }),
  };
}

/** Task pane side of the pop-out: runs the window's requests against Excel. */
function serveDialog(dialog, handlers) {
  const send = makeSender((s) => dialog.messageChild(s));
  const recv = makeReceiver(async (m) => {
    if (!m) return;
    if (m.k === "req") {
      if (!Object.prototype.hasOwnProperty.call(ExcelBackend, m.method)) {
        send({ k: "res", id: m.id, ok: false, error: `Unknown method ${m.method}` });
        return;
      }
      try {
        send({ k: "res", id: m.id, ok: true, result: await ExcelBackend[m.method](m.args || {}) });
      } catch (e) {
        send({ k: "res", id: m.id, ok: false, error: (e && e.message) || String(e) });
      }
    } else if (m.k === "evt") {
      handlers.onEvent(m.name, m.data);
    }
  });
  dialog.addEventHandler(Office.EventType.DialogMessageReceived, (arg) => recv(arg.message));
  dialog.addEventHandler(Office.EventType.DialogEventReceived, () => handlers.onClosed());
  const emit = (name, data) => send({ k: "evt", name, data });
  emit("init"); // the window may not be listening yet; it also asks with "ready"
  return { emit };
}
