"""Physical camera discovery and capture."""
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
    width: int
    height: int

    def __str__(self) -> str:
        return f"{self.name} ({self.width}x{self.height})"


def device_names() -> list[str]:
    """DirectShow device names in index order (Windows, needs pygrabber)."""
    if platform.system() != "Windows":
        return []
    try:
        from pygrabber.dshow_graph import FilterGraph
        return list(FilterGraph().get_input_devices())
    except Exception:
        return []


# our own output device must never be used as an input (feedback loop)
OWN_DEVICE_NAMES = ("AlapCamera",)

# phone-bridge / software cameras: still selectable, but not picked by default
_SOFTWARE_HINTS = ("virtual", "camo", "droidcam", "iriun", "obs", "streamlabs",
                   "bytecast", "unity", "xsplit", "snap camera", "manycam", "lsvcam")


def is_software_camera(name: str) -> bool:
    n = name.lower()
    return any(h in n for h in _SOFTWARE_HINTS)


def default_camera(cams: list["CameraInfo"]) -> int:
    """Index (into ``cams``) of the best default: first real hardware camera."""
    for i, c in enumerate(cams):
        if not is_software_camera(c.name):
            return i
    return 0 if cams else -1


def list_cameras(max_index: int = 10) -> list[CameraInfo]:
    """Probe camera indices and return the ones that deliver frames."""
    names = device_names()
    count = len(names) if names else max_index
    found: list[CameraInfo] = []
    for idx in range(count):
        name = names[idx] if idx < len(names) else f"Camera {idx}"
        if any(own.lower() in name.lower() for own in OWN_DEVICE_NAMES):
            continue
        cap = cv2.VideoCapture(idx, _BACKEND)
        if not cap.isOpened():
            cap.release()
            continue
        ok, frame = cap.read()
        if ok and frame is not None:
            h, w = frame.shape[:2]
            found.append(CameraInfo(idx, name, w, h))
        cap.release()
    return found


class CameraStream:
    """Grabs frames from a physical camera in a background thread.

    Includes automatic DirectShow sensor calibration so external USB webcams
    whose factory black-level / exposure crushes shadows to 0 are automatically
    recovered at the hardware ISP level.
    """

    def __init__(self, index: int, width: int = 1280, height: int = 720, fps: int = 30):
        self.index = index
        self.req_width = width
        self.req_height = height
        self.req_fps = fps
        self.auto_tune_sensor = True
        self._cap: cv2.VideoCapture | None = None
        self._frame: np.ndarray | None = None
        self._lock = threading.Lock()
        self._running = False
        self._thread: threading.Thread | None = None
        self._tick = 0

    @property
    def size(self) -> tuple[int, int]:
        if self._cap is None:
            return self.req_width, self.req_height
        return (int(self._cap.get(cv2.CAP_PROP_FRAME_WIDTH)),
                int(self._cap.get(cv2.CAP_PROP_FRAME_HEIGHT)))

    def start(self) -> "CameraStream":
        cap = cv2.VideoCapture(self.index, _BACKEND)
        if not cap.isOpened():
            raise RuntimeError(f"Cannot open camera {self.index}")
        cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*"MJPG"))
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, self.req_width)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, self.req_height)
        cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
        # Enable hardware auto white balance & backlight compensation when supported
        try:
            cap.set(cv2.CAP_PROP_AUTO_WB, 1)
            cap.set(cv2.CAP_PROP_BACKLIGHT, 2)
        except Exception:
            pass
        self._cap = cap
        self._running = True
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()
        return self

    def _tune_sensor(self, frame: np.ndarray) -> None:
        """Prevent hardware ISP black-crush & underexposure on external USB webcams."""
        if self._cap is None or not self.auto_tune_sensor:
            return
        sample = frame[::8, ::8]
        p10, p25, p50, p75, p92 = np.percentile(sample, [10, 25, 50, 75, 92])

        # 1) If >25% of pixels are crushed near 0, lift hardware brightness offset
        if p25 <= 4.0:
            cur_bri = self._cap.get(cv2.CAP_PROP_BRIGHTNESS)
            if 0.0 <= cur_bri < 52.0:
                self._cap.set(cv2.CAP_PROP_BRIGHTNESS, min(54.0, cur_bri + 12.0))
            cur_con = self._cap.get(cv2.CAP_PROP_CONTRAST)
            if cur_con > 16.0:
                self._cap.set(cv2.CAP_PROP_CONTRAST, 12.0)

        # 2) If dark shadows are still very low and highlights have headroom, step up exposure
        if p25 < 15.0 and p92 < 175.0:
            cur_exp = self._cap.get(cv2.CAP_PROP_EXPOSURE)
            if -10.0 <= cur_exp <= -6.0:
                self._cap.set(cv2.CAP_PROP_EXPOSURE, cur_exp + 1.0)
        # 3) If sensor is blowing out >25% of the frame, step down exposure or brightness
        elif p75 > 248.0:
            cur_exp = self._cap.get(cv2.CAP_PROP_EXPOSURE)
            if -6.0 < cur_exp <= -1.0:
                self._cap.set(cv2.CAP_PROP_EXPOSURE, cur_exp - 1.0)
            else:
                cur_bri = self._cap.get(cv2.CAP_PROP_BRIGHTNESS)
                if cur_bri > 36.0:
                    self._cap.set(cv2.CAP_PROP_BRIGHTNESS, cur_bri - 6.0)

    def _loop(self) -> None:
        while self._running and self._cap is not None:
            ok, frame = self._cap.read()
            if not ok:
                time.sleep(0.01)
                continue
            self._tick += 1
            if self._tick in (3, 12, 25) or self._tick % 45 == 0:
                try:
                    self._tune_sensor(frame)
                except Exception:
                    pass
            with self._lock:
                self._frame = frame

    def read(self) -> np.ndarray | None:
        with self._lock:
            return None if self._frame is None else self._frame.copy()

    def stop(self) -> None:
        self._running = False
        if self._thread is not None:
            self._thread.join(timeout=1.0)
        if self._cap is not None:
            self._cap.release()
        self._cap = None
        self._frame = None
