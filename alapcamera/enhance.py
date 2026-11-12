"""Automatic visual enhancement ("auto visual").

Works fully in real-time on CPU, no dataset / training needed:

* **Face-priority auto exposure** - detects the face (pre-trained MediaPipe
  BlazeFace model, Haar cascade fallback) and meters brightness on the face, then
  applies an adaptive gamma curve so a person in a dark room is lifted to a
  pleasant level. The gain is smoothed over time to avoid flicker.
* **Low-light boost / local contrast** - CLAHE on the luminance channel.
* **Auto white balance** - gray-world colour correction.
* **Denoise** - edge-preserving bilateral filter, strength scales with how
  dark the scene is (dark cameras are noisy).
* **Sharpen** - unsharp mask.
"""
from __future__ import annotations

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
                min_detection_confidence=0.5)
            self._mp = vision.FaceDetector.create_from_options(opts)
            self._mp_image = mp.Image
            self._mp_format = mp.ImageFormat.SRGB
        except Exception as exc:  # pragma: no cover - fallback path
            print("MediaPipe face detector unavailable, using Haar cascade:", exc)
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


class AutoEnhancer:
    name = "enhance"

    def __init__(self, enabled: bool = True):
        self.enabled = enabled
        # user controls (0..1)
        self.strength = 0.8
        self.target_brightness = 0.50
        self.auto_exposure = True
        self.white_balance = True
        self.local_contrast = True
        self.denoise = True
        self.sharpen = 0.3
        self.face_priority = True

        self._gamma = 1.0
        self._wb_gains = np.ones(3, np.float32)
        self._clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
        self._face = _FaceDetector()
        self._face_box: tuple[int, int, int, int] | None = None
        self._frame_no = 0
        self.last_brightness = 0.0

    @property
    def face_box(self):
        return self._face_box

    def close(self) -> None:
        self._face.close()

    # ---------------------------------------------------------------- helpers
    def _detect_face(self, frame_bgr: np.ndarray) -> None:
        h, w = frame_bgr.shape[:2]
        scale = 320.0 / w
        small = cv2.resize(frame_bgr, (320, int(h * scale)))
        # equalize so the detector still finds faces in a dark room
        ycc = cv2.cvtColor(small, cv2.COLOR_BGR2YCrCb)
        ycc[..., 0] = cv2.equalizeHist(ycc[..., 0])
        small = cv2.cvtColor(ycc, cv2.COLOR_YCrCb2BGR)
        box = self._face.detect(small)
        if box is None:
            self._face_box = None
        else:
            x, y, fw, fh = box
            self._face_box = (int(x / scale), int(y / scale), int(fw / scale), int(fh / scale))

    def _meter(self, l_chan: np.ndarray) -> float:
        if self.face_priority and self._face_box is not None:
            x, y, w, h = self._face_box
            roi = l_chan[y:y + h, x:x + w]
            if roi.size:
                face = float(roi.mean()) / 255.0
                scene = float(l_chan.mean()) / 255.0
                return 0.7 * face + 0.3 * scene
        # center-weighted metering
        h, w = l_chan.shape
        center = l_chan[h // 4: 3 * h // 4, w // 4: 3 * w // 4]
        return 0.6 * float(center.mean()) / 255.0 + 0.4 * float(l_chan.mean()) / 255.0

    @staticmethod
    def _gamma_lut(gamma: float) -> np.ndarray:
        x = np.arange(256, dtype=np.float32) / 255.0
        return np.clip(np.power(x, gamma) * 255.0, 0, 255).astype(np.uint8)

    # ---------------------------------------------------------------- process
    def process(self, frame: np.ndarray) -> np.ndarray:
        s = float(np.clip(self.strength, 0.0, 1.0))
        if s <= 0.0:
            return frame
        self._frame_no += 1
        out = frame

        # 1) white balance (gray world, smoothed)
        if self.white_balance:
            means = out.reshape(-1, 3)[::16].mean(axis=0).astype(np.float32) + 1e-3
            gains = means.mean() / means
            gains = np.clip(gains, 0.6, 1.6)
            self._wb_gains = 0.9 * self._wb_gains + 0.1 * gains
            g = (1.0 + (self._wb_gains - 1.0) * s).astype(np.float32)
            x = np.arange(256, dtype=np.float32).reshape(256, 1)
            lut = np.clip(x * g.reshape(1, 3), 0, 255).astype(np.uint8).reshape(1, 256, 3)
            out = cv2.LUT(out, lut)

        lab = cv2.cvtColor(out, cv2.COLOR_BGR2LAB)
        l_chan, a_chan, b_chan = cv2.split(lab)

        if self.face_priority and self._frame_no % 6 == 1:
            self._detect_face(frame)

        brightness = self._meter(l_chan)
        self.last_brightness = brightness
        darkness = float(np.clip((self.target_brightness - brightness) / self.target_brightness, 0, 1))

        # 2) auto exposure via adaptive gamma (temporal smoothing)
        if self.auto_exposure:
            b = float(np.clip(brightness, 0.02, 0.98))
            target = float(np.clip(self.target_brightness, 0.2, 0.8))
            wanted = float(np.clip(np.log(target) / np.log(b), 0.35, 1.8))
            self._gamma = 0.85 * self._gamma + 0.15 * wanted
            gamma = 1.0 + (self._gamma - 1.0) * s
            if abs(gamma - 1.0) > 0.01:
                l_chan = cv2.LUT(l_chan, self._gamma_lut(gamma))

        # 3) local contrast (stronger when dark)
        if self.local_contrast:
            self._clahe.setClipLimit(1.2 + 2.0 * s * (0.4 + darkness))
            l_eq = self._clahe.apply(l_chan)
            l_chan = cv2.addWeighted(l_eq, 0.5 + 0.5 * s, l_chan, 0.5 - 0.5 * s, 0)

        out = cv2.cvtColor(cv2.merge((l_chan, a_chan, b_chan)), cv2.COLOR_LAB2BGR)

        # 4) denoise (only meaningful when dark / boosted)
        if self.denoise and darkness * s > 0.05:
            sigma = 20 + 60 * darkness * s
            out = cv2.bilateralFilter(out, 5, sigma, sigma)

        # 5) sharpen
        if self.sharpen > 0.01:
            blur = cv2.GaussianBlur(out, (0, 0), 2.0)
            amt = float(self.sharpen) * 0.8
            out = cv2.addWeighted(out, 1 + amt, blur, -amt, 0)

        return out
