"""Physical camera discovery and capture at native hardware FPS."""
from __future__ import annotations

import platform
import threading
import time
from dataclasses import dataclass

import cv2
import numpy as np

_BACKEND = cv2.CAP_DSHOW if platform.system() == "Windows" else cv2.CAP_ANY


@dataclass
class CameraInfo:
    index: int
    name: str
    width: int = 0
    height: int = 0

    def __str__(self) -> str:
        if self.width > 0 and self.height > 0:
            return f"{self.name} ({self.width}x{self.height})"
        return self.name


def device_names() -> list[str]:
    """DirectShow device names in index order (Windows, needs pygrabber)."""
    if platform.system() != "Windows":
        return []
    try:
        from pygrabber.dshow_graph import FilterGraph
        return list(FilterGraph().get_input_devices())
    except Exception:
        return []


# Virtual output sinks that should never be offered as camera inputs
_VIRTUAL_SINKS = (
    "alapcamera",
    "streamlabs",
    "lsvcam",
    "bytecast",
    "unity video capture",
    "obs virtual",
    "xsplit",
    "manycam",
    "snap camera",
)

# Phone-bridge / secondary software cameras: selectable, but hardware cameras are preferred first
_SOFTWARE_HINTS = ("virtual", "camo", "droidcam", "iriun")


def is_virtual_sink(name: str) -> bool:
    n = name.lower()
    return any(s in n for s in _VIRTUAL_SINKS)


def is_software_camera(name: str) -> bool:
    n = name.lower()
    return is_virtual_sink(name) or any(h in n for h in _SOFTWARE_HINTS)


def default_camera(cams: list["CameraInfo"]) -> int:
    """Index (into ``cams``) of the best default: first real hardware camera."""
    for i, c in enumerate(cams):
        if not is_software_camera(c.name):
            return i
    return 0 if cams else -1


def list_cameras(max_index: int = 6) -> list[CameraInfo]:
    """Return available input cameras instantly via DirectShow (or probe fallback)."""
    names = device_names()
    if names:
        return [
            CameraInfo(idx, name)
            for idx, name in enumerate(names)
            if not is_virtual_sink(name)
        ]

    found: list[CameraInfo] = []
    for idx in range(max_index):
        cap = cv2.VideoCapture(idx, _BACKEND)
        if not cap.isOpened():
            cap.release()
            continue
        ok, frame = cap.read()
        if ok and frame is not None:
            h, w = frame.shape[:2]
            found.append(CameraInfo(idx, f"Camera {idx}", w, h))
        cap.release()
    return found


