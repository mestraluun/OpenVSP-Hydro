using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ExcelDna.Integration;
using Excel = Microsoft.Office.Interop.Excel;

namespace CellViewer
{
    internal sealed class CellData
    {
        public string Col;
        public string Value;     // display text, line breaks as \r\n
        public bool IsFormula;
        public string Formula;
        public string Header;
    }

    internal sealed class RowData
    {
        public string Workbook;
        public string Sheet;
        public int Row;
        public int LastRow;
        public CellData[] Cells;
    }

    internal sealed class SelectionInfo
    {
        public string Workbook;
        public string Sheet;
        public int Row;
        public string[] Cols;
    }

    internal sealed class ColumnData
    {
        public string Workbook;
        public string Sheet;
        public string Col;
        public int StartRow;
        public string[] Values;
    }

    internal sealed class WriteResult
    {
        public bool Conflict;
        public string Current;
        public string Value;
    }

    internal sealed class SheetNotFoundException : Exception
    {
        public SheetNotFoundException(string msg) : base(msg) { }
    }

    /// <summary>
    /// All access to the Excel object model. The viewer window runs on its own thread,
    /// so every call is queued to Excel's main thread with QueueAsMacro (which also waits
    /// politely while the user is editing a cell).
    /// </summary>
    internal static class ExcelBridge
    {
        public const int MaxCellChars = 32767;

        static Excel.Application _app;   // kept alive so the COM event sink is not collected

        public static event Action<SelectionInfo> SelectionChanged;

        /// <summary>Call on Excel's main thread (from the ribbon).</summary>
        public static Excel.Application Init()
        {
            if (_app != null) return _app;
            _app = (Excel.Application)ExcelDnaUtil.Application;
            _app.SheetSelectionChange += OnSheetSelectionChange;
            return _app;
        }

        static void OnSheetSelectionChange(object sh, Excel.Range target)
        {
            var handler = SelectionChanged;
            if (handler == null) return;
            try { handler(Describe(_app, target)); } catch { /* never let an event break Excel */ }
        }

