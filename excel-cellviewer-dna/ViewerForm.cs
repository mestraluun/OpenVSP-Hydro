using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CellViewer
{
    /// <summary>
    /// The floating Cell Viewer window: shows 2–4 cells of one row in large text boxes,
    /// steps through rows with the columns fixed, saves edits back to Excel and lets you
    /// browse the edit column to reuse text from other rows.
    /// </summary>
    internal sealed class ViewerForm : Form
    {
        sealed class Block
        {
            public string Col;
            public Label Addr, Hdr, Info;
            public TextBox Box;
            public CellData Cell;
        }

        sealed class NavOptions
        {
            public bool Force, Discard, Fallback, FromExcel;
            public Dictionary<string, string> Drafts;
        }

        static readonly Color DirtyColor = Color.FromArgb(176, 128, 0);
        static readonly Color ErrorColor = Color.Firebrick;
        static readonly Color CurrentRowColor = Color.FromArgb(222, 240, 228);

        readonly IntPtr _excelHwnd;
        readonly Settings _s = Settings.Load();
        readonly ToolTip _tips = new ToolTip { AutoPopDelay = 15000 };
        readonly System.Windows.Forms.Timer _filterTimer = new System.Windows.Forms.Timer { Interval = 200 };

        // toolbar
        Button _prev, _next, _useSel, _reload, _smaller, _bigger, _settingsBtn;
        TextBox _rowBox;
        Label _ofLabel;
        // settings
        FlowLayoutPanel _settingsPanel;
        TextBox _colsBox;
        CheckBox _chkHeader, _chkFollow, _chkSync, _chkAuto, _chkTop;
        Label _hint;
        // cells
        SplitContainer _split;
        TableLayoutPanel _cellsTable;
        Button _save, _revert, _browseBtn;
        Label _dirtyLabel;
        // browser
        TextBox _filter, _preview;
        Label _bTitle, _bCount, _previewLabel;
        ListView _list;
        Button _bIns, _bRep, _bApp, _bCopy, _bGo, _bRefresh;
        // status
        ToolStripStatusLabel _status;

        readonly List<Block> _blocks = new List<Block>();
        string _layoutKey;
        RowData _data;
        Font _textFont;
        TaskScheduler _ui;
        Task _chain = Task.CompletedTask;
        int? _wantRow;
        int _lastSelfSelect = Environment.TickCount - 10000;
        ColumnData _browse;
        List<int> _filtered = new List<int>();
        int? _previewRow;
        bool _loadingText, _closeConfirmed;

        public ViewerForm(IntPtr excelHwnd)
        {
            _excelHwnd = excelHwnd;
            BuildUi();
        }

        int MinRow => _s.Header ? 2 : 1;
        string TargetCol => _s.Cols.Length > 0 ? _s.Cols[_s.Cols.Length - 1] : null;
        Block Target => _blocks.Count > 0 ? _blocks[_blocks.Count - 1] : null;

        /* ================= UI construction ================= */

        void BuildUi()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "Cell Viewer";
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(540, 780);
            MinimumSize = new Size(380, 460);
            KeyPreview = true;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(6, 6, 6, 0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // --- toolbar
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0) };
            _prev = Btn("▲", "Previous row (Alt+↑)", (s, e) => Step(-1));
            _rowBox = new TextBox { Width = 64, Margin = new Padding(2, 4, 2, 2), TextAlign = HorizontalAlignment.Right };
            _rowBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                if (int.TryParse(_rowBox.Text, out var r)) Nav(r);
            };
            _tips.SetToolTip(_rowBox, "Type a row number and press Enter");
            _ofLabel = new Label { AutoSize = true, Margin = new Padding(2, 7, 6, 2), ForeColor = SystemColors.GrayText };
            _next = Btn("▼", "Next row (Alt+↓)", (s, e) => Step(1));
            _useSel = Btn("Use selection", "Show the columns of the cells selected in Excel (Ctrl+click for columns that aren't next to each other)", async (s, e) => await Safe(UseSelectionAsync));
            _reload = Btn("⟳", "Reload from Excel (F5). Unsaved edits are kept.", (s, e) => Reload());
            _smaller = Btn("A−", "Smaller text", (s, e) => ChangeFont(-1));
            _bigger = Btn("A+", "Larger text", (s, e) => ChangeFont(+1));
            _settingsBtn = Btn("⚙", "Settings", (s, e) => _settingsPanel.Visible = !_settingsPanel.Visible);
            bar.Controls.AddRange(new Control[] { _prev, new Label { Text = "Row", AutoSize = true, Margin = new Padding(4, 7, 0, 2) }, _rowBox, _ofLabel, _next, _useSel, _reload, _smaller, _bigger, _settingsBtn });
            root.Controls.Add(bar, 0, 0);

            // --- settings
            _settingsPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Visible = false, Padding = new Padding(0, 2, 0, 4) };
            _colsBox = new TextBox { Width = 110, Margin = new Padding(2, 4, 2, 2) };
            _colsBox.HandleCreated += (s, e) => SetCue(_colsBox, "e.g. A, C, F");
            _colsBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyColsText(); } };
            var apply = Btn("Apply", "Use these columns (the last one is the edit column)", (s, e) => ApplyColsText());
            _chkHeader = Chk("Row 1 is a header", v => { _s.Header = v; _browse = null; Nav(Math.Max(_s.Row, MinRow), new NavOptions { Force = true }); if (BrowserOpen) _ = Safe(LoadColumnAsync); });
            _chkFollow = Chk("Follow the Excel selection", v => _s.Follow = v);
            _chkSync = Chk("Move the Excel selection with the viewer", v => _s.SyncSelection = v);
            _chkAuto = Chk("Save automatically when changing row", v => _s.AutoSave = v);
            _chkTop = Chk("Stay on top of all windows", v => { _s.TopMost = v; TopMost = v; });
            _settingsPanel.Controls.AddRange(new Control[] { new Label { Text = "Columns", AutoSize = true, Margin = new Padding(2, 7, 0, 2) }, _colsBox, apply, _chkHeader, _chkFollow, _chkSync, _chkAuto, _chkTop });
            root.Controls.Add(_settingsPanel, 0, 1);

            // --- hint
            _hint = new Label
            {
                Dock = DockStyle.Fill, AutoSize = false, Height = 78, Padding = new Padding(8), Margin = new Padding(0, 2, 0, 4),
                BackColor = Color.FromArgb(243, 246, 250),
                Text = "In Excel, select 2 to 4 cells in one row. Hold Ctrl and click to pick columns that aren't next to each other. " +
                       "Put the column you want to edit rightmost, then click “Use selection”. You can also type column letters under ⚙ Settings.",
            };
            root.Controls.Add(_hint, 0, 2);

            // --- split: cells (top) / column browser (bottom)
            // Size first: setting the min sizes while the control is at its 150×100 default throws.
            _split = new SplitContainer { Size = new Size(500, 700), Orientation = Orientation.Horizontal, SplitterWidth = 6 };
            _split.Panel1MinSize = 260;
            _split.Panel2MinSize = 120;
            _split.FixedPanel = FixedPanel.Panel2;
            _split.Dock = DockStyle.Fill;
            root.Controls.Add(_split, 0, 3);

            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _cellsTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0) };
            _cellsTable.SizeChanged += (s, e) => LayoutBlockHeights();
            top.Controls.Add(_cellsTable, 0, 0);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 2, 0, 2) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _save = Btn("Save", "Write your changes to Excel (Ctrl+S)", async (s, e) => await Safe(() => SaveAllAsync()));
            _save.Font = new Font(Font, FontStyle.Bold);
            _revert = Btn("Revert", "Undo your unsaved changes", (s, e) => Revert());
            _dirtyLabel = new Label { AutoSize = true, ForeColor = DirtyColor, Margin = new Padding(6, 7, 2, 2), Font = new Font(Font, FontStyle.Bold) };
            _browseBtn = Btn("Browse column ▾", "List the other cells in the edit column, to find text to reuse (Ctrl+F)", (s, e) => ToggleBrowser());
            actions.Controls.Add(_save, 0, 0);
            actions.Controls.Add(_revert, 1, 0);
            actions.Controls.Add(_dirtyLabel, 2, 0);
            actions.Controls.Add(_browseBtn, 3, 0);
            top.Controls.Add(actions, 0, 1);
            _split.Panel1.Controls.Add(top);

            _split.Panel2.Controls.Add(BuildBrowser());
            _split.Panel2Collapsed = true;

            var statusStrip = new StatusStrip { SizingGrip = true };
            _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(_status);

            Controls.Add(root);
            Controls.Add(statusStrip);
            root.BringToFront();

            _filterTimer.Tick += (s, e) => { _filterTimer.Stop(); ApplyFilter(); };
            ResumeLayout(true);
        }

        Control BuildBrowser()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new Padding(0), Padding = new Padding(0, 4, 0, 0) };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

            var head = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _bTitle = new Label { AutoSize = true, Margin = new Padding(2, 7, 6, 2), Font = new Font(Font, FontStyle.Bold) };
            _filter = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(2, 4, 2, 2) };
            _filter.HandleCreated += (s, e) => SetCue(_filter, "Filter…");
            _filter.TextChanged += (s, e) => { _filterTimer.Stop(); _filterTimer.Start(); };
            _filter.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down && _list.VirtualListSize > 0)
                {
                    e.SuppressKeyPress = true;
                    _list.Focus();
                    _list.SelectedIndices.Clear();
                    _list.SelectedIndices.Add(0);
                }
            };
            _bRefresh = Btn("⟳", "Reload the column from Excel", async (s, e) => await Safe(LoadColumnAsync));
            head.Controls.Add(_bTitle, 0, 0);
            head.Controls.Add(_filter, 1, 0);
            head.Controls.Add(_bRefresh, 2, 0);
            t.Controls.Add(head, 0, 0);

            _bCount = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(2, 0, 2, 2) };
            t.Controls.Add(_bCount, 0, 1);

            _list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                VirtualMode = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Margin = new Padding(2),
            };
            _list.Columns.Add("Row", 60, HorizontalAlignment.Right);
            _list.Columns.Add("Text", 400);
            _list.RetrieveVirtualItem += OnRetrieveItem;
            _list.SelectedIndexChanged += (s, e) =>
            {
                if (_list.SelectedIndices.Count == 1) Pick(_filtered[_list.SelectedIndices[0]]);
            };
            _list.DoubleClick += (s, e) => InsertPicked();
            _list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; InsertPicked(); } };
            _list.Resize += (s, e) => _list.Columns[1].Width = Math.Max(100, _list.ClientSize.Width - _list.Columns[0].Width - 4);
            t.Controls.Add(_list, 0, 2);

            var pbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0) };
            _previewLabel = new Label { AutoSize = true, Margin = new Padding(2, 7, 6, 2), Font = new Font(Font, FontStyle.Bold) };
            _bIns = Btn("Insert at cursor", "Insert into the edit box at the cursor (or double-click / Enter in the list)", (s, e) => InsertPicked());
            _bRep = Btn("Replace", "Replace the whole edit box with this text", (s, e) => EditTarget((box, text) => box.Text = text, "Replaced text"));
            _bApp = Btn("Append", "Add this text to the end of the edit box", (s, e) => EditTarget((box, text) =>
            {
                var cur = box.Text;
                box.Text = cur.Length > 0 && !char.IsWhiteSpace(cur[cur.Length - 1]) ? cur + "\r\n" + text : cur + text;
            }, "Appended text"));
            _bCopy = Btn("Copy", "Copy this text to the clipboard", (s, e) => CopyPicked());
            _bGo = Btn("Go to row", "Show this row in the viewer", (s, e) => { if (_previewRow.HasValue) Nav(_previewRow.Value); });
            pbar.Controls.AddRange(new Control[] { _previewLabel, _bIns, _bRep, _bApp, _bCopy, _bGo });
            t.Controls.Add(pbar, 0, 3);

            _preview = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
                BackColor = Color.FromArgb(248, 248, 248), Margin = new Padding(2),
            };
            t.Controls.Add(_preview, 0, 4);
            SetPreviewEnabled(false);
            return t;
        }

        Button Btn(string text, string tip, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(2), Padding = new Padding(4, 1, 4, 1), UseVisualStyleBackColor = true };
            b.Click += click;
            _tips.SetToolTip(b, tip);
            return b;
        }

        CheckBox Chk(string text, Action<bool> changed)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(6, 5, 6, 2) };
            c.CheckedChanged += (s, e) => { if (c.Tag == null) changed(c.Checked); };
            return c;
        }

        static void SetChecked(CheckBox c, bool v)
        {
            c.Tag = "setting";
            c.Checked = v;
            c.Tag = null;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        static void SetCue(TextBox box, string cue)
        {
            try { SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, IntPtr.Zero, cue); }
            catch { /* placeholder text is cosmetic */ }
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

        /// <summary>Owned by Excel's window: stays above Excel and hides when Excel is minimized, like the Watch Window.</summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            const int GWLP_HWNDPARENT = -8;
            if (_excelHwnd == IntPtr.Zero) return;
            if (IntPtr.Size == 8) SetWindowLongPtr64(Handle, GWLP_HWNDPARENT, _excelHwnd);
            else SetWindowLong32(Handle, GWLP_HWNDPARENT, _excelHwnd.ToInt32());
        }

        /* ================= lifecycle ================= */

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _ui = TaskScheduler.FromCurrentSynchronizationContext();

            var b = _s.Bounds;
            if (b.Width >= MinimumSize.Width && b.Height >= MinimumSize.Height &&
                Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new Rectangle(b.X, b.Y, b.Width, 40))))
                Bounds = b;
            else
            {
                var wa = Screen.FromHandle(_excelHwnd != IntPtr.Zero ? _excelHwnd : Handle).WorkingArea;
                Location = new Point(Math.Max(wa.Left, wa.Right - Width - 24), wa.Top + 90);
                Height = Math.Min(Height, wa.Height - 120);
            }
            TopMost = _s.TopMost;

            SetChecked(_chkHeader, _s.Header);
            SetChecked(_chkFollow, _s.Follow);
            SetChecked(_chkSync, _s.SyncSelection);
            SetChecked(_chkAuto, _s.AutoSave);
            SetChecked(_chkTop, _s.TopMost);
            ApplyFont();
            SyncSettingsUi();
            RefreshDirty();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_s.BrowserOpen) ToggleBrowser();
            if (_s.Cols.Length > 0) Nav(_s.Row, new NavOptions { Force = true, Fallback = true });
            else Status("Select cells in Excel, then click “Use selection”.");
        }

        public void BringToFrontAndFocus()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        public void CloseWithoutPrompt()
        {
            _closeConfirmed = true;
            Close();
        }

        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closeConfirmed && IsDirty && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                var ans = MessageBox.Show(this, $"Save your changes to {DirtyNames()} before closing?", "Cell Viewer",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (ans == DialogResult.Cancel) return;
                if (ans == DialogResult.Yes && !await Safe(() => SaveAllAsync())) return;
                _closeConfirmed = true;
                Close();
                return;
            }
            _s.Bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (!_split.Panel2Collapsed) _s.BrowserHeight = _split.Panel2.Height;
            _s.Save();
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Alt && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
            {
                Step(e.KeyCode == Keys.Up ? -1 : 1);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.S)
            {
                _ = Safe(() => SaveAllAsync());
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.A && ActiveControl is TextBox tb)
            {
                tb.SelectAll();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.F)
            {
                if (!BrowserOpen) ToggleBrowser();
                _filter.Focus();
                _filter.SelectAll();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.F5)
            {
                Reload();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        /* ================= helpers ================= */

        void Status(string msg, bool error = false)
        {
            _status.Text = msg;
            _status.ForeColor = error ? ErrorColor : SystemColors.ControlText;
        }

        /// <summary>Runs an action and reports any error in the status bar. Returns the action's result (false on error).</summary>
        async Task<bool> Safe(Func<Task<bool>> f)
        {
            try { return await f(); }
            catch (Exception ex) { Status(Clean(ex), true); return false; }
        }

        async Task<bool> Safe(Func<Task> f)
        {
            try { await f(); return true; }
            catch (Exception ex) { Status(Clean(ex), true); return false; }
        }

        static string Clean(Exception ex)
        {
            while (ex is AggregateException ae && ae.InnerException != null) ex = ae.InnerException;
            return ex is COMException ? "Excel refused the change (is the sheet protected or a cell merged?): " + ex.Message : ex.Message;
        }

        /// <summary>Awaits an Excel call, telling the user if Excel is busy (e.g. editing a cell).</summary>
        async Task<T> Call<T>(Task<T> task)
        {
            if (await Task.WhenAny(task, Task.Delay(1500)) != task)
                Status("Waiting for Excel… If you are editing a cell in Excel, press Enter or Esc there.");
            return await task;
        }

        static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

        bool BrowserOpen => !_split.Panel2Collapsed;

        void SyncSettingsUi()
        {
            _colsBox.Text = string.Join(", ", _s.Cols);
            _hint.Visible = _s.Cols.Length == 0;
            var where = _s.Cols.Length == 0 ? "" :
                $" — {(_s.Workbook.Length > 0 ? _s.Workbook + " › " : "")}{(_s.Sheet.Length > 0 ? _s.Sheet + " › " : "")}{string.Join(", ", _s.Cols)}";
            Text = "Cell Viewer" + where;
            _bTitle.Text = TargetCol != null ? $"Column {TargetCol}" : "Column";
            _browseBtn.Text = (BrowserOpen ? "Hide column list ▴" : $"Browse column {TargetCol} ▾").Replace("  ", " ");
            _browseBtn.Enabled = _s.Cols.Length > 0;
            foreach (var c in new Control[] { _prev, _next, _rowBox, _reload }) c.Enabled = _s.Cols.Length > 0;
        }

        void UpdateRowBox()
        {
            if (_data == null) return;
            _rowBox.Text = _data.Row.ToString();
            _ofLabel.Text = $"of {_data.LastRow}";
            _prev.Enabled = _data.Row > MinRow;
        }

        void ApplyFont()
        {
            var old = _textFont;
            _textFont = new Font("Segoe UI", _s.FontSize);
            foreach (var b in _blocks) b.Box.Font = _textFont;
            _preview.Font = _textFont;
            LayoutBlockHeights();
            old?.Dispose();
        }

        void ChangeFont(int delta)
        {
            _s.FontSize = Math.Max(7, Math.Min(28, _s.FontSize + delta));
            ApplyFont();
        }

        /* ================= cell blocks ================= */

        void BuildBlocks()
        {
            var key = string.Join(",", _s.Cols);
            if (key == _layoutKey) return;
            _layoutKey = key;

            _cellsTable.SuspendLayout();
            foreach (Control c in _cellsTable.Controls.Cast<Control>().ToList()) c.Dispose();
            _cellsTable.Controls.Clear();
            _cellsTable.RowStyles.Clear();
            _blocks.Clear();
            _cellsTable.RowCount = _s.Cols.Length * 2;

            for (int i = 0; i < _s.Cols.Length; i++)
            {
                var head = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, i == 0 ? 0 : 6, 0, 0) };
                head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var b = new Block
                {
                    Col = _s.Cols[i],
                    Addr = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 2, 6, 2) },
                    Hdr = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 6, 2), TextAlign = ContentAlignment.MiddleLeft },
                    Info = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 2) },
                    Box = new TextBox
                    {
                        Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
                        HideSelection = false, MaxLength = 0, Font = _textFont, Margin = new Padding(0, 0, 0, 0),
                    },
                };
                b.Box.TextChanged += (s, e) => { if (!_loadingText) RefreshDirty(); };
                head.Controls.Add(b.Addr, 0, 0);
                head.Controls.Add(b.Hdr, 1, 0);
                head.Controls.Add(b.Info, 2, 0);
                _cellsTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                _cellsTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
                _cellsTable.Controls.Add(head, 0, i * 2);
                _cellsTable.Controls.Add(b.Box, 0, i * 2 + 1);
                _blocks.Add(b);
            }
            LayoutBlockHeights();
            _cellsTable.ResumeLayout(true);

            _data = null;
            _browse = null;
            ClearPreview();
            SyncSettingsUi();
            if (BrowserOpen) _ = Safe(LoadColumnAsync);
        }

        /// <summary>
        /// Context columns get 2 to 4 lines depending on the space available; the edit
        /// column (last) always takes the rest, so it never scrolls out of view.
        /// </summary>
        void LayoutBlockHeights()
        {
            if (_textFont == null || _blocks.Count == 0) return;
            int line = _textFont.Height;
            int headH = _blocks[0].Addr.PreferredHeight + 10;
            int avail = _cellsTable.ClientSize.Height - _blocks.Count * headH;
            int contexts = _blocks.Count - 1;
            int ctx = contexts == 0 ? 0 : (int)(avail * 0.6 / (contexts + 1.5));   // edit box ≈ 1.5 context shares or more
            ctx = Math.Max(line * 2 + 10, Math.Min(line * 4 + 10, ctx));
            _cellsTable.SuspendLayout();
            for (int i = 0; i < _blocks.Count; i++)
            {
                var style = _cellsTable.RowStyles[i * 2 + 1];
                if (i == _blocks.Count - 1) { style.SizeType = SizeType.Percent; style.Height = 100; }
                else { style.SizeType = SizeType.Absolute; style.Height = ctx; }
            }
            _cellsTable.ResumeLayout(true);
        }

        void SetBoxText(Block b, string text)
        {
            _loadingText = true;
            b.Box.Text = text;
            b.Box.SelectionStart = 0;
            b.Box.SelectionLength = 0;
            b.Box.ScrollToCaret();
            _loadingText = false;
        }

        void FillBlocks(Dictionary<string, string> drafts)
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                var b = _blocks[i];
                var c = _data.Cells[i];
                b.Cell = c;
                b.Hdr.Text = c.Header;
                _tips.SetToolTip(b.Hdr, c.Header);
                b.Box.ReadOnly = c.IsFormula;
                b.Box.BackColor = c.IsFormula ? SystemColors.Control : SystemColors.Window;
                _tips.SetToolTip(b.Box, c.IsFormula ? "Formula: " + c.Formula : null);
                SetBoxText(b, drafts != null && !c.IsFormula && drafts.TryGetValue(b.Col, out var d) ? d : c.Value);
            }
            RefreshDirty();
        }

        IEnumerable<Block> DirtyBlocks() => _blocks.Where(b => b.Cell != null && !b.Cell.IsFormula && b.Box.Text != b.Cell.Value);

        bool IsDirty => DirtyBlocks().Any();

        string DirtyNames() => string.Join(", ", DirtyBlocks().Select(b => b.Col + _data.Row));

        Dictionary<string, string> Drafts() => DirtyBlocks().ToDictionary(b => b.Col, b => b.Box.Text);

        void RefreshDirty()
        {
            int n = 0;
            foreach (var b in _blocks)
            {
                bool dirty = b.Cell != null && !b.Cell.IsFormula && b.Box.Text != b.Cell.Value;
                if (dirty) n++;
                b.Addr.Text = (_data != null ? b.Col + _data.Row : b.Col) + (dirty ? " ●" : "");
                b.Addr.ForeColor = dirty ? DirtyColor : SystemColors.ControlText;
                int len = ExcelBridge.ExcelLength(b.Box.Text);
                bool over = len > ExcelBridge.MaxCellChars;
                b.Info.Text = (b.Cell != null && b.Cell.IsFormula ? "formula · read-only · " : "") + $"{len:N0} chars" + (over ? " — too long for Excel!" : "");
                b.Info.ForeColor = over ? ErrorColor : SystemColors.GrayText;
            }
            _dirtyLabel.Text = n > 0 ? $"{n} unsaved" : "";
            _save.Enabled = _revert.Enabled = n > 0;
        }

        void Revert()
        {
            foreach (var b in _blocks) if (b.Cell != null) SetBoxText(b, b.Cell.Value);
            RefreshDirty();
            Status("Changes reverted.");
        }

        /* ================= navigation ================= */

        void Nav(int row, NavOptions o = null)
        {
            if (_ui == null) return;
            _wantRow = row;
            o = o ?? new NavOptions();
            _chain = _chain.ContinueWith(_ => GoAsync(row, o), CancellationToken.None, TaskContinuationOptions.None, _ui).Unwrap();
        }

        void Step(int delta)
        {
            if (_s.Cols.Length == 0) return;
            Nav((_wantRow ?? _data?.Row ?? _s.Row) + delta);
        }

        void Reload()
        {
            if (_s.Cols.Length == 0) return;
            var drafts = Drafts();
            Nav(_data?.Row ?? _s.Row, new NavOptions { Force = true, Discard = true, Drafts = drafts.Count > 0 ? drafts : null });
            if (BrowserOpen) _ = Safe(LoadColumnAsync);
        }

        async Task GoAsync(int row, NavOptions o)
        {
            try
            {
                if (_s.Cols.Length == 0) return;
                row = Math.Max(MinRow, Math.Min(row, 1048576));
                if (!o.Force && _data != null && row == _data.Row) { _wantRow = null; return; }
                if (!o.Discard && IsDirty && !await ResolveDirtyAsync())
                {
                    _wantRow = null;
                    UpdateRowBox();
                    return;
                }
                BuildBlocks();
                var d = await Call(ExcelBridge.GetRow(_s.Workbook, _s.Sheet, _s.Cols, row, _s.Header, o.Fallback));
                bool sheetChanged = _browse != null && (_browse.Workbook != d.Workbook || _browse.Sheet != d.Sheet);
                _data = d;
                _s.Workbook = d.Workbook;
                _s.Sheet = d.Sheet;
                _s.Row = row;
                _wantRow = null;
                FillBlocks(o.Drafts);
                UpdateRowBox();
                SyncSettingsUi();
                if (sheetChanged) { _browse = null; if (BrowserOpen) _ = Safe(LoadColumnAsync); }
                _list.Invalidate();
                Status(o.Drafts != null ? $"Row {row} reloaded; your unsaved edits are kept." : $"Row {row}");
                if (_s.SyncSelection && !o.FromExcel)
                {
                    _lastSelfSelect = Environment.TickCount;
                    _ = ExcelBridge.SelectCell(d.Workbook, d.Sheet, TargetCol, row).ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                }
            }
            catch (Exception ex)
            {
                _wantRow = null;
                UpdateRowBox();
                Status(Clean(ex), true);
            }
        }

        /// <summary>Called before leaving a row with unsaved edits. Returns false to stay.</summary>
        async Task<bool> ResolveDirtyAsync()
        {
            if (_s.AutoSave) return await SaveAllAsync();
            var ans = MessageBox.Show(this, $"Save your changes to {DirtyNames()} before moving?", "Cell Viewer",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (ans == DialogResult.Yes) return await SaveAllAsync();
            return ans == DialogResult.No;
        }

        /// <summary>Called from Excel's thread when the selection changes.</summary>
        public void OnExcelSelection(SelectionInfo info)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => HandleExcelSelection(info))); } catch { /* closing */ }
        }

        void HandleExcelSelection(SelectionInfo info)
        {
            if (!_s.Follow || _s.Cols.Length == 0) return;
            if (unchecked(Environment.TickCount - _lastSelfSelect) < 800) return;   // echo of our own selectCell
            if (!string.Equals(info.Workbook, _s.Workbook, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(info.Sheet, _s.Sheet, StringComparison.OrdinalIgnoreCase)) return;
            if (info.Row < MinRow) return;
            if (_data != null && info.Row == _data.Row && _wantRow == null) return;
            Nav(info.Row, new NavOptions { FromExcel = true });
        }

        /* ================= saving ================= */

        async Task<bool> SaveAllAsync()
        {
            if (_data == null) return true;
            var saved = new List<string>();
            foreach (var b in DirtyBlocks().ToList())
            {
                var res = await Call(ExcelBridge.WriteCell(_data.Workbook, _data.Sheet, b.Col, _data.Row, b.Box.Text, b.Cell.Value, false));
                if (res.Conflict)
                {
                    var ans = MessageBox.Show(this,
                        $"{b.Col}{_data.Row} was changed in Excel after the viewer loaded it.\n\nIt now begins:\n“{Trunc(res.Current, 160)}”\n\n" +
                        "Yes — overwrite it with your text\nNo — discard your text and show Excel's version\nCancel — keep editing",
                        "Cell changed in Excel", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (ans == DialogResult.Cancel) { Status("Not saved.", true); return false; }
                    if (ans == DialogResult.No)
                    {
                        b.Cell.Value = res.Current;
                        SetBoxText(b, res.Current);
                        UpdateListValue(b.Col, _data.Row, res.Current);
                        continue;
                    }
                    res = await Call(ExcelBridge.WriteCell(_data.Workbook, _data.Sheet, b.Col, _data.Row, b.Box.Text, null, true));
                }
                b.Cell.Value = res.Value;
                if (b.Box.Text != res.Value)
                {
                    int caret = b.Box.SelectionStart;
                    SetBoxText(b, res.Value);
                    b.Box.SelectionStart = Math.Min(caret, res.Value.Length);
                }
                UpdateListValue(b.Col, _data.Row, res.Value);
                saved.Add(b.Col + _data.Row);
            }
            RefreshDirty();
            if (saved.Count > 0) Status("Saved " + string.Join(", ", saved) + ".");
            return true;
        }

        /* ================= choosing columns ================= */

        async Task UseSelectionAsync()
        {
            var sel = await Call(ExcelBridge.GetSelection());
            if (sel.Cols.Length < 1 || sel.Cols.Length > 4)
            {
                Status("Select 1 to 4 cells in one row (Ctrl+click for columns that aren't next to each other), with the edit column rightmost.", true);
                return;
            }
            await SetColumnsAsync(sel.Cols, sel.Workbook, sel.Sheet, sel.Row);
        }

        void ApplyColsText()
        {
            var cols = _colsBox.Text.ToUpperInvariant()
                .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Distinct().ToArray();
            if (cols.Length < 1 || cols.Length > 4 || !cols.All(ExcelBridge.IsValidCol))
            {
                Status("Enter 1 to 4 column letters, for example: A, C, F (the last one is the edit column).", true);
                return;
            }
            _ = Safe(() => SetColumnsAsync(cols, null, null, _data?.Row ?? _s.Row));
        }

        async Task SetColumnsAsync(string[] cols, string workbook, string sheet, int row)
        {
            if (IsDirty && !await ResolveDirtyAsync()) return;
            _s.Cols = cols;
            if (workbook != null) { _s.Workbook = workbook; _s.Sheet = sheet; }
            BuildBlocks();
            Nav(Math.Max(row, MinRow), new NavOptions { Force = true, Discard = true });
        }

        /* ================= column browser ================= */

        void ToggleBrowser()
        {
            if (_s.Cols.Length == 0 && _split.Panel2Collapsed) return;
            if (_split.Panel2Collapsed)
            {
                _split.Panel2Collapsed = false;
                int want = Math.Max(_split.Panel2MinSize, Math.Min(_s.BrowserHeight, (int)(_split.Height * 0.45)));
                int max = _split.Height - _split.Panel2MinSize - _split.SplitterWidth;
                int dist = Math.Min(max, Math.Max(_split.Panel1MinSize, _split.Height - want - _split.SplitterWidth));
                try { if (dist >= _split.Panel1MinSize) _split.SplitterDistance = dist; } catch (InvalidOperationException) { /* window too small */ }
                if (_browse == null || _browse.Col != TargetCol) _ = Safe(LoadColumnAsync);
            }
            else
            {
                _s.BrowserHeight = _split.Panel2.Height;
                _split.Panel2Collapsed = true;
            }
            _s.BrowserOpen = BrowserOpen;
            SyncSettingsUi();
        }

        async Task LoadColumnAsync()
        {
            if (TargetCol == null) return;
            _bCount.Text = "Loading…";
            _browse = await Call(ExcelBridge.GetColumn(_s.Workbook, _s.Sheet, TargetCol, _s.Header));
            ApplyFilter();
        }

        void ApplyFilter()
        {
            if (_browse == null) { _filtered = new List<int>(); _list.VirtualListSize = 0; return; }
            var q = _filter.Text.Trim();
            int filled = 0;
            var list = new List<int>();
            for (int i = 0; i < _browse.Values.Length; i++)
            {
                var v = _browse.Values[i];
                if (string.IsNullOrEmpty(v)) continue;
                filled++;
                if (q.Length == 0 || v.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) list.Add(i);
            }
            _filtered = list;
            _list.SelectedIndices.Clear();
            _list.VirtualListSize = list.Count;
            _list.Invalidate();
            _bCount.Text = q.Length == 0
                ? $"{filled:N0} filled cells. Click one to read it; double-click to insert it at the cursor."
                : $"{list.Count:N0} of {filled:N0} filled cells match.";
            if (_previewRow.HasValue)
            {
                int idx = list.IndexOf(_previewRow.Value - _browse.StartRow);
                if (idx >= 0) { _list.SelectedIndices.Add(idx); _list.EnsureVisible(idx); }
            }
        }

        void OnRetrieveItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (_browse == null || e.ItemIndex >= _filtered.Count) { e.Item = new ListViewItem(new[] { "", "" }); return; }
            int i = _filtered[e.ItemIndex];
            int row = _browse.StartRow + i;
            var v = _browse.Values[i];
            var preview = Trunc(v.Replace("\r\n", " ⏎ ").Replace('\t', ' '), 300);
            var item = new ListViewItem(new[] { row.ToString(), preview });
            if (_data != null && row == _data.Row) item.BackColor = CurrentRowColor;
            e.Item = item;
        }

        void UpdateListValue(string col, int row, string value)
        {
            if (_browse == null || _browse.Col != col || _data == null || _browse.Sheet != _data.Sheet || _browse.Workbook != _data.Workbook) return;
            int i = row - _browse.StartRow;
            if (i < 0) return;
            if (i >= _browse.Values.Length)
            {
                var vals = _browse.Values;
                int old = vals.Length;
                Array.Resize(ref vals, i + 1);
                for (int k = old; k < vals.Length; k++) vals[k] = vals[k] ?? "";
                _browse.Values = vals;
            }
            _browse.Values[i] = value;
            ApplyFilter();
            if (_previewRow == row) _preview.Text = value;
        }

        void Pick(int valueIndex)
        {
            if (_browse == null) return;
            _previewRow = _browse.StartRow + valueIndex;
            _previewLabel.Text = $"{_browse.Col}{_previewRow}";
            _preview.Text = _browse.Values[valueIndex];
            SetPreviewEnabled(true);
        }

        void ClearPreview()
        {
            _previewRow = null;
            _previewLabel.Text = "";
            _preview.Text = "";
            SetPreviewEnabled(false);
        }

        void SetPreviewEnabled(bool on)
        {
            foreach (var b in new[] { _bIns, _bRep, _bApp, _bCopy, _bGo }) b.Enabled = on;
        }

        string PickedText => _previewRow.HasValue ? _preview.Text : "";

        void EditTarget(Action<TextBox, string> edit, string what)
        {
            var t = Target;
            if (!_previewRow.HasValue) { Status("Pick a cell in the list first.", true); return; }
            if (t?.Cell == null) { Status("Load a row first.", true); return; }
            if (t.Cell.IsFormula) { Status($"{t.Col}{_data.Row} contains a formula and can't be edited here.", true); return; }
            edit(t.Box, PickedText);
            RefreshDirty();
            t.Box.Focus();
            Status($"{what} in {t.Col}{_data.Row}. Not saved yet: press Ctrl+S to save.");
        }

        void InsertPicked() => EditTarget((box, text) => box.SelectedText = text, "Inserted text at the cursor");

        void CopyPicked()
        {
            var text = PickedText;
            if (text.Length == 0) return;
            try
            {
                Clipboard.SetText(text);
                Status("Copied to the clipboard.");
            }
            catch (Exception ex)
            {
                Status("Copy failed: " + ex.Message, true);
            }
        }
    }
}
