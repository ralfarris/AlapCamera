"""Ultra-fast automatic visual enhancement ("auto visual").

Designed for zero FPS drop (< 1 ms per frame on the main video thread):

* **Async AI Face & Shadow Metering** - runs MediaPipe BlazeFace + midtone/shadow
  metering in a background worker thread so the video loop never blocks.
* **Fused 3-Channel Tone & Color LUT** - combines auto white balance, face-priority
  exposure, shadow toe-lift, and highlight roll-off into a single `1x256x3` lookup
  table applied via `cv2.LUT(frame, lut)` in ~0.4 ms.
"""
from __future__ import annotations

import threading

import cv2
import numpy as np

from .models import get_model


class _FaceDetector:
    """MediaPipe BlazeFace detector, falling back to an OpenCV Haar cascade."""

    def __init__(self) -> None:
        self._mp = None
        self._haar = None
        try:
            import mediapipe as mp
            from mediapipe.tasks.python import BaseOptions, vision

            opts = vision.FaceDetectorOptions(
                base_options=BaseOptions(model_asset_path=str(get_model("face"))),
                min_detection_confidence=0.45)
            self._mp = vision.FaceDetector.create_from_options(opts)
            self._mp_image = mp.Image
            self._mp_format = mp.ImageFormat.SRGB
        except Exception:
            path = getattr(cv2, "data", None)
            if path is not None:
                c = cv2.CascadeClassifier(path.haarcascades + "haarcascade_frontalface_default.xml")
                self._haar = None if c.empty() else c

    def detect(self, small_bgr: np.ndarray):
        if self._mp is not None:
            rgb = np.ascontiguousarray(cv2.cvtColor(small_bgr, cv2.COLOR_BGR2RGB))
            res = self._mp.detect(self._mp_image(image_format=self._mp_format, data=rgb))
            if res.detections:
                bb = max((d.bounding_box for d in res.detections), key=lambda b: b.width * b.height)
                return bb.origin_x, bb.origin_y, bb.width, bb.height
            return None
        if self._haar is not None:
            gray = cv2.cvtColor(small_bgr, cv2.COLOR_BGR2GRAY)
            faces = self._haar.detectMultiScale(gray, 1.15, 5, minSize=(40, 40))
            if len(faces):
                return tuple(max(faces, key=lambda f: f[2] * f[3]))
        return None

    def close(self) -> None:
        if self._mp is not None:
            self._mp.close()
            self._mp = None


_DETECT_GAMMA_LUT = np.clip(
    np.power(np.arange(256, dtype=np.float32) / 255.0, 0.38) * 255.0, 0, 255
).astype(np.uint8)


