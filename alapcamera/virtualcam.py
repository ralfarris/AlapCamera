"""Virtual camera output (shows up as 'AlapCamera' in Streamlabs / OBS / Zoom).

Checks if 'AlapCamera' is registered in HKLM (required for elevated apps like
Streamlabs Desktop that run as Administrator). If not yet in HKLM, runs the
one-time elevated installer (scripts/install_virtualcam.ps1) and also provides
an HKCU fallback.
"""
from __future__ import annotations

import ctypes
import os
import subprocess
import sys
import urllib.request
from pathlib import Path

import cv2
import numpy as np

try:
    import pyvirtualcam
except ImportError:  # pragma: no cover
    pyvirtualcam = None

DEVICE_NAME = "AlapCamera"
_UNITY_CLSID = "{5C2CD55C-92AD-4999-8666-912BD3E70010}"
_VIDEO_CAT = "{860BB310-5D01-11D0-BD3B-00A0C911CE86}"
_DLLS = (
    ("UnityCaptureFilter64.dll", "https://github.com/schellingb/UnityCapture/raw/master/Install/UnityCaptureFilter64.dll"),
    ("UnityCaptureFilter32.dll", "https://github.com/schellingb/UnityCapture/raw/master/Install/UnityCaptureFilter32.dll"),
)


def is_hklm_registered(device_name: str = DEVICE_NAME) -> bool:
    """Return True if 'AlapCamera' is registered system-wide in HKLM (needed by Admin apps like Streamlabs)."""
    if sys.platform != "win32":
        return True
    try:
        import winreg
        inst_key_path = rf"Software\Classes\CLSID\{_VIDEO_CAT}\Instance\{_UNITY_CLSID}"
        clsid_key_path = rf"Software\Classes\CLSID\{_UNITY_CLSID}\InprocServer32"
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, inst_key_path) as k:
            fn, _ = winreg.QueryValueEx(k, "FriendlyName")
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, clsid_key_path) as k:
            dll_p, _ = winreg.QueryValueEx(k, "")
        return fn == device_name and Path(dll_p).exists()
    except OSError:
        return False


def _ensure_dlls_cached() -> Path:
    base_dir = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "AlapCamera" / "driver"
    base_dir.mkdir(parents=True, exist_ok=True)
    for fname, url in _DLLS:
        p = base_dir / fname
        if not p.exists() or p.stat().st_size < 100_000:
            urllib.request.urlretrieve(url, p)
    return base_dir / "UnityCaptureFilter64.dll"


def install_hklm_driver() -> bool:
    """Run scripts/install_virtualcam.ps1 with UAC elevation to register 'AlapCamera' in HKLM."""
    if sys.platform != "win32":
        return False
    try:
        _ensure_dlls_cached()
        script = Path(__file__).resolve().parent.parent / "scripts" / "install_virtualcam.ps1"
        if not script.exists():
            return False

        class SHELLEXECUTEINFOW(ctypes.Structure):
            _fields_ = [
                ("cbSize", ctypes.c_ulong),
                ("fMask", ctypes.c_ulong),
                ("hwnd", ctypes.c_void_p),
                ("lpVerb", ctypes.c_wchar_p),
                ("lpFile", ctypes.c_wchar_p),
                ("lpParameters", ctypes.c_wchar_p),
                ("lpDirectory", ctypes.c_wchar_p),
                ("nShow", ctypes.c_int),
                ("hInstApp", ctypes.c_void_p),
                ("lpIDList", ctypes.c_void_p),
                ("lpClass", ctypes.c_wchar_p),
                ("hkeyClass", ctypes.c_void_p),
                ("dwHotKey", ctypes.c_ulong),
                ("hIcon", ctypes.c_void_p),
                ("hProcess", ctypes.c_void_p),
            ]

        sei = SHELLEXECUTEINFOW()
        sei.cbSize = ctypes.sizeof(sei)
        sei.fMask = 0x00000040  # SEE_MASK_NOCLOSEPROCESS
        sei.lpVerb = "runas"
        sei.lpFile = "powershell.exe"
        sei.lpParameters = f'-NoProfile -ExecutionPolicy Bypass -File "{script}" -Name "{DEVICE_NAME}"'
        sei.nShow = 0  # SW_HIDE

        if ctypes.windll.shell32.ShellExecuteExW(ctypes.byref(sei)) and sei.hProcess:
            ctypes.windll.kernel32.WaitForSingleObject(sei.hProcess, 30000)
            ctypes.windll.kernel32.CloseHandle(sei.hProcess)

        return is_hklm_registered(DEVICE_NAME)
    except Exception as exc:
        print("HKLM driver install warning:", exc)
        return False


