// Minimal HTTPS static server for the add-in, using the Office dev certificates.
// Run `npm run certs` once first (or `npm start`, which installs them for you).
"use strict";
const https = require("https");
const fs = require("fs");
const path = require("path");
const devCerts = require("office-addin-dev-certs");

const PORT = Number(process.env.PORT) || 3000;
const ROOT = __dirname;
const TYPES = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".png": "image/png",
  ".xml": "application/xml",
  ".json": "application/json",
};
const SERVED = ["src", "assets"];

(async () => {
  const options = await devCerts.getHttpsServerOptions();
  https
    .createServer(options, (req, res) => {
      const urlPath = decodeURIComponent(new URL(req.url, "https://localhost").pathname);
      const file = path.normalize(path.join(ROOT, urlPath));
      const top = path.relative(ROOT, file).split(path.sep)[0];
      if (!file.startsWith(ROOT + path.sep) || !SERVED.includes(top)) {
        res.writeHead(404).end("Not found");
        return;
      }
      fs.readFile(file, (err, data) => {
        if (err) return res.writeHead(404).end("Not found");
        res.writeHead(200, {
          "Content-Type": TYPES[path.extname(file)] || "application/octet-stream",
          "Cache-Control": "no-store",
        });
        res.end(data);
      });
    })
    .listen(PORT, () => console.log(`Add-in served at https://localhost:${PORT}/src/taskpane.html`));
})();
