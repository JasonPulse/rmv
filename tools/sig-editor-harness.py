#!/usr/bin/env python3
"""Drives wwwroot/js/signature-editor.js in a real browser.

The editor page needs a Discord sign-in, so the script itself was never covered by
anything: the C# suite proves the page and the renderer, and the dragging and the
token buttons were only ever checked by hand. This serves the real file with the same
data attributes the Razor view writes, and answers the preview request with the same
shape SignatureModel.OnPostPreviewAsync returns.

    python3 tools/sig-editor-harness.py 8099
"""
import json
import pathlib
import re
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import parse_qs, urlparse

ROOT = pathlib.Path(__file__).resolve().parent.parent / "src" / "Rmv.Web" / "wwwroot"

DESIGN = {
    "background": "Colour",
    "backgroundKey": None,
    "colour": "#101820",
    # The shipped default's own positions, because where a new line lands depends on
    # where the last one is and 134 is what made "I added a line and it didn't show
    # up" happen.
    "elements": [
        {"x": 12, "y": 18, "align": "Left", "font": "vollkorn", "size": 22,
         "colour": "#ffcc66", "outline": None, "characterId": 1,
         "template": "%Name%%SP%%Class%"},
        {"x": 12, "y": 48, "align": "Left", "font": "vollkorn", "size": 17,
         "colour": "#cfd6e4", "outline": None, "characterId": None,
         "template": "%User% plays %AllChars%"},
        {"x": 12, "y": 134, "align": "Left", "font": "vollkorn", "size": 14,
         "colour": "#a89f8c", "outline": None, "characterId": None,
         "template": "%User% has played %AllChars% characters in %AllGames% games"},
    ],
}

# What a name actually is once it is drawn, which is the point of the preview.
VALUES = {
    "Name": "Milliennial", "Class": "Skald", "SP": " - ",
    "User": "Property", "AllChars": "4", "AllGames": "3",
}

PAGE = """<!doctype html>
<html><head><meta charset="utf-8"><title>editor harness</title>
<style>
  body { background:#0a0c12; color:#cfd6e4; font-family:system-ui; margin:2rem; }
  .sig__canvas { position:relative; outline:1px solid #2a3242; }
  .sig__line { position:absolute; white-space:pre; cursor:move; }
  .sig__line--active { outline:1px dashed #ffcc66; }
  .input { display:block; width:22rem; }
  .chip { margin:2px; }
</style></head><body>
<div class="sig sig__layout" data-editor
     data-canvas-width="520" data-canvas-height="160" data-max-elements="12">
  <input type="hidden" name="Design" data-design value='__DESIGN__' />
  <div class="sig__canvas" data-stage style="width:520px;height:160px"></div>
  <div data-elements data-characters='__CHARACTERS__'
       data-fonts='["vollkorn","cinzel"]' data-preview='__PREVIEW__'></div>
  <p><button type="button" data-add>Add a line</button></p>
  <p data-element-count></p>
  <input type="color" data-colour />
  <div>
    <button class="chip" type="button" data-token="Name">%Name%</button>
    <button class="chip" type="button" data-token="SP">%SP%</button>
    <button class="chip" type="button" data-token="AllGames">%AllGames%</button>
  </div>
</div>
<script src="/js/signature-editor.js"></script>
</body></html>
"""


def resolve(template):
    return re.sub(r"%([A-Za-z][A-Za-z0-9]*)%",
                  lambda m: VALUES.get(m.group(1), m.group(0)), template)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def _send(self, body, kind="text/html; charset=utf-8"):
        raw = body if isinstance(body, bytes) else body.encode()
        self.send_response(200)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        path = urlparse(self.path).path

        if path.startswith("/js/") or path.startswith("/css/"):
            file = ROOT / path.lstrip("/")
            if not file.is_file():
                self.send_error(404)
                return
            kind = "text/javascript" if file.suffix == ".js" else "text/css"
            self._send(file.read_bytes(), kind)
            return

        preview = [resolve(e["template"]) for e in DESIGN["elements"]]
        self._send(PAGE
                   .replace("__DESIGN__", json.dumps(DESIGN))
                   .replace("__CHARACTERS__", json.dumps(
                       [{"id": 1, "label": "Milliennial (Dark Age of Camelot)"},
                        {"id": 2, "label": "Milliennial (Final Fantasy XI)"}]))
                   .replace("__PREVIEW__", json.dumps(preview)))

    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0))).decode()

        # The form post the editor makes. Only the design matters here.
        design = parse_qs(body).get("Design", ["{}"])[0]
        if "Design" not in body:
            match = re.search(r'name="Design"\r\n\r\n(.*?)\r\n--', body, re.S)
            design = match.group(1) if match else "{}"
        else:
            match = re.search(r'name="Design"\r\n\r\n(.*?)\r\n--', body, re.S)
            if match:
                design = match.group(1)

        try:
            elements = json.loads(design).get("elements", [])
        except json.JSONDecodeError:
            self.send_error(400)
            return

        self._send(json.dumps([resolve(e.get("template", "")) for e in elements]),
                   "application/json")


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8099
    print(f"harness on http://localhost:{port}/")
    HTTPServer(("127.0.0.1", port), Handler).serve_forever()
