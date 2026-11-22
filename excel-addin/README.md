# Excel Toolkit — Office.js add-in for Excel desktop

This add-in adds a **Toolkit** group to Excel's **Home** tab with two buttons: **Open Toolkit** opens the tools below, and **Cell Viewer** opens the long-text viewer described further down.

| Group | Tools |
|---|---|
| Summarise | Stats for the selection: count, sum, average, median, std dev, min and max |
| Clean | Trim whitespace, UPPER/lower/Proper case, convert text to numbers (handles `1,200` and `12%`), remove duplicate rows, delete blank rows |
| Format | Highlight with a chosen colour, convert to table, autofit columns, freeze top row, clear formatting |
| Insert | Static timestamp in the active cell, sample data sheet |

## Cell Viewer (Home → Toolkit → Cell Viewer)

The Cell Viewer works like a Watch Window for long text. It shows 2 to 4 cells from the same row in large, wrapping text boxes. You can step through the rows while the chosen columns stay the same, and edit the text directly.

1. In Excel, select the cells you want to see in one row. Hold **Ctrl** (**Cmd** on Mac) and click to pick columns that aren't next to each other, for example `B5`, `C5`, `F5`. Put the column you want to edit **rightmost**: it gets the big edit box. You can also type column letters under *Settings*.
2. Click **Use selection**.
3. Move between rows with **▲ / ▼**, **Alt+↑ / Alt+↓**, or by typing a row number. If *Follow the Excel selection* is on, clicking a cell in Excel jumps the viewer to that row.
4. Edit the text in any box, then click **Save** or press **Ctrl+S**. Unsaved boxes are marked ●. If *Save automatically when changing row* is on, edits are saved when you move to another row. Formula cells are shown read-only.
5. **Pop out** opens the viewer in its own window that floats above Excel and can be moved and resized. **Dock** puts it back. Keep the Cell Viewer task pane open while the window is out: the window works through the pane, and you can make the pane narrow.
6. **Browse column …** lists every filled cell of the edit column, with a filter box. Click an entry to read its full text, then choose **Insert at cursor** (or double-click the entry), **Replace**, **Append**, **Copy** or **Go to row**.

Details:
- If someone changes a cell in Excel after the viewer loaded it, saving does not silently overwrite it. You'll get a choice: **Overwrite** or **Discard mine, reload**.
- **A− / A+** change the text size. Your columns and settings are remembered.
- A cell holds at most 32,767 characters, and the character counter warns you when you go past that.
- On Windows, if pop-out isn't available in your Office version, drag the task pane's title bar away from the edge of Excel to make the pane itself float.

## Toolkit pane

The cleaning tools skip formula cells, and every tool works only on the part of the selection that holds data (selecting whole columns is fine).

## Requirements
- Excel for Windows or Mac (Microsoft 365, or Excel 2019 and later)
- Node.js 18 or newer

## Run it in desktop Excel
```bash
cd excel-addin
npm install
npm run certs   # one time: installs a trusted localhost HTTPS certificate
npm start       # starts https://localhost:3000 and opens Excel with the add-in loaded
```
Then go to **Home → Toolkit → Open Toolkit**. To unload the add-in, run `npm run stop`.

### Manual sideload (if `npm start` can't open Excel)
Run `npm run dev-server` and keep it running. Then:
- **Windows:** put `manifest.xml` in a shared network folder. In Excel, open *File → Options → Trust Center → Trust Center Settings → Trusted Add-in Catalogs* and add that folder's path. Restart Excel, then go to *Home → Add-ins → Advanced → Shared Folder*.
- **Mac:** copy `manifest.xml` to `~/Library/Containers/com.microsoft.Excel/Data/Documents/wef/` (create the folder if it doesn't exist), restart Excel, then go to *Home → Add-ins → My Add-ins*.

## Customising
- To add a tool, add a `<button data-action="name">` to `src/taskpane.html` and a matching `name()` function to the `actions` object in `src/taskpane.js`.
- Before you deploy for real, host `src/` and `assets/` on any HTTPS server. Then replace every `https://localhost:3000` in `manifest.xml` and give the add-in a new `<Id>` GUID if you fork it.
- `npm run validate` checks the manifest against Microsoft's online validator.
