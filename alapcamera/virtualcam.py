"""Virtual camera output (shows up in OBS / Streamlabs / Zoom / Meet).

Preferred device: our own "AlapCamera" (Unity Capture filter registered by
``scripts/install_virtualcam.ps1``). Falls back to OBS Virtual Camera or any
other Unity Capture device.
"""
from __future__ import annotations

import cv2
import numpy as np

try:
    import pyvirtualcam
except ImportError:  # pragma: no cover
    pyvirtualcam = None

DEVICE_NAME = "AlapCamera"


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
        attempts = [
            dict(backend="unitycapture", device=DEVICE_NAME),  # our branded device
            dict(backend="obs"),
            dict(backend="unitycapture"),
        ]
        errors = []
        for kw in attempts:
            try:
                self._cam = pyvirtualcam.Camera(
                    width=self.width, height=self.height, fps=self.fps,
                    fmt=pyvirtualcam.PixelFormat.RGB, **kw)
                break
            except Exception as exc:
                errors.append(f"{kw}: {exc}")
        if self._cam is None:
            raise RuntimeError(
                "No virtual camera driver found.\n"
                "Run scripts\\install_virtualcam.ps1 (installs 'AlapCamera') "
                "or install OBS Studio.\n\n" + "\n".join(errors))
        self.device_name = self._cam.device
        return self.device_name

    def send(self, frame_bgr: np.ndarray) -> None:
        if self._cam is None:
            return
        h, w = frame_bgr.shape[:2]
        if (w, h) != (self.width, self.height):
            frame_bgr = cv2.resize(frame_bgr, (self.width, self.height))
        self._cam.send(cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB))

    @property
    def running(self) -> bool:
        return self._cam is not None

    def stop(self) -> None:
        if self._cam is not None:
            self._cam.close()
        self._cam = None