class CameraStream:
    """Grabs frames from a physical camera at its native resolution & full FPS.

    Avoids forcing non-native resolutions on USB 2.0 webcams (which drops
    hardware frame rate from 30 fps to ~7.5 fps), automatically calibrates
    DirectShow sensor black-level/exposure so dark rooms never clip to black,
    and auto-reconnects if another app (e.g. Streamlabs) is temporarily holding
    the physical webcam.
    """

    def __init__(self, index: int, width: int | None = None, height: int | None = None, name: str = ""):
        self.index = index
        self.name = name or f"Camera {index}"
        self.req_width = width
        self.req_height = height
        self.auto_tune_sensor = True
        self.is_busy = False
        self._cap: cv2.VideoCapture | None = None
        self._frame: np.ndarray | None = None
        self._lock = threading.Lock()
        self._new_frame = threading.Event()
        self._running = False
        self._thread: threading.Thread | None = None
        self._tick = 0

    @property
    def size(self) -> tuple[int, int]:
        with self._lock:
            if self._frame is not None:
                h, w = self._frame.shape[:2]
                return w, h
        if self._cap is not None:
            return (int(self._cap.get(cv2.CAP_PROP_FRAME_WIDTH)) or 640,
                    int(self._cap.get(cv2.CAP_PROP_FRAME_HEIGHT)) or 480)
        return (self.req_width or 640, self.req_height or 480)

    def _open_device(self) -> bool:
        cap = cv2.VideoCapture(self.index, _BACKEND)
        if not cap.isOpened():
            cap.release()
            return False
        cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
        if self.req_width and self.req_height:
            cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*"MJPG"))
            cap.set(cv2.CAP_PROP_FRAME_WIDTH, self.req_width)
            cap.set(cv2.CAP_PROP_FRAME_HEIGHT, self.req_height)
        try:
            cap.set(cv2.CAP_PROP_AUTO_WB, 1)
            cap.set(cv2.CAP_PROP_BACKLIGHT, 2)
            if 0.0 <= cap.get(cv2.CAP_PROP_CONTRAST) < 8.0:
                cap.set(cv2.CAP_PROP_CONTRAST, 12.0)
        except Exception:
            pass
        ok, first = cap.read()
        if not ok or first is None:
            cap.release()
            return False
        try:
            self._cap = cap
            self._tune_sensor(first)
        except Exception:
            pass
        with self._lock:
            self._frame = first
        self.is_busy = False
        self._new_frame.set()
        return True

    def _make_busy_frame(self) -> np.ndarray:
        img = np.full((480, 640, 3), (20, 15, 13), dtype=np.uint8)
        cv2.putText(img, f"Kamera '{self.name}' sedang dipakai aplikasi lain",
                    (42, 200), cv2.FONT_HERSHEY_SIMPLEX, 0.62, (129, 140, 248), 2, cv2.LINE_AA)
        cv2.putText(img, "Di Streamlabs / OBS, ganti Device ke 'AlapCamera'",
                    (58, 245), cv2.FONT_HERSHEY_SIMPLEX, 0.60, (240, 240, 240), 1, cv2.LINE_AA)
        cv2.putText(img, "Kamera akan tersambung otomatis begitu dilepas...",
                    (72, 285), cv2.FONT_HERSHEY_SIMPLEX, 0.52, (156, 163, 175), 1, cv2.LINE_AA)
        return img

    def start(self) -> "CameraStream":
        if not self._open_device():
            self.is_busy = True
            with self._lock:
                self._frame = self._make_busy_frame()
            self._new_frame.set()
        self._running = True
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()
        return self

    def _tune_sensor(self, frame: np.ndarray) -> None:
        """Prevent hardware ISP black-crush on external USB webcams while keeping 30 FPS."""
        if self._cap is None or not self.auto_tune_sensor:
            return
        sample = frame[::8, ::8]
        p25, p75, p92 = np.percentile(sample, [25, 75, 92])

        # 1) Lift hardware black-level offset if shadows are crushed near 0
        if p25 <= 6.0:
            cur_bri = self._cap.get(cv2.CAP_PROP_BRIGHTNESS)
            if 0.0 <= cur_bri < 54.0:
                self._cap.set(cv2.CAP_PROP_BRIGHTNESS, min(56.0, cur_bri + 14.0))
            cur_con = self._cap.get(cv2.CAP_PROP_CONTRAST)
            if cur_con > 16.0 or (0.0 <= cur_con < 8.0):
                self._cap.set(cv2.CAP_PROP_CONTRAST, 12.0)

        # 2) Step exposure up at most to -5 (which keeps 30 FPS on USB webcams)
        if p25 < 18.0 and p92 < 185.0:
            cur_exp = self._cap.get(cv2.CAP_PROP_EXPOSURE)
            if -10.0 <= cur_exp <= -6.0:
                self._cap.set(cv2.CAP_PROP_EXPOSURE, -5.0)
        elif p75 > 248.0:
            cur_exp = self._cap.get(cv2.CAP_PROP_EXPOSURE)
            if -5.0 <= cur_exp <= -1.0:
                self._cap.set(cv2.CAP_PROP_EXPOSURE, cur_exp - 1.0)

    def _loop(self) -> None:
        while self._running:
            if self._cap is None:
                self._new_frame.set()
                time.sleep(1.2)
                if self._running:
                    self._open_device()
                continue
            ok, frame = self._cap.read()
            if not ok or frame is None:
                time.sleep(0.01)
                continue
            self._tick += 1
            if self._tick in (3, 10, 22) or self._tick % 60 == 0:
                try:
                    self._tune_sensor(frame)
                except Exception:
                    pass
            with self._lock:
                self._frame = frame
            self._new_frame.set()

    def wait_read(self, timeout: float = 0.05) -> np.ndarray | None:
        """Wait for the next fresh camera frame and return a copy."""
        if not self._new_frame.wait(timeout=timeout):
            return None
        self._new_frame.clear()
        with self._lock:
            return None if self._frame is None else self._frame.copy()

    def read(self) -> np.ndarray | None:
        with self._lock:
            return None if self._frame is None else self._frame.copy()

    def stop(self) -> None:
        self._running = False
        self._new_frame.set()
        if self._thread is not None:
            self._thread.join(timeout=1.0)
        if self._cap is not None:
            self._cap.release()
        self._cap = None
        self._frame = None
