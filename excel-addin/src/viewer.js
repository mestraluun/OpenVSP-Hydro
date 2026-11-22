/* global Office, makeDirectBackend, makeDialogBackend, serveDialog, colToIndex, MAX_CELL_CHARS */
"use strict";

const DEFAULTS = { sheet: null, cols: [], row: 2, header: true, follow: true, syncSel: true, autosave: true, font: 14 };
const CFG_KEY = "cellViewer.cfg";
const LIST_LIMIT = 2000;

const $ = (id) => document.getElementById(id);

function loadCfg() {
  try { return JSON.parse(localStorage.getItem(CFG_KEY)) || null; } catch { return null; }
}

function parseCols(text) {
  const cols = text.toUpperCase().split(/[\s,;]+/).filter(Boolean);
  if (!cols.length || cols.length > 4 || cols.some((c) => !/^[A-Z]{1,3}$/.test(c) || colToIndex(c) > 16383)) {
    throw new Error("Enter 1 to 4 column letters, for example: A, C, F");
  }
  return [...new Set(cols)];
}

async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    const ta = document.createElement("textarea");
    ta.value = text;
    ta.style.position = "fixed";
    ta.style.opacity = "0";
    document.body.appendChild(ta);
    ta.select();
    const ok = document.execCommand("copy");
    ta.remove();
    return ok;
  }
}

/**
 * The viewer UI. `backend` provides call(method, args) for Excel access and
 * on(fn) for selection events.
 */
