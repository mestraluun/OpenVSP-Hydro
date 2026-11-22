using System.Runtime.InteropServices;
using ExcelDna.Integration;
using ExcelDna.Integration.CustomUI;

namespace CellViewer
{
    public sealed class AddIn : IExcelAddIn
    {
        public void AutoOpen() { }

        public void AutoClose() => ViewerHost.CloseIfOpen();
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
          <button id='CellViewerOpen' label='Cell Viewer' size='large' imageMso='ZoomDialog'
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
