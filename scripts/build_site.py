"""Builds the landing page from website/ into dist/. Needs nothing but Python.

Run from the repository root:

    python scripts/build_site.py

SITE_URL is the address the landing page is published at and DOCS_URL the address of the documentation,
which is a site of its own ("mkdocs build"); both without a trailing slash. The defaults suit a preview
on this computer: "python -m http.server 8001 -d dist" next to "mkdocs serve".
"""

import os
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"
# Pictures the landing page shares with the documentation, so there is one copy of each to keep up to date.
SHARED_IMAGES = ["logo.png", "overview.png"]


def main() -> None:
    addresses = {
        "{{SITE_URL}}": os.environ.get("SITE_URL", "http://localhost:8001").rstrip("/"),
        "{{DOCS_URL}}": os.environ.get("DOCS_URL", "http://127.0.0.1:8000").rstrip("/"),
    }

    shutil.rmtree(DIST, ignore_errors=True)
    shutil.copytree(ROOT / "website", DIST)
    (DIST / "img").mkdir()
    for name in SHARED_IMAGES:
        shutil.copy2(ROOT / "docs" / "assets" / "img" / name, DIST / "img" / name)

    for page in DIST.rglob("*.html"):
        text = page.read_text(encoding="utf-8")
        for placeholder, address in addresses.items():
            text = text.replace(placeholder, address)
        page.write_text(text, encoding="utf-8")


if __name__ == "__main__":
    main()