function createViewer(backend, opts = {}) {
  const cfg = { ...DEFAULTS, ...(opts.cfg || {}) };
  let data = null;          // last getRow result
  let blocks = [];          // [{col, el, ta, label, ...}]
  let layoutKey = "";
  let suspended = false;
  let chain = Promise.resolve();
  let wantRow = null;
  let lastSelfSelect = 0;
  let browse = null;        // {col, startRow, values}
  let previewRow = null;
  let forceNext = false;
  let stateTimer = null;

  const minRow = () => (cfg.header ? 2 : 1);
  const target = () => blocks[blocks.length - 1];

  function status(msg, isError = false) {
    $("status").textContent = msg;
    $("status").classList.toggle("error", isError);
  }

  function persist() {
    try { localStorage.setItem(CFG_KEY, JSON.stringify(cfg)); } catch { /* storage unavailable */ }
    if (opts.onState) opts.onState(state());
  }

  function state() {
    const drafts = {};
    for (const b of dirtyBlocks()) drafts[b.col] = b.ta.value;
    return { cfg: { ...cfg }, drafts };
  }

  function scheduleState() {
    clearTimeout(stateTimer);
    stateTimer = setTimeout(() => opts.onState && opts.onState(state()), 600);
  }

  function applyFont() {
    document.documentElement.style.setProperty("--fs", `${cfg.font}px`);
  }

  function syncSettingsUI() {
    $("cols").value = cfg.cols.join(", ");
    $("header").checked = cfg.header;
    $("follow").checked = cfg.follow;
    $("syncSel").checked = cfg.syncSel;
    $("autosave").checked = cfg.autosave;
    $("where").textContent = cfg.cols.length
      ? `${cfg.sheet || "active sheet"} · ${cfg.cols.join(", ")}`
      : "no columns chosen";
    $("hint").hidden = cfg.cols.length > 0;
    $("bcol").textContent = cfg.cols.length ? cfg.cols[cfg.cols.length - 1] : "";
  }

  /* ---------- cell blocks ---------- */

  function buildBlocks() {
    const key = cfg.cols.join(",");
    if (key === layoutKey) return;
    layoutKey = key;
    const host = $("cells");
    host.replaceChildren();
    blocks = cfg.cols.map((col, i) => {
      const el = document.createElement("section");
      el.className = "cell" + (i === cfg.cols.length - 1 ? " target" : "");
      el.innerHTML =
        '<div class="clabel"><b class="addr"></b><span class="hdr"></span>' +
        '<span class="badge" hidden>formula · read-only</span><span class="count"></span></div>' +
        '<textarea spellcheck="true"></textarea>';
      const ta = el.querySelector("textarea");
      const b = { col, el, ta, addr: el.querySelector(".addr"), hdr: el.querySelector(".hdr"),
        badge: el.querySelector(".badge"), count: el.querySelector(".count"), cell: null };
      ta.addEventListener("input", () => { refreshDirty(); scheduleState(); });
      host.appendChild(el);
      return b;
    });
    browse = null;
    if ($("browser").open) loadColumn();
  }

  function fillBlocks(drafts) {
    blocks.forEach((b, i) => {
      const c = data.cells[i];
      b.cell = c;
      b.addr.textContent = `${b.col}${data.row}`;
      b.hdr.textContent = c.header;
      b.hdr.title = c.header;
      b.badge.hidden = !c.isFormula;
      b.ta.readOnly = c.isFormula;
      b.ta.title = c.isFormula ? c.formula : "";
      b.ta.value = drafts && drafts[b.col] !== undefined && !c.isFormula ? drafts[b.col] : c.value;
      b.ta.scrollTop = 0;
    });
    refreshDirty();
  }

  const dirtyBlocks = () => blocks.filter((b) => b.cell && !b.cell.isFormula && b.ta.value !== b.cell.value);
  const isDirty = () => dirtyBlocks().length > 0;

  function refreshDirty() {
    for (const b of blocks) {
      const d = b.cell && !b.cell.isFormula && b.ta.value !== b.cell.value;
      b.el.classList.toggle("dirty", !!d);
      const n = b.ta.value.length;
      b.count.textContent = `${n.toLocaleString()} chars` + (n > MAX_CELL_CHARS ? " — too long!" : "");
      b.count.classList.toggle("over", n > MAX_CELL_CHARS);
    }
    const n = dirtyBlocks().length;
    $("dirty").textContent = n ? `${n} unsaved` : "";
    $("save").disabled = !n;
    $("revert").disabled = !n;
  }

  /* ---------- navigation ---------- */

  function nav(row, o = {}) {
    wantRow = row;
    chain = chain.then(() => go(row, o)).catch((e) => status(e.message || String(e), true));
    return chain;
  }

  async function go(row, { force = false, discard = false, drafts = null } = {}) {
    if (!cfg.cols.length) return;
    row = Math.max(minRow(), Math.round(Number(row)) || minRow());
    if (!force && data && row === data.row) { wantRow = null; return; }
    if (!discard && isDirty()) {
      if (!cfg.autosave) {
        $("row").value = data ? data.row : cfg.row;
        wantRow = null;
        status("You have unsaved changes. Save or Revert before changing row.", true);
        return;
      }
      if (!(await saveAll())) { wantRow = null; return; }
    }
    hideConflict();
    buildBlocks();
    const d = await backend.call("getRow", { sheet: cfg.sheet, cols: cfg.cols, row, header: cfg.header });
    data = d;
    cfg.sheet = d.sheet;
    cfg.row = row;
    wantRow = null;
    fillBlocks(drafts);
    $("row").value = row;
    $("of").textContent = `of ${d.lastRow}`;
    $("prev").disabled = row <= minRow();
    syncSettingsUI();
    markCurrentInList();
    persist();
    if (cfg.syncSel) {
      lastSelfSelect = Date.now();
      backend.call("selectCell", { sheet: cfg.sheet, col: cfg.cols[cfg.cols.length - 1], row }).catch(() => {});
    }
    if (!drafts) status(`Row ${row}`);
  }

  const step = (delta) => nav((wantRow ?? (data ? data.row : cfg.row)) + delta);

  /* ---------- saving ---------- */

  async function saveAll(force = forceNext) {
    forceNext = false;
    const dirty = dirtyBlocks();
    for (const b of dirty) {
      const res = await backend.call("writeCell", {
        sheet: cfg.sheet, col: b.col, row: data.row, text: b.ta.value, expected: b.cell.value, force,
      });
      if (res.conflict) {
        showConflict(b, res.current);
        return false;
      }
      b.cell.value = res.value;
      if (b.ta.value !== res.value) b.ta.value = res.value;
      updateListValue(b.col, data.row, res.value);
    }
    refreshDirty();
    hideConflict();
    if (dirty.length) status(`Saved ${dirty.map((b) => b.col + data.row).join(", ")}.`);
    scheduleState();
    return true;
  }

  function showConflict(b, current) {
    $("conflictMsg").textContent =
      `${b.col}${data.row} was changed in Excel after the viewer loaded it ` +
      `(it now starts “${current.slice(0, 60)}${current.length > 60 ? "…" : ""}”).`;
    $("conflict").hidden = false;
    status("Not saved: the cell changed in Excel.", true);
  }

  function hideConflict() { $("conflict").hidden = true; }

  function revert() {
    for (const b of blocks) if (b.cell) b.ta.value = b.cell.value;
    refreshDirty();
    scheduleState();
    status("Changes reverted.");
  }

  /** Saves pending edits; resolves false if they could not be saved. */
  async function flush() {
    if (!isDirty()) return true;
    try { return await saveAll(); } catch (e) { status(e.message, true); return false; }
  }

  /* ---------- column choice ---------- */

  async function setColumns(cols, sheet, row) {
    if (isDirty() && !(await flush())) return;
    cfg.cols = cols;
    if (sheet !== undefined) cfg.sheet = sheet;
    data = null;
    syncSettingsUI();
    await nav(Math.max(row || cfg.row, minRow()), { force: true, discard: true });
  }

  async function useSelection() {
    const s = await backend.call("getSelection");
    if (s.cols.length < 1 || s.cols.length > 4) {
      status("Select 1 to 4 cells in one row (Ctrl+click or Cmd+click for columns that aren't next to each other). Put the column you'll edit rightmost.", true);
      return;
    }
    await setColumns(s.cols, s.sheet, s.row);
  }

  /* ---------- column browser ---------- */

  async function loadColumn() {
    if (!cfg.cols.length) return;
    $("bcount").textContent = "Loading…";
    browse = await backend.call("getColumn", { sheet: cfg.sheet, col: cfg.cols[cfg.cols.length - 1], header: cfg.header });
    renderList();
  }

  function renderList() {
    const list = $("blist");
    list.replaceChildren();
    if (!browse) return;
    const q = $("filter").value.trim().toLowerCase();
    const frag = document.createDocumentFragment();
    let shown = 0, matches = 0, nonEmpty = 0;
    browse.values.forEach((v, i) => {
      if (!v) return;
      nonEmpty++;
      if (q && !v.toLowerCase().includes(q)) return;
      matches++;
      if (shown >= LIST_LIMIT) return;
      shown++;
      const row = browse.startRow + i;
      const li = document.createElement("li");
      li.dataset.row = row;
      const rn = document.createElement("span");
      rn.className = "rn";
      rn.textContent = row;
      const pv = document.createElement("span");
      pv.className = "pv";
      pv.textContent = v.slice(0, 200).replace(/\s+/g, " ");
      li.append(rn, pv);
      if (data && row === data.row) li.classList.add("current");
      if (row === previewRow) li.classList.add("picked");
      frag.appendChild(li);
    });
    list.appendChild(frag);
    $("bcount").textContent =
      `${matches.toLocaleString()} of ${nonEmpty.toLocaleString()} filled cells` +
      (matches > shown ? ` (showing the first ${shown}; type in the filter to narrow the list)` : "");
  }

  function markCurrentInList() {
    for (const li of $("blist").querySelectorAll("li.current")) li.classList.remove("current");
    if (!data) return;
    const li = $("blist").querySelector(`li[data-row="${data.row}"]`);
    if (li) li.classList.add("current");
  }

  function updateListValue(col, row, value) {
    if (!browse || browse.col !== col) return;
    const i = row - browse.startRow;
    if (i >= 0 && i < browse.values.length) browse.values[i] = value;
    else if (i >= browse.values.length) { browse.values.length = i; browse.values[i] = value; }
    if ($("browser").open) renderList();
    if (previewRow === row) $("bptext").textContent = value;
  }

  function pick(row) {
    previewRow = row;
    for (const li of $("blist").querySelectorAll("li.picked")) li.classList.remove("picked");
    const li = $("blist").querySelector(`li[data-row="${row}"]`);
    if (li) li.classList.add("picked");
    $("bprow").textContent = `${browse.col}${row}`;
    $("bptext").textContent = browse.values[row - browse.startRow] || "";
    $("bpreview").hidden = false;
  }

  const pickedText = () => (browse && previewRow != null ? browse.values[previewRow - browse.startRow] || "" : "");

  function editTarget(fn, label) {
    const b = target();
    if (!b || !b.cell) return status("Load a row first.", true);
    if (b.cell.isFormula) return status(`${b.col}${data.row} contains a formula and can't be edited here.`, true);
    fn(b.ta, pickedText());
    refreshDirty();
    scheduleState();
    status(`${label} — not saved yet (Ctrl+S to save).`);
  }

  /* ---------- wiring ---------- */

  $("prev").onclick = () => step(-1);
  $("next").onclick = () => step(1);
  $("row").onchange = () => nav($("row").value);
  $("reload").onclick = () => {
    // Re-read the row from Excel; unsaved edits stay in the boxes as drafts.
    const drafts = state().drafts;
    nav(data ? data.row : cfg.row, { force: true, discard: true, drafts: Object.keys(drafts).length ? drafts : null });
    if ($("browser").open) loadColumn().catch((e) => status(e.message, true));
  };
  $("useSel").onclick = () => useSelection().catch((e) => status(e.message, true));
  $("applyCols").onclick = () => {
    try { setColumns(parseCols($("cols").value)); } catch (e) { status(e.message, true); }
  };
  $("cols").onkeydown = (e) => { if (e.key === "Enter") $("applyCols").click(); };
  $("header").onchange = () => {
    cfg.header = $("header").checked;
    browse = null;
    nav(Math.max(cfg.row, minRow()), { force: true }).then(() => $("browser").open && loadColumn());
  };
  for (const k of ["follow", "syncSel", "autosave"]) $(k).onchange = () => { cfg[k] = $(k).checked; persist(); };
  $("smaller").onclick = () => { cfg.font = Math.max(10, cfg.font - 1); applyFont(); persist(); };
  $("bigger").onclick = () => { cfg.font = Math.min(28, cfg.font + 1); applyFont(); persist(); };
  $("save").onclick = () => saveAll().catch((e) => status(e.message, true));
  $("revert").onclick = revert;
  $("overwrite").onclick = () => { forceNext = true; saveAll().catch((e) => status(e.message, true)); };
  $("discard").onclick = () => nav(data.row, { force: true, discard: true });

  $("browser").addEventListener("toggle", () => {
    if ($("browser").open && (!browse || browse.col !== cfg.cols[cfg.cols.length - 1])) {
      loadColumn().catch((e) => status(e.message, true));
    }
  });
  $("brefresh").onclick = () => loadColumn().catch((e) => status(e.message, true));
  let filterTimer;
  $("filter").oninput = () => { clearTimeout(filterTimer); filterTimer = setTimeout(renderList, 150); };
  $("blist").onclick = (e) => {
    const li = e.target.closest("li");
    if (li) pick(Number(li.dataset.row));
  };
  $("blist").ondblclick = (e) => {
    const li = e.target.closest("li");
    if (li) { pick(Number(li.dataset.row)); $("bins").click(); }
  };
  $("bins").onclick = () => editTarget((ta, t) => {
    ta.focus();
    ta.setRangeText(t, ta.selectionStart, ta.selectionEnd, "end");
  }, "Inserted at cursor");
  $("brep").onclick = () => editTarget((ta, t) => { ta.value = t; }, "Replaced text");
  $("bapp").onclick = () => editTarget((ta, t) => {
    ta.value = ta.value && !/\s$/.test(ta.value) ? `${ta.value}\n${t}` : ta.value + t;
  }, "Appended text");
  $("bcopy").onclick = async () => {
    const ok = await copyText(pickedText());
    status(ok ? "Copied to clipboard." : "Copy failed: select the text in the preview and press Ctrl+C.", !ok);
  };
  $("bgo").onclick = () => previewRow != null && nav(previewRow);

  document.addEventListener("keydown", (e) => {
    if (suspended) return;
    if (e.altKey && (e.key === "ArrowUp" || e.key === "ArrowDown")) {
      e.preventDefault();
      step(e.key === "ArrowUp" ? -1 : 1);
    } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
      e.preventDefault();
      saveAll().catch((err) => status(err.message, true));
    }
  });

  if (opts.onPopOut) $("popout").onclick = () => opts.onPopOut();
  if (opts.onDock) {
    $("popout").textContent = "Dock";
    $("popout").title = "Put the viewer back in the task pane";
    $("popout").onclick = () => opts.onDock();
  }

  backend.on((name, sel) => {
    if (suspended || name !== "selection" || !cfg.follow || !cfg.cols.length) return;
    if (Date.now() - lastSelfSelect < 800) return;       // our own selectCell echo
    if (sel.sheet !== cfg.sheet || sel.row < minRow()) return;
    if (data && sel.row === data.row && wantRow == null) return;
    nav(sel.row);
  });

  applyFont();
  syncSettingsUI();
  refreshDirty();

  return {
    start() { if (cfg.cols.length) nav(cfg.row, { force: true }); },
    status,
    state,
    flush,
    suspend() { suspended = true; $("app").hidden = true; $("popped").hidden = false; },
    resume(s) {
      suspended = false;
      $("popped").hidden = true;
      $("app").hidden = false;
      if (s && s.cfg) Object.assign(cfg, s.cfg);
      applyFont();
      layoutKey = "";
      data = null;
      syncSettingsUI();
      if (cfg.cols.length) nav(cfg.row, { force: true, discard: true, drafts: s && s.drafts });
    },
  };
}

