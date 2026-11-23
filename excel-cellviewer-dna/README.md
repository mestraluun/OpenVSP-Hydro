# Cell Viewer: Excel add-in (.xll) for Windows

Cell Viewer is a floating window, like Excel's Watch Window, for reading and editing **long text**. It shows 2 to 4 cells from the same row in large, wrapping text boxes. You can step through the rows while the columns stay fixed, edit the text and save it back, and browse the edit column to reuse text from other rows.

It is one `.xll` file. **No installer and no admin rights are needed**, and nothing is sent anywhere.

## Install (per user, no admin)
1. Check whether your Excel is 64-bit or 32-bit: **File → Account → About Excel**. The first line ends in "64-bit" or "32-bit".
2. Take the matching file: **`CellViewer64.xll`** for 64-bit (most installations) or `CellViewer32.xll` for 32-bit.
3. Copy it to a folder of your own, for example `C:\Users\<you>\ExcelAddIns\`.
4. Right-click the file → **Properties**. If there is an **Unblock** box at the bottom, tick it and click OK. Windows blocks downloaded add-ins otherwise.
5. In Excel: **File → Options → Add-ins**. At the bottom, set *Manage* to **Excel Add-ins** and click **Go… → Browse…**. Pick the `.xll`, make sure it is ticked, and click OK.

Excel now loads it every time it starts. A **Cell Viewer** button appears on the **Home** tab, at the right.

**No button?**
1. Type `=CellViewerInfo()` in any cell.
   - If it shows the version, the add-in is loaded and only the ribbon button is missing. Open the viewer from **Add-ins tab → Menu Commands → Cell Viewer → Open Cell Viewer**, or press **Alt+F8**, type `CellViewer` and click **Run**.
   - If it shows `#NAME?`, the add-in is not loaded. See the next points.
2. Check **File → Options → Add-ins → Manage: Disabled Items → Go…**. If Cell Viewer is listed, enable it.
3. Check **File → Options → Trust Center → Trust Center Settings → Add-ins**. "Require Application Add-ins to be signed" or "Disable all Application Add-ins" block it.
4. The add-in needs .NET Framework 4.6.2 or newer, which is standard on Windows 10/11.

If Excel says the add-in is disabled or unsigned, your organisation blocks unsigned add-ins (*Trust Center → Add-ins*). Ask IT to allow it, or use the Office.js version in `../excel-addin`.

To remove it, go to the same dialog, untick it, and delete the file.

## Use
1. In Excel, select the cells to watch in one row. Hold **Ctrl** and click to pick columns that aren't next to each other, for example `B5`, `C5`, `F5`. Put the column you want to edit **rightmost**: it gets the big edit box.
2. Press **Ctrl+Shift+Q**, or click **Cell Viewer** on the Home tab (or **Add-ins tab → Menu Commands → Cell Viewer**). Then click **Use selection**. You can also type column letters under ⚙ Settings.
3. Change rows with **▲ / ▼** or **Alt+↑ / Alt+↓**, or by typing a row number and pressing Enter. If *Follow the Excel selection* is on, clicking a cell in Excel jumps the viewer to that row.
4. Edit any box. Unsaved boxes are marked ●. Press **Ctrl+S** or click **Save**. With *Save automatically when changing row* on, edits are saved when you move to another row. Formula cells are shown read-only.
5. **Browse column ▾** (or **Ctrl+F**) lists every filled cell of the edit column, with a filter box. Click an entry to read all of it, then:
   - **Insert at cursor** (or double-click / Enter) puts the text where your cursor is in the edit box;
   - **Replace**, **Append** or **Copy**;
   - **Go to row** jumps the viewer to that row.

More:
- **F5** reloads from Excel and keeps your unsaved edits.
- **Resize the boxes:** drag the dotted bar under a box up or down. Double-click the bar to reset it. The edit box takes the remaining space.
- **A− / A+** change the text size.
- *Stay on top of all windows* keeps it above other programs too. By default it stays above Excel only and hides when Excel is minimized.
- If a cell was changed in Excel after the viewer loaded it, you're asked before anything is overwritten.
- Line breaks inside cells (Alt+Enter in Excel) are kept.
- A cell holds at most 32,767 characters. The counter turns red if you go over.
- Your columns, options, window position and size are remembered in `%APPDATA%\CellViewer\settings.txt`.

## Build
You need Windows or Linux with the .NET 8 SDK; packing also works on Linux.
```
cd excel-cellviewer-dna
dotnet build -c Release
```
The output is `bin/Release/net462/publish/CellViewer-AddIn64-packed.xll` (64-bit) and `CellViewer-AddIn-packed.xll` (32-bit).

The GitHub workflow `.github/workflows/cellviewer-xll.yml` builds both on Windows on every push that changes this folder. Download them from the repository's **Actions** tab → latest run → *CellViewer-xll*.

## How it works
- `AddIn.cs` holds the ribbon button.
- `ViewerHost.cs` runs the window on its own UI thread, so typing and shortcuts behave normally. The window is owned by Excel's main window.
- `ExcelBridge.cs` does every workbook read and write on Excel's main thread through `ExcelAsyncUtil.QueueAsMacro`. If you are in the middle of editing a cell in Excel, the viewer waits and says so.
- `ViewerForm.cs` is the window. `Settings.cs` stores the preferences.
