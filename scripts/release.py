"""Create or update a GitHub Release for AlapCamera.

Usage:
    python scripts/release.py            # creates or updates release for current __version__ (e.g. v0.1.0)
    python scripts/release.py v0.2.0     # creates or updates release for tag v0.2.0
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from alapcamera import __version__  # noqa: E402

REPO = "ralfarris/AlapCamera"
API_BASE = f"https://api.github.com/repos/{REPO}"


def get_github_token() -> str:
    for env_key in ("GH_TOKEN", "GITHUB_TOKEN"):
        if os.environ.get(env_key):
            return os.environ[env_key].strip()
    proc = subprocess.run(
        ["git", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n",
        text=True,
        capture_output=True,
        check=True,
    )
    for line in proc.stdout.splitlines():
        if line.startswith("password="):
            return line.split("=", 1)[1].strip()
    raise RuntimeError("Could not obtain GitHub token from git credential helper or GH_TOKEN.")


def build_zip(tag: str) -> Path:
    dist_dir = ROOT / "dist"
    dist_dir.mkdir(exist_ok=True)
    zip_path = dist_dir / f"AlapCamera-{tag}-windows.zip"
    include_files = [
        "main.py",
        "run.bat",
        "requirements.txt",
        "README.md",
        "scripts/install_virtualcam.ps1",
        "scripts/release.py",
    ]
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for rel in include_files:
            p = ROOT / rel
            if p.exists():
                zf.write(p, f"AlapCamera/{rel}")
        for p in sorted((ROOT / "alapcamera").glob("*.py")):
            zf.write(p, f"AlapCamera/alapcamera/{p.name}")
    return zip_path


def gh_request(method: str, url: str, token: str, data: dict | bytes | None = None,
               content_type: str = "application/json") -> dict:
    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "AlapCamera-Release-Script",
    }
    body = None
    if isinstance(data, dict):
        body = json.dumps(data).encode("utf-8")
        headers["Content-Type"] = content_type
    elif isinstance(data, bytes):
        body = data
        headers["Content-Type"] = content_type
    req = urllib.request.Request(url, data=body, headers=headers, method=method)
    with urllib.request.urlopen(req) as resp:
        raw = resp.read()
        return json.loads(raw.decode("utf-8")) if raw else {}


def ensure_tag_pushed(tag: str) -> None:
    subprocess.run(["git", "tag", "-f", tag], cwd=ROOT, check=True)
    subprocess.run(["git", "push", "-f", "origin", tag], cwd=ROOT, check=True)


def release_notes(tag: str) -> str:
    return (
        f"## AlapCamera {tag}\n\n"
        "Aplikasi virtual camera untuk Windows dengan **AI Auto Visual Enhance** dan **AI Background Removal**.\n\n"
        "### Fitur Utama\n"
        "- **Auto Visual Enhance**: Face-priority auto exposure (BlazeFace AI), low-light boost (CLAHE), "
        "auto white balance, low-light color preservation, adaptive bilateral denoise, dan unsharp mask.\n"
        "- **AI Background**: Blur (bokeh), Remove (solid color / chroma key green screen), dan custom Image background "
        "dengan pilihan model `fast` dan `quality`.\n"
        "- **Virtual Camera Output**: Terintegrasi dengan OBS, Streamlabs, Zoom, Google Meet, dan Discord melalui perangkat "
        "`AlapCamera` (`scripts/install_virtualcam.ps1`) atau fallback ke OBS Virtual Camera.\n"
        "- **Compare (Before/After)** & **Auto-save Settings**.\n\n"
        "### Cara Menjalankan\n"
        "1. Ekstrak file ZIP lalu klik dua kali **`run.bat`** (otomatis menginstal dependency Python jika belum ada).\n"
        "2. Klik **Install 'AlapCamera' device...** (cukup sekali saat pertama kali) untuk mendaftarkan kamera virtual.\n"
        "3. Klik **Start virtual camera**.\n"
    )


def create_or_update_release(tag: str) -> str:
    token = get_github_token()
    ensure_tag_pushed(tag)
    zip_path = build_zip(tag)

    payload = {
        "tag_name": tag,
        "target_commitish": "main",
        "name": f"AlapCamera {tag}",
        "body": release_notes(tag),
        "draft": False,
        "prerelease": False,
    }

    try:
        rel = gh_request("GET", f"{API_BASE}/releases/tags/{tag}", token)
        rel = gh_request("PATCH", f"{API_BASE}/releases/{rel['id']}", token, payload)
        action = "Updated"
    except urllib.error.HTTPError as exc:
        if exc.code != 404:
            raise
        rel = gh_request("POST", f"{API_BASE}/releases", token, payload)
        action = "Created"

    # Replace existing asset with the same name if present
    for asset in rel.get("assets", []):
        if asset.get("name") == zip_path.name:
            gh_request("DELETE", f"{API_BASE}/releases/assets/{asset['id']}", token)

    upload_url = rel["upload_url"].split("{", 1)[0] + f"?name={zip_path.name}"
    gh_request("POST", upload_url, token, zip_path.read_bytes(), content_type="application/zip")
    print(f"{action} release {tag}: {rel['html_url']}")
    return rel["html_url"]


if __name__ == "__main__":
    target_tag = sys.argv[1] if len(sys.argv) > 1 else f"v{__version__}"
    if not target_tag.startswith("v"):
        target_tag = f"v{target_tag}"
    create_or_update_release(target_tag)