/* ---------- start-up: task pane or pop-out window ---------- */

Office.onReady(() => {
  const params = new URLSearchParams(location.search);

  if (params.get("mode") === "dialog") {
    document.body.classList.add("in-dialog");
    const be = makeDialogBackend();
    let cfg = null;
    try { cfg = JSON.parse(params.get("cfg")); } catch { /* use defaults */ }
    const ui = createViewer(be, {
      cfg,
      onState: (s) => be.emit("state", s),
      onDock: async () => {
        if (await ui.flush()) { be.emit("state", ui.state()); be.emit("dock"); }
      },
    });
    // Handshake: keep saying "ready" until the task pane answers "init".
    let started = false;
    const hello = setInterval(() => be.emit("ready"), 500);
    be.emit("ready");
    be.on((name) => {
      if (name !== "init" || started) return;
      started = true;
      clearInterval(hello);
      ui.start();
    });
    return;
  }

  const be = makeDirectBackend();
  let dialog = null;
  let link = null;
  let last = null;

  const ui = createViewer(be, { cfg: loadCfg(), onPopOut: popOut });
  ui.start();
  be.on((name, data) => { if (link && name === "selection") link.emit("selection", data); });

  function docked() {
    if (!dialog) return;
    dialog = null;
    link = null;
    ui.resume(last);
  }

  $("bringBack").onclick = () => {
    if (dialog) { try { dialog.close(); } catch { /* already closed */ } }
    docked();
  };

  async function popOut() {
    if (!Office.context.requirements.isSetSupported("DialogApi", "1.2")) {
      ui.status("This version of Office can't open the viewer in its own window. On Windows, drag the task pane's title bar off the side of Excel to make it float.", true);
      return;
    }
    if (!(await ui.flush())) return;
    last = ui.state();
    const url = new URL("viewer.html", location.href);
    url.search = `mode=dialog&cfg=${encodeURIComponent(JSON.stringify(last.cfg))}`;
    Office.context.ui.displayDialogAsync(url.href, { width: 35, height: 70, displayInIframe: false }, (res) => {
      if (res.status !== Office.AsyncResultStatus.Succeeded) {
        ui.status(`Could not open the window: ${res.error.message}`, true);
        return;
      }
      dialog = res.value;
      ui.suspend();
      link = serveDialog(dialog, {
        onEvent(name, data) {
          if (name === "ready") link.emit("init");
          else if (name === "state") last = data;
          else if (name === "dock") { try { dialog.close(); } catch { /* closed */ } docked(); }
        },
        onClosed: docked,
      });
    });
  }
});
