# Excel Toolkit — Office.js add-in for Excel desktop

This add-in adds a **Toolkit → Open Toolkit** button to Excel's **Home** tab. The button opens a task pane with these tools:

| Group | Tools |
|---|---|
| Summarise | Stats for the selection: count, sum, average, median, std dev, min and max |
| Clean | Trim whitespace, UPPER/lower/Proper case, convert text to numbers (handles `1,200` and `12%`), remove duplicate rows, delete blank rows |
| Format | Highlight with a chosen colour, convert to table, autofit columns, freeze top row, clear formatting |
| Insert | Static timestamp in the active cell, sample data sheet |

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