class AutoEnhancer:
    name = "enhance"

    def __init__(self, enabled: bool = True):
        self.enabled = enabled
        self.strength = 0.85
        self.target_brightness = 0.54
        self.auto_exposure = True
        self.white_balance = True
        self.local_contrast = True
        self.denoise = False
        self.sharpen = 0.0
        self.face_priority = True

        self._gamma = 1.0
        self._shadow_lift = 0.0
        self._wb_gains = np.ones(3, np.float32)
        self._lut = np.arange(256, dtype=np.uint8).reshape(1, 256, 1).repeat(3, axis=2)

        self._face: _FaceDetector | None = None
        self._face_box: tuple[int, int, int, int] | None = None
        self._frame_no = 0
        self.last_brightness = 0.5

        self._cond = threading.Condition()
        self._pending: np.ndarray | None = None
        self._running = True
        self._worker = threading.Thread(target=self._meter_worker, daemon=True)
        self._worker.start()

    @property
    def face_box(self):
        return self._face_box

    def close(self) -> None:
        with self._cond:
            self._running = False
            self._cond.notify_all()
        if self._worker.is_alive():
            self._worker.join(timeout=1.5)
        if self._face is not None:
            self._face.close()
            self._face = None

    def _meter_worker(self) -> None:
        while True:
            with self._cond:
                while self._pending is None and self._running:
                    self._cond.wait()
                if not self._running:
                    return
                frame, self._pending = self._pending, None
            try:
                if self._face is None:
                    self._face = _FaceDetector()
                self._update_lut(frame)
            except Exception as exc:
                print("enhance worker error:", exc)

    def _update_lut(self, frame: np.ndarray) -> None:
        s = float(np.clip(self.strength, 0.0, 1.0))
        h, w = frame.shape[:2]
        scale = 240.0 / w
        small = cv2.resize(frame, (240, max(1, int(h * scale))), interpolation=cv2.INTER_AREA)

        if self.face_priority and self._face is not None:
            boosted = cv2.LUT(small, _DETECT_GAMMA_LUT)
            box = self._face.detect(boosted) or self._face.detect(small)
            if box is not None:
                bx, by, bw, bh = box
                self._face_box = (int(bx / scale), int(by / scale), int(bw / scale), int(bh / scale))
            else:
                self._face_box = None

        # White balance gains from midtones
        if self.white_balance:
            sub = small.reshape(-1, 3).astype(np.float32)
            luma = sub.mean(axis=1)
            mid = sub[(luma > 14.0) & (luma < 225.0)]
            if len(mid) < 32:
                mid = sub
            means = mid.mean(axis=0) + 1e-3
            gains = np.clip(means.mean() / means, 0.78, 1.38)
            self._wb_gains = 0.85 * self._wb_gains + 0.15 * gains
        else:
            self._wb_gains = np.ones(3, np.float32)

        # Subject & shadow metering
        gray = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY)
        sh, sw = gray.shape
        p25, p50 = np.percentile(gray, [25, 50]) / 255.0
        if self.face_priority and self._face_box is not None:
            fx, fy, fw, fh = [int(v * scale) for v in self._face_box]
            px, py = int(fw * 0.15), int(fh * 0.15)
            x0, y0 = max(0, fx + px), max(0, fy + py)
            x1, y1 = min(sw, fx + fw - px), min(sh, fy + fh - py)
            roi = gray[y0:y1, x0:x1]
            face_b = float(np.percentile(roi, 55)) / 255.0 if roi.size else float(p50)
            subj = 0.85 * face_b + 0.15 * float(p50)
        else:
            portrait = gray[sh // 5: 9 * sh // 10, sw // 4: 3 * sw // 4]
            port_p35 = float(np.percentile(portrait, 35)) / 255.0 if portrait.size else float(p25)
            subj = 0.65 * port_p35 + 0.35 * float(p25)

        self.last_brightness = subj
        target = float(np.clip(self.target_brightness, 0.25, 0.80))
        darkness = float(np.clip((target - subj) / max(target, 0.2), 0.0, 1.0))

        if self.auto_exposure:
            b = float(np.clip(subj, 0.04, 0.95))
            wanted_gamma = float(np.clip(np.log(target) / np.log(b), 0.42, 1.45))
            self._gamma = 0.80 * self._gamma + 0.20 * wanted_gamma
            self._shadow_lift = 0.80 * self._shadow_lift + 0.20 * darkness
        else:
            self._gamma = 1.0
            self._shadow_lift = 0.0

        # Build fused 1x256x3 LUT (White Balance + Shadow Toe Lift + Highlight Roll-off + Contrast S-curve)
        gamma = 1.0 + (self._gamma - 1.0) * s
        lift = self._shadow_lift * s
        hl_protect = max(0.0, (1.0 - gamma) * 0.85 + lift * 0.45)
        wb = 1.0 + (self._wb_gains - 1.0) * s

        x = np.linspace(0.0, 1.0, 256, dtype=np.float32).reshape(256, 1)
        x_wb = np.clip(x * wb.reshape(1, 3), 0.0, 1.0)
        y = np.power(x_wb, gamma)
        if lift > 0.01:
            toe = lift * 0.50 * x_wb * np.power(1.0 - x_wb, 1.8)
            y = np.clip(y + toe, 0.0, 1.0)
        if hl_protect > 0.01:
            knee = 0.72
            mask = y > knee
            over = (y[mask] - knee) / (1.0 - knee)
            y[mask] = knee + (1.0 - knee) * (over / (1.0 + hl_protect * 0.6 * over))
        if self.local_contrast:
            # Subtle S-curve contrast around midpoint
            c = 0.12 * s
            y = np.clip(y + c * y * (1.0 - y) * (y - 0.35), 0.0, 1.0)

        self._lut = np.clip(np.round(y * 255.0), 0, 255).astype(np.uint8).reshape(1, 256, 3)

    # ---------------------------------------------------------------- process
    def process(self, frame: np.ndarray) -> np.ndarray:
        if not self.enabled or self.strength <= 0.0:
            return frame
        self._frame_no += 1
        if self._frame_no == 1 or self._frame_no % 6 == 0:
            with self._cond:
                self._pending = frame
                self._cond.notify()
        return cv2.LUT(frame, self._lut)
