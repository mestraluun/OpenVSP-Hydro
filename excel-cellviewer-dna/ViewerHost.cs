using System;
using System.Threading;
using System.Windows.Forms;

namespace CellViewer
{
    /// <summary>
    /// Opens the viewer window on its own UI thread. A separate thread keeps keyboard
    /// handling (Tab, arrows, shortcuts) reliable and keeps the window responsive while
    /// Excel is busy.
    /// </summary>
    internal static class ViewerHost
    {
        static readonly object Gate = new object();
        static ViewerForm _form;
        static bool _starting;

        /// <summary>Call on Excel's main thread.</summary>
        public static void Show()
        {
            var app = ExcelBridge.Init();
            var excelHwnd = new IntPtr(app.Hwnd);

            lock (Gate)
            {
                if (_form != null && !_form.IsDisposed)
                {
                    var f = _form;
                    try { f.BeginInvoke(new Action(f.BringToFrontAndFocus)); } catch { /* closing */ }
                    return;
                }
                if (_starting) return;
                _starting = true;
            }

            var thread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    var form = new ViewerForm(excelHwnd);
                    lock (Gate) { _form = form; _starting = false; }
                    Action<SelectionInfo> onSel = form.OnExcelSelection;
                    ExcelBridge.SelectionChanged += onSel;
                    try { Application.Run(form); }
                    finally { ExcelBridge.SelectionChanged -= onSel; }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Cell Viewer could not start:\n\n" + ex.Message, "Cell Viewer",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    lock (Gate) { _form = null; _starting = false; }
                }
            })
            {
                IsBackground = true,
                Name = "Cell Viewer UI",
            };
            thread.SetApartmentState(ApartmentState.STA); // needed for the clipboard
            thread.Start();
        }

        public static void CloseIfOpen()
        {
            ViewerForm f;
            lock (Gate) f = _form;
            if (f == null || f.IsDisposed) return;
            try { f.BeginInvoke(new Action(() => f.CloseWithoutPrompt())); } catch { /* already gone */ }
        }
    }
}