def ensure_virtualcam_registered(device_name: str = DEVICE_NAME) -> bool:
    """Ensure 'AlapCamera' DirectShow filter is registered (prefers HKLM for Streamlabs Admin)."""
    if sys.platform != "win32":
        return False
    if is_hklm_registered(device_name):
        return True

    # Try one-time HKLM registration (needed because Streamlabs Desktop runs as Administrator
    # and Windows COM blocks Administrator processes from loading HKCU CLSIDs)
    if install_hklm_driver():
        return True

    # Fallback: HKCU registration for non-elevated apps
    try:
        import winreg

        dll_path = _ensure_dlls_cached()
        inst_key_path = rf"Software\Classes\CLSID\{_VIDEO_CAT}\Instance\{_UNITY_CLSID}"
        clsid_key_path = rf"Software\Classes\CLSID\{_UNITY_CLSID}"

        advapi32 = ctypes.windll.advapi32
        advapi32.RegOverridePredefKey.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        advapi32.RegOverridePredefKey.restype = ctypes.c_long

        h_cu_classes = winreg.CreateKeyEx(
            winreg.HKEY_CURRENT_USER, r"Software\Classes", 0, winreg.KEY_ALL_ACCESS
        )
        advapi32.RegOverridePredefKey(winreg.HKEY_CLASSES_ROOT, h_cu_classes.handle)
        try:
            dll = ctypes.OleDLL(str(dll_path))
            dll.DllRegisterServer()
        finally:
            advapi32.RegOverridePredefKey(winreg.HKEY_CLASSES_ROOT, None)

        for p in (inst_key_path, clsid_key_path):
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, p, 0, winreg.KEY_SET_VALUE) as k:
                winreg.SetValueEx(k, "FriendlyName", 0, winreg.REG_SZ, device_name)
                winreg.SetValueEx(k, "", 0, winreg.REG_SZ, device_name)
        return True
    except Exception as exc:
        print("Virtualcam auto-registration warning:", exc)
        return False


class VirtualCamera:
    def __init__(self, width: int, height: int, fps: int = 30):
        self.width = width
        self.height = height
        self.fps = fps
        self._cam = None
        self.device_name = ""

    def start(self) -> str:
        if pyvirtualcam is None:
            raise RuntimeError("pyvirtualcam is not installed")
        ensure_virtualcam_registered(DEVICE_NAME)
        attempts = [
            dict(backend="unitycapture", device=DEVICE_NAME),
            dict(backend="unitycapture"),
            dict(backend="obs"),
        ]
        errors = []
        for kw in attempts:
            try:
                self._cam = pyvirtualcam.Camera(
                    width=self.width, height=self.height, fps=self.fps,
                    fmt=pyvirtualcam.PixelFormat.BGR, **kw)
                break
            except Exception as exc:
                errors.append(f"{kw}: {exc}")
        if self._cam is None:
            raise RuntimeError(
                "No virtual camera driver found.\n" + "\n".join(errors)
            )
        self.device_name = self._cam.device
        return self.device_name

    def send(self, frame_bgr: np.ndarray) -> None:
        if self._cam is None:
            return
        h, w = frame_bgr.shape[:2]
        if (w, h) != (self.width, self.height):
            frame_bgr = cv2.resize(frame_bgr, (self.width, self.height))
        self._cam.send(np.ascontiguousarray(frame_bgr))

    @property
    def running(self) -> bool:
        return self._cam is not None

    def stop(self) -> None:
        if self._cam is not None:
            self._cam.close()
        self._cam = None
