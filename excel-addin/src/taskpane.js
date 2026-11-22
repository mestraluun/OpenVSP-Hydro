/* global Office, Excel */
"use strict";

const statusEl = () => document.getElementById("status");

function setStatus(msg, isError = false) {
  const el = statusEl();
  el.textContent = msg;
  el.classList.toggle("error", isError);
}

/**
 * Returns the selected range clipped to the sheet's used range, so that
 * selecting whole columns does not pull a million empty rows into memory.
 */
async function getWorkingRange(context, props = "address,values,formulas,rowCount,columnCount") {
  const selected = context.workbook.getSelectedRange();
  const used = selected.worksheet.getUsedRange(true);
  const range = selected.getIntersectionOrNullObject(used);
  range.load(props);
  await context.sync();
  if (range.isNullObject) throw new Error("The selection is empty.");
  return range;
}

const isFormula = (f) => typeof f === "string" && f.startsWith("=");

/** Applies fn to every constant text cell; formulas are left untouched. */
async function mapText(fn, label) {
  await Excel.run(async (context) => {
    const range = await getWorkingRange(context);
    let changed = 0;
    const out = range.formulas.map((row, r) =>
      row.map((f, c) => {
        const v = range.values[r][c];
        if (isFormula(f) || typeof v !== "string") return f;
        const nv = fn(v);
        if (nv !== v) changed++;
        return nv;
      })
    );
    range.formulas = out;
    await context.sync();
    setStatus(`${label}: ${changed} cell(s) changed in ${range.address}.`);
  });
}

const NUMERIC = /^[-+]?(\d{1,3}(,\d{3})+|\d*)(\.\d+)?(e[-+]?\d+)?%?$/i;

