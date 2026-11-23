using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CellViewer
{
    /// <summary>User preferences, stored as key=value lines in %APPDATA%\CellViewer\settings.txt.</summary>
    internal sealed class Settings
    {
        public string Workbook = "";
        public string Sheet = "";
        public string[] Cols = new string[0];
        public int Row = 2;
        public bool Header = true;
        public bool Follow = true;
        public bool SyncSelection = true;
        public bool AutoSave = true;
        public bool TopMost;
        public float FontSize = 11f;
        public Rectangle Bounds = Rectangle.Empty;
        public bool BrowserOpen;
        public int BrowserHeight = 260;
        public int[] BlockHeights = new int[0];   // dragged heights of the context boxes (0 = automatic)

        static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CellViewer", "settings.txt");

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var d = File.ReadAllLines(FilePath)
                    .Select(l => l.Split(new[] { '=' }, 2))
                    .Where(p => p.Length == 2)
                    .GroupBy(p => p[0].Trim())
                    .ToDictionary(g => g.Key, g => g.Last()[1].Trim());
                string Get(string k) => d.TryGetValue(k, out var v) ? v : null;
                bool Bool(string k, bool def) => bool.TryParse(Get(k), out var b) ? b : def;
                int Int(string k, int def) => int.TryParse(Get(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : def;

                s.Workbook = Get("Workbook") ?? "";
                s.Sheet = Get("Sheet") ?? "";
                s.Cols = (Get("Cols") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).ToArray();
                s.Row = Math.Max(1, Int("Row", 2));
                s.Header = Bool("Header", true);
                s.Follow = Bool("Follow", true);
                s.SyncSelection = Bool("SyncSelection", true);
                s.AutoSave = Bool("AutoSave", true);
                s.TopMost = Bool("TopMost", false);
                if (float.TryParse(Get("FontSize"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) s.FontSize = Math.Max(7, Math.Min(28, f));
                var b = (Get("Bounds") ?? "").Split(',').Select(x => int.TryParse(x, out var n) ? n : int.MinValue).ToArray();
                if (b.Length == 4 && b.All(n => n != int.MinValue)) s.Bounds = new Rectangle(b[0], b[1], b[2], b[3]);
                s.BrowserOpen = Bool("BrowserOpen", false);
                s.BrowserHeight = Math.Max(120, Int("BrowserHeight", 260));
                s.BlockHeights = (Get("BlockHeights") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Max(0, Math.Min(4000, n)) : 0).ToArray();
            }
            catch { /* corrupt file: use defaults */ }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = new List<string>
                {
                    "Workbook=" + Workbook,
                    "Sheet=" + Sheet,
                    "Cols=" + string.Join(",", Cols),
                    "Row=" + Row.ToString(CultureInfo.InvariantCulture),
                    "Header=" + Header,
                    "Follow=" + Follow,
                    "SyncSelection=" + SyncSelection,
                    "AutoSave=" + AutoSave,
                    "TopMost=" + TopMost,
                    "FontSize=" + FontSize.ToString(CultureInfo.InvariantCulture),
                    $"Bounds={Bounds.X},{Bounds.Y},{Bounds.Width},{Bounds.Height}",
                    "BrowserOpen=" + BrowserOpen,
                    "BrowserHeight=" + BrowserHeight.ToString(CultureInfo.InvariantCulture),
                    "BlockHeights=" + string.Join(",", BlockHeights.Select(x => x.ToString(CultureInfo.InvariantCulture))),
                };
                File.WriteAllLines(FilePath, lines);
            }
            catch { /* settings are a convenience only */ }
        }
    }
}
