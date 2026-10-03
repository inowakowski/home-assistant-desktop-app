"""Builds the whole website into dist/: the landing page from website/ at the root, the documentation under docs/.

Run from the repository root, after "pip install -r docs/requirements.txt":

    python scripts/build_site.py

SITE_URL is the address the site is published at, without a trailing slash; it ends up in the canonical
and language links. Preview the result with "python -m http.server -d dist".
"""

import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"
# Pictures the landing page shares with the documentation, so there is one copy of each to keep up to date.
SHARED_IMAGES = ["logo.png", "overview.png"]


def main() -> None:
    site_url = os.environ.get("SITE_URL", "http://localhost:8000").rstrip("/")

    shutil.rmtree(DIST, ignore_errors=True)
    shutil.copytree(ROOT / "website", DIST)
    (DIST / "img").mkdir()
    for name in SHARED_IMAGES:
        shutil.copy2(ROOT / "docs" / "assets" / "img" / name, DIST / "img" / name)

    for page in DIST.rglob("*.html"):
        page.write_text(page.read_text(encoding="utf-8").replace("{{SITE_URL}}", site_url), encoding="utf-8")

    subprocess.run(
        [sys.executable, "-m", "mkdocs", "build", "--strict", "--site-dir", str(DIST / "docs")],
        cwd=ROOT,
        env={**os.environ, "DOCS_SITE_URL": f"{site_url}/docs/"},
        check=True,
    )


if __name__ == "__main__":
    main()
