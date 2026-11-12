"""Virtual camera output (shows up in OBS / Streamlabs / Zoom / Meet)."""
from __future__ import annotations

import cv2
import numpy as np

try:
    import pyvirtualcam
except ImportError:  # pragma: no cover
    pyvirtualcam = None


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
        self._cam = pyvirtualcam.Camera(
            width=self.width, height=self.height, fps=self.fps,
            fmt=pyvirtualcam.PixelFormat.BGR,
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