const actions = {
  async stats() {
    await Excel.run(async (context) => {
      const range = await getWorkingRange(context, "address,values");
      const nums = [];
      let cells = 0, blanks = 0, text = 0;
      for (const row of range.values) {
        for (const v of row) {
          cells++;
          if (v === "" || v === null) blanks++;
          else if (typeof v === "number") nums.push(v);
          else text++;
        }
      }
      const sum = nums.reduce((a, b) => a + b, 0);
      const fmt = (n) => (Number.isFinite(n) ? n.toLocaleString(undefined, { maximumFractionDigits: 6 }) : "—");
      const sorted = [...nums].sort((a, b) => a - b);
      const median = sorted.length
        ? sorted.length % 2 ? sorted[(sorted.length - 1) / 2]
          : (sorted[sorted.length / 2 - 1] + sorted[sorted.length / 2]) / 2
        : NaN;
      const mean = nums.length ? sum / nums.length : NaN;
      const sd = nums.length > 1
        ? Math.sqrt(nums.reduce((a, b) => a + (b - mean) ** 2, 0) / (nums.length - 1))
        : NaN;
      const rows = [
        ["Range", range.address],
        ["Cells", fmt(cells)],
        ["Numbers", fmt(nums.length)],
        ["Text", fmt(text)],
        ["Blank", fmt(blanks)],
        ["Sum", fmt(sum)],
        ["Average", fmt(mean)],
        ["Median", fmt(median)],
        ["Std dev", fmt(sd)],
        ["Min", fmt(nums.length ? sorted[0] : NaN)],
        ["Max", fmt(nums.length ? sorted[sorted.length - 1] : NaN)],
      ];
      const dl = document.getElementById("stats");
      dl.replaceChildren(...rows.flatMap(([k, v]) => {
        const dt = document.createElement("dt"); dt.textContent = k;
        const dd = document.createElement("dd"); dd.textContent = v;
        return [dt, dd];
      }));
      dl.hidden = false;
      setStatus("Stats updated.");
    });
  },

  trim: () => mapText((s) => s.replace(/\s+/g, " ").trim(), "Trim"),
  upper: () => mapText((s) => s.toUpperCase(), "UPPER"),
  lower: () => mapText((s) => s.toLowerCase(), "lower"),
  proper: () => mapText((s) => s.toLowerCase().replace(/(^|[\s\-'(])(\p{L})/gu, (m, p, ch) => p + ch.toUpperCase()), "Proper"),

  async textToNumber() {
    await Excel.run(async (context) => {
      const range = await getWorkingRange(context);
      let changed = 0;
      range.formulas = range.formulas.map((row, r) =>
        row.map((f, c) => {
          const v = range.values[r][c];
          if (isFormula(f) || typeof v !== "string") return f;
          const s = v.trim();
          if (!s || !NUMERIC.test(s)) return f;
          let n = Number(s.replace(/,/g, "").replace(/%$/, ""));
          if (s.endsWith("%")) n /= 100;
          if (!Number.isFinite(n)) return f;
          changed++;
          return n;
        })
      );
      await context.sync();
      setStatus(`Converted ${changed} text cell(s) to numbers.`);
    });
  },

  async dedupe() {
    await Excel.run(async (context) => {
      const range = await getWorkingRange(context, "address,columnCount");
      const cols = Array.from({ length: range.columnCount }, (_, i) => i);
      const result = range.removeDuplicates(cols, true);
      result.load("removed,uniqueRemaining");
      await context.sync();
      setStatus(`Removed ${result.removed} duplicate row(s); ${result.uniqueRemaining} unique remain (first row treated as header).`);
    });
  },

  async blanks() {
    await Excel.run(async (context) => {
      const range = await getWorkingRange(context, "address,values");
      let deleted = 0;
      // Delete bottom-up so earlier row indexes stay valid.
      for (let r = range.values.length - 1; r >= 0; r--) {
        if (range.values[r].every((v) => v === "" || v === null)) {
          range.getRow(r).delete(Excel.DeleteShiftDirection.up);
          deleted++;
        }
      }
      await context.sync();
      setStatus(`Deleted ${deleted} blank row(s).`);
    });
  },

  async highlight() {
    const color = document.getElementById("fill").value;
    await Excel.run(async (context) => {
      const range = context.workbook.getSelectedRange();
      range.format.fill.color = color;
      await context.sync();
      setStatus(`Filled selection with ${color}.`);
    });
  },

  async table() {
    await Excel.run(async (context) => {
      const range = await getWorkingRange(context, "address");
      const table = range.worksheet.tables.add(range, true);
      table.load("name");
      await context.sync();
      range.format.autofitColumns();
      await context.sync();
      setStatus(`Created table ${table.name}.`);
    });
  },

  async autofit() {
    await Excel.run(async (context) => {
      const range = await getWorkingRange(context, "address");
      range.format.autofitColumns();
      await context.sync();
      setStatus("Columns autofitted.");
    });
  },

  async freeze() {
    await Excel.run(async (context) => {
      context.workbook.worksheets.getActiveWorksheet().freezePanes.freezeRows(1);
      await context.sync();
      setStatus("Top row frozen.");
    });
  },

  async clearFormat() {
    await Excel.run(async (context) => {
      context.workbook.getSelectedRange().clear(Excel.ClearApplyTo.formats);
      await context.sync();
      setStatus("Formatting cleared.");
    });
  },

  async timestamp() {
    await Excel.run(async (context) => {
      const cell = context.workbook.getActiveCell();
      const now = new Date();
      // Excel serial date in local time (static value, unlike =NOW()).
      const serial = (now.getTime() - now.getTimezoneOffset() * 60000) / 86400000 + 25569;
      cell.values = [[serial]];
      cell.numberFormat = [["yyyy-mm-dd hh:mm:ss"]];
      cell.format.autofitColumns();
      await context.sync();
      setStatus("Timestamp inserted.");
    });
  },

  async sample() {
    await Excel.run(async (context) => {
      const sheets = context.workbook.worksheets;
      sheets.load("items/name");
      await context.sync();
      const names = new Set(sheets.items.map((s) => s.name));
      let name = "Sample", i = 2;
      while (names.has(name)) name = `Sample ${i++}`;
      const ws = sheets.add(name);
      const data = [
        ["Region", "Product", "Units", "Unit price", "Revenue"],
        ["North", "Widget", 120, 4.5],
        ["South", "Widget", 95, 4.5],
        ["East", "Gadget", 60, 12],
        ["West", "Gadget", 80, 12],
        ["North", "Gizmo", 40, 25],
        ["South", "Gizmo", 55, 25],
      ];
      const rows = data.map((row, r) => (r === 0 ? row : [...row, `=C${r + 1}*D${r + 1}`]));
      const range = ws.getRangeByIndexes(0, 0, rows.length, 5);
      range.formulas = rows;
      ws.getRange(`D2:E${rows.length}`).numberFormat = Array(rows.length - 1).fill(["#,##0.00", "#,##0.00"]);
      ws.tables.add(range, true).name = name.replace(/\W/g, "") + "Table";
      range.format.autofitColumns();
      ws.activate();
      await context.sync();
      setStatus(`Added sheet "${name}".`);
    });
  },
};

async function refreshSelection() {
  try {
    await Excel.run(async (context) => {
      const range = context.workbook.getSelectedRange();
      range.load("address,rowCount,columnCount");
      await context.sync();
      document.getElementById("selection").textContent =
        `${range.address} — ${range.rowCount} × ${range.columnCount}`;
    });
  } catch {
    /* selection may be a chart or shape; ignore */
  }
}

Office.onReady((info) => {
  if (info.host !== Office.HostType.Excel) {
    setStatus("This add-in runs in Excel.", true);
    return;
  }
  document.querySelectorAll("button[data-action]").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const fn = actions[btn.dataset.action];
      btn.disabled = true;
      try {
        await fn();
      } catch (err) {
        console.error(err);
        setStatus(err.message || String(err), true);
      } finally {
        btn.disabled = false;
      }
    });
  });
  Office.context.document.addHandlerAsync(Office.EventType.DocumentSelectionChanged, refreshSelection);
  refreshSelection();
});