        /// <summary>Runs <paramref name="f"/> on Excel's main thread and returns its result.</summary>
        public static Task<T> Run<T>(Func<Excel.Application, T> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            ExcelAsyncUtil.QueueAsMacro(() =>
            {
                try { tcs.SetResult(f(_app ?? (Excel.Application)ExcelDnaUtil.Application)); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        /* ---------- column letters ---------- */

        public static int ColToIndex(string col)
        {
            int n = 0;
            foreach (char ch in col.ToUpperInvariant()) n = n * 26 + (ch - 'A' + 1);
            return n; // 1-based
        }

        public static string IndexToCol(int index) // 1-based
        {
            var s = "";
            for (int i = index; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s;
            return s;
        }

        public static bool IsValidCol(string col) =>
            col.Length >= 1 && col.Length <= 3 && col.All(c => c >= 'A' && c <= 'Z') && ColToIndex(col) <= 16384;

        /* ---------- text conversion ---------- */

        static readonly Dictionary<int, string> Errors = new Dictionary<int, string>
        {
            { -2146826281, "#DIV/0!" }, { -2146826246, "#N/A" }, { -2146826259, "#NAME?" },
            { -2146826288, "#NULL!" }, { -2146826252, "#NUM!" }, { -2146826265, "#REF!" },
            { -2146826273, "#VALUE!" },
        };

        /// <summary>Excel stores line breaks as \n; the TextBox needs \r\n.</summary>
        public static string ToDisplay(string excelText) =>
            excelText.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

        public static string ToExcel(string displayText) => displayText.Replace("\r\n", "\n");

        public static int ExcelLength(string displayText) => ToExcel(displayText).Length;

        static string CellText(Excel.Range cell)
        {
            object v = cell.Value2;
            switch (v)
            {
                case null: return "";
                case string s: return ToDisplay(s);
                case bool b: return b ? "TRUE" : "FALSE";
                case int e: return Errors.TryGetValue(e, out var name) ? name : "#ERROR";
                case double d:
                    // Prefer what Excel shows (dates, currency…) unless the column is too narrow.
                    var shown = Convert.ToString(cell.Text, CultureInfo.CurrentCulture) ?? "";
                    return shown.Length > 0 && shown.Trim('#').Length > 0 ? shown : d.ToString("G15", CultureInfo.CurrentCulture);
                default: return Convert.ToString(v, CultureInfo.CurrentCulture);
            }
        }

        static string ValueText(object v)
        {
            switch (v)
            {
                case null: return "";
                case string s: return ToDisplay(s);
                case bool b: return b ? "TRUE" : "FALSE";
                case int e: return Errors.TryGetValue(e, out var name) ? name : "#ERROR";
                case double d: return d.ToString("G15", CultureInfo.CurrentCulture);
                default: return Convert.ToString(v, CultureInfo.CurrentCulture);
            }
        }

        /* ---------- sheet lookup ---------- */

        static Excel.Worksheet ActiveWorksheet(Excel.Application app)
        {
            if (app.ActiveWorkbook == null) throw new InvalidOperationException("No workbook is open in Excel.");
            return app.ActiveSheet as Excel.Worksheet
                   ?? throw new InvalidOperationException("The active sheet is not a worksheet.");
        }

        static Excel.Worksheet FindSheet(Excel.Application app, string workbook, string sheet, bool fallbackToActive)
        {
            if (string.IsNullOrEmpty(sheet)) return ActiveWorksheet(app);
            Excel.Workbook wb = null;
            if (string.IsNullOrEmpty(workbook)) wb = app.ActiveWorkbook;
            else
                foreach (Excel.Workbook w in app.Workbooks)
                    if (string.Equals(w.Name, workbook, StringComparison.OrdinalIgnoreCase)) { wb = w; break; }
            if (wb != null)
                foreach (object o in wb.Worksheets)
                    if (o is Excel.Worksheet ws && string.Equals(ws.Name, sheet, StringComparison.OrdinalIgnoreCase)) return ws;
            if (fallbackToActive) return ActiveWorksheet(app);
            throw new SheetNotFoundException(wb == null
                ? $"Workbook \"{workbook}\" is not open. Open it, or choose columns again with “Use selection”."
                : $"Sheet \"{sheet}\" was not found in {wb.Name}.");
        }

        static string WorkbookName(Excel.Worksheet ws) => ((Excel.Workbook)ws.Parent).Name;

        static int LastRow(Excel.Worksheet ws)
        {
            var used = ws.UsedRange;
            return used.Row + used.Rows.Count - 1;
        }

        /* ---------- operations ---------- */

        public static Task<RowData> GetRow(string workbook, string sheet, string[] cols, int row, bool header, bool fallbackToActive) =>
            Run(app =>
            {
                var ws = FindSheet(app, workbook, sheet, fallbackToActive);
                var cells = cols.Select(col =>
                {
                    var r = (Excel.Range)ws.Range[col + row];
                    bool hasFormula = r.HasFormula is bool hf && hf;
                    return new CellData
                    {
                        Col = col,
                        Value = CellText(r),
                        IsFormula = hasFormula,
                        Formula = hasFormula ? Convert.ToString(r.Formula, CultureInfo.InvariantCulture) : "",
                        Header = header ? CellText((Excel.Range)ws.Range[col + "1"]).Replace("\r\n", " ") : "",
                    };
                }).ToArray();
                return new RowData { Workbook = WorkbookName(ws), Sheet = ws.Name, Row = row, LastRow = LastRow(ws), Cells = cells };
            });

        /// <summary>Writes text to a cell unless it changed since <paramref name="expected"/> was read (and force is false).</summary>
        public static Task<WriteResult> WriteCell(string workbook, string sheet, string col, int row, string text, string expected, bool force) =>
            Run(app =>
            {
                if (ExcelLength(text) > MaxCellChars)
                    throw new InvalidOperationException($"The text is {ExcelLength(text):N0} characters; an Excel cell holds at most {MaxCellChars:N0}.");
                var ws = FindSheet(app, workbook, sheet, false);
                var r = (Excel.Range)ws.Range[col + row];
                if (r.HasFormula is bool hf && hf) throw new InvalidOperationException($"{col}{row} contains a formula and was not changed.");
                var current = CellText(r);
                if (!force && expected != null && current != expected) return new WriteResult { Conflict = true, Current = current };
                if (text.Length == 0) r.ClearContents();
                else r.Value2 = ToExcel(text);
                return new WriteResult { Value = CellText(r) };
            });

        public static Task<ColumnData> GetColumn(string workbook, string sheet, string col, bool header) =>
            Run(app =>
            {
                var ws = FindSheet(app, workbook, sheet, false);
                int start = header ? 2 : 1;
                int last = LastRow(ws);
                var result = new ColumnData { Workbook = WorkbookName(ws), Sheet = ws.Name, Col = col, StartRow = start, Values = new string[0] };
                if (last < start) return result;
                object v = ((Excel.Range)ws.Range[$"{col}{start}:{col}{last}"]).Value2;
                if (v is object[,] arr)
                {
                    int lo = arr.GetLowerBound(0), n = arr.GetLength(0), c = arr.GetLowerBound(1);
                    var values = new string[n];
                    for (int i = 0; i < n; i++) values[i] = ValueText(arr[lo + i, c]);
                    result.Values = values;
                }
                else result.Values = new[] { ValueText(v) };
                return result;
            });

        public static Task<bool> SelectCell(string workbook, string sheet, string col, int row) =>
            Run(app =>
            {
                var ws = FindSheet(app, workbook, sheet, false);
                var wb = (Excel.Workbook)ws.Parent;
                if (app.ActiveWorkbook == null || app.ActiveWorkbook.Name != wb.Name) wb.Activate();
                if (!(app.ActiveSheet is Excel.Worksheet a) || a.Name != ws.Name) ws.Activate();
                app.Goto(ws.Range[col + row], false);
                return true;
            });

        public static Task<SelectionInfo> GetSelection() =>
            Run(app =>
            {
                if (!(app.Selection is Excel.Range sel))
                    throw new InvalidOperationException("Select cells on a worksheet first.");
                return Describe(app, sel);
            });

        static SelectionInfo Describe(Excel.Application app, Excel.Range sel)
        {
            var ws = sel.Worksheet;
            var cols = new SortedSet<int>();
            foreach (Excel.Range area in sel.Areas)
            {
                int first = area.Column, count = Math.Min(area.Columns.Count, 16);
                for (int c = first; c < first + count && cols.Count <= 16; c++) cols.Add(c);
            }
            int row = sel.Row;
            try { if (app.ActiveCell != null) row = app.ActiveCell.Row; } catch { /* no active cell */ }
            return new SelectionInfo
            {
                Workbook = WorkbookName(ws),
                Sheet = ws.Name,
                Row = row,
                Cols = cols.Select(IndexToCol).ToArray(),
            };
        }
    }
}
