"""Virtual camera output (shows up as 'AlapCamera' in Streamlabs / OBS / Zoom).

Automatically downloads and registers the Unity Capture DirectShow filter under
the name 'AlapCamera' in HKCU (Current User registry) on first run — no Admin
or UAC prompt required!
"""
from __future__ import annotations

import ctypes
import os
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
_DLL_URL = "https://github.com/schellingb/UnityCapture/raw/master/Install/UnityCaptureFilter64.dll"


def ensure_virtualcam_registered(device_name: str = DEVICE_NAME) -> bool:
    """Register the 'AlapCamera' DirectShow virtual webcam in HKCU (no Admin needed)."""
    if sys.platform != "win32":
        return False
    try:
        import winreg

        inst_key_path = rf"Software\Classes\CLSID\{_VIDEO_CAT}\Instance\{_UNITY_CLSID}"
        clsid_key_path = rf"Software\Classes\CLSID\{_UNITY_CLSID}"

        # Fast check if already registered with our name and DLL exists
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, inst_key_path) as k:
                fn, _ = winreg.QueryValueEx(k, "FriendlyName")
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, rf"{clsid_key_path}\InprocServer32") as k:
                dll_p, _ = winreg.QueryValueEx(k, "")
            if fn == device_name and Path(dll_p).exists():
                return True
        except OSError:
            pass

        base_dir = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "AlapCamera" / "driver"
        base_dir.mkdir(parents=True, exist_ok=True)
        dll_path = base_dir / "UnityCaptureFilter64.dll"
        if not dll_path.exists() or dll_path.stat().st_size < 100_000:
            urllib.request.urlretrieve(_DLL_URL, dll_path)

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
