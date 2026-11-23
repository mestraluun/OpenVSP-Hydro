using System;
using System.Reflection;
using System.Runtime.InteropServices;
using ExcelDna.Integration;
using ExcelDna.Integration.CustomUI;
using Microsoft.Win32;

namespace CellViewer
{
    public sealed class AddIn : IExcelAddIn
    {
        public void AutoOpen() { }

        public void AutoClose() => ViewerHost.CloseIfOpen();
    }

    /// <summary>
    /// Ways to open the viewer that don't depend on the ribbon:
    ///  • Add-ins tab → Menu Commands → Cell Viewer → Open Cell Viewer
    ///  • Alt+F8, type CellViewer, Run
    /// and a worksheet function to check that the add-in is loaded: =CellViewerInfo()
    /// </summary>
    public static class Commands
    {
        [ExcelCommand(Name = "CellViewer", MenuName = "Cell Viewer", MenuText = "Open Cell Viewer",
            Description = "Open the Cell Viewer window")]
        public static void OpenCellViewer() => ViewerHost.Show();

        [ExcelFunction(Name = "CellViewerInfo", Description = "Shows that the Cell Viewer add-in is loaded, with version details.")]
        public static string CellViewerInfo()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return $"Cell Viewer {v.Major}.{v.Minor}.{v.Build} · {(IntPtr.Size == 8 ? "64" : "32")}-bit · .NET Framework {NetFrameworkVersion()} · Excel {ExcelDnaUtil.ExcelVersion}";
        }

        static string NetFrameworkVersion()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (!(k?.GetValue("Release") is int r)) return Environment.Version.ToString();
                    return r >= 528040 ? "4.8+" : r >= 461808 ? "4.7.2" : r >= 461308 ? "4.7.1" : r >= 460798 ? "4.7"
                         : r >= 394802 ? "4.6.2" : r >= 394254 ? "4.6.1" : r >= 393295 ? "4.6" : "4.5.x (too old)";
                }
            }
            catch { return Environment.Version.ToString(); }
        }
    }

    /// <summary>Adds Home → Cell Viewer → Cell Viewer to the ribbon.</summary>
    [ComVisible(true)]
    public sealed class Ribbon : ExcelRibbon
    {
        public override string GetCustomUI(string ribbonId) => @"
<customUI xmlns='http://schemas.microsoft.com/office/2009/07/customui'>
  <ribbon>
    <tabs>
      <tab idMso='TabHome'>
        <group id='CellViewerGroup' label='Cell Viewer'>
          <button id='CellViewerOpen' label='Cell Viewer' size='large' imageMso='FindDialog'
                  screentip='Cell Viewer'
                  supertip='A floating window for reading and editing long text in 2 to 4 columns of a row. Step through rows and reuse text from other rows.'
                  onAction='OnOpenViewer'/>
        </group>
      </tab>
    </tabs>
  </ribbon>
</customUI>";

        public void OnOpenViewer(IRibbonControl control) => ViewerHost.Show();
    }
}
