"""Create or update a GitHub Release for AlapCamera (uploads standalone AlapCamera.exe).

Usage:
    python scripts/release.py            # creates or updates release for v0.2.0
    python scripts/release.py v0.2.0     # creates or updates release for tag v0.2.0
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
REPO = "ralfarris/AlapCamera"
API_BASE = f"https://api.github.com/repos/{REPO}"
DEFAULT_TAG = "v0.2.0"


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


def build_exe() -> Path:
    subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "build.ps1")],
        cwd=ROOT,
        check=True,
    )
    exe_path = ROOT / "dist" / "AlapCamera.exe"
    if not exe_path.exists():
        raise FileNotFoundError(f"Expected built executable at {exe_path}")
    return exe_path


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
        "Aplikasi virtual camera ultra-ringan untuk Windows (~382 KB single-file `.exe`, tanpa Python).\n\n"
        "### Fitur Utama\n"
        "- **DirectShow Native Capture (FPS Penuh)**: Capture langsung dari kamera fisik pada resolusi & FPS asli.\n"
        "- **Auto Visual Enhance**: Otomatis mencerahkan wajah di ruangan gelap/backlight & menyeimbangkan warna.\n"
        "- **Erase Background (No Background)**: Hapus latar belakang otomatis tanpa kain green screen (*PNG Cutout*, *Green Screen*, *Blur*).\n"
        "- **Auto-Start Virtual Camera (`AlapCamera`)**: Otomatis memasang driver & langsung terdeteksi di Streamlabs Desktop, OBS, Zoom, dan Discord.\n"
        "- **Split Compare (Before/After)** & **Auto-save Settings**.\n\n"
        "### Cara Pakai\n"
        "1. Download **`AlapCamera.exe`** di bawah.\n"
        "2. Klik dua kali **`AlapCamera.exe`** (klik *Yes* pada jendela Admin agar driver `AlapCamera` terbaca oleh Streamlabs/OBS).\n"
        "3. Di Streamlabs / OBS, pilih **`AlapCamera`** pada dropdown *Video Capture Device*.\n"
    )


def create_or_update_release(tag: str) -> str:
    token = get_github_token()
    ensure_tag_pushed(tag)
    exe_path = build_exe()

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

    for asset in rel.get("assets", []):
        if asset.get("name") == exe_path.name:
            gh_request("DELETE", f"{API_BASE}/releases/assets/{asset['id']}", token)

    upload_url = rel["upload_url"].split("{", 1)[0] + f"?name={exe_path.name}"
    gh_request("POST", upload_url, token, exe_path.read_bytes(), content_type="application/octet-stream")
    print(f"{action} release {tag}: {rel['html_url']}")
    return rel["html_url"]


if __name__ == "__main__":
    target_tag = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_TAG
    if not target_tag.startswith("v"):
        target_tag = f"v{target_tag}"
    create_or_update_release(target_tag)
