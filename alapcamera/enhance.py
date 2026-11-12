"""Automatic visual enhancement ("auto visual").

Works fully in real-time on CPU, no dataset / training needed:

* **Face-priority shadow & exposure recovery** - detects the face (even in
  strong backlight / dark silhouettes via gamma-boosted detection) and meters
  on the face + foreground shadows. Applies a smooth shadow-highlight tone
  curve that lifts dark faces/shadows without blowing out a bright background
  wall.
* **De-banded low-light boost** - pre-smooths sensor quantization steps before
  tone mapping so dark webcams never look posterized or cartoonish.
* **Midtone auto white balance** - removes green/muddy colour casts from budget
  webcams while ignoring clipped blacks and blown highlights.
* **Denoise & sharpen** - adaptive edge-preserving smoothing and unsharp mask.
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
                min_detection_confidence=0.45)
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


_DETECT_GAMMA_LUT = np.clip(
    np.power(np.arange(256, dtype=np.float32) / 255.0, 0.38) * 255.0, 0, 255
).astype(np.uint8)


class AutoEnhancer:
    name = "enhance"

    def __init__(self, enabled: bool = True):
        self.enabled = enabled
        # user controls (0..1)
        self.strength = 0.8
        self.target_brightness = 0.52
        self.auto_exposure = True
        self.white_balance = True
        self.local_contrast = True
        self.denoise = True
        self.sharpen = 0.25
        self.face_priority = True

        self._gamma = 1.0
        self._shadow_lift = 0.0
        self._wb_gains = np.ones(3, np.float32)
        self._clahe = cv2.createCLAHE(clipLimit=1.5, tileGridSize=(8, 8))
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
        # Gamma-lift shadows strongly so faces silhouetted against a bright wall
        # are still clearly visible to BlazeFace
        boosted = cv2.LUT(small, _DETECT_GAMMA_LUT)
        box = self._face.detect(boosted)
        if box is None:
            box = self._face.detect(small)
        if box is None:
            self._face_box = None
        else:
            x, y, fw, fh = box
            self._face_box = (int(x / scale), int(y / scale), int(fw / scale), int(fh / scale))

    def _meter(self, l_chan: np.ndarray) -> tuple[float, float]:
        """Return (subject_brightness, shadow_darkness) in 0..1."""
        ih, iw = l_chan.shape
        sample = l_chan[::4, ::4]
        p25, p50 = np.percentile(sample, [25, 50]) / 255.0

        if self.face_priority and self._face_box is not None:
            x, y, w, h = self._face_box
            # Focus on the inner 70% of the face box (skin, not background wall)
            pad_x, pad_y = int(w * 0.15), int(h * 0.15)
            x0, y0 = max(0, x + pad_x), max(0, y + pad_y)
            x1, y1 = min(iw, x + w - pad_x), min(ih, y + h - pad_y)
            roi = l_chan[y0:y1, x0:x1]
            if roi.size:
                face_b = float(np.percentile(roi, 55)) / 255.0
                subj = 0.85 * face_b + 0.15 * float(p50)
                shadow_gap = max(0.0, self.target_brightness - face_b)
                return subj, float(np.clip(shadow_gap / max(self.target_brightness, 0.2), 0.0, 1.0))

        # No face box: meter on lower-center portrait zone + 30th percentile
        # so a bright background wall never hides a dark person in the foreground
        portrait = l_chan[ih // 5: 9 * ih // 10, iw // 4: 3 * iw // 4]
        port_p35 = float(np.percentile(portrait[::2, ::2], 35)) / 255.0
        subj = 0.65 * port_p35 + 0.35 * float(p25)
        shadow_gap = max(0.0, self.target_brightness - subj)
        return subj, float(np.clip(shadow_gap / max(self.target_brightness, 0.2), 0.0, 1.0))

    @staticmethod
    def _tone_curve_lut(gamma: float, shadow_lift: float, highlight_protect: float) -> np.ndarray:
        """Smooth shadow-lifting & highlight-preserving curve (avoids wall blow-out)."""
        x = np.linspace(0.0, 1.0, 256, dtype=np.float32)
        # 1) Smooth gamma curve
        y = np.power(x, gamma)
        # 2) Dedicated shadow toe lift: boosts darks (x < 0.5) without touching highlights
        if shadow_lift > 0.01:
            toe = shadow_lift * 0.55 * x * np.power(1.0 - x, 1.8)
            y = np.clip(y + toe, 0.0, 1.0)
        # 3) Soft highlight roll-off so bright walls keep texture
        if highlight_protect > 0.01:
            knee = 0.72
            mask = y > knee
            over = (y[mask] - knee) / (1.0 - knee)
            compressed = knee + (1.0 - knee) * (over / (1.0 + highlight_protect * 0.6 * over))
            y[mask] = compressed
        return np.clip(np.round(y * 255.0), 0, 255).astype(np.uint8)

    # ---------------------------------------------------------------- process
    def process(self, frame: np.ndarray) -> np.ndarray:
        s = float(np.clip(self.strength, 0.0, 1.0))
        if s <= 0.0:
            return frame
        self._frame_no += 1
        out = frame

        if self.face_priority and self._frame_no % 5 == 1:
            self._detect_face(frame)

        # 1) Midtone auto white balance (ignores clipped blacks <12 & bright walls >225)
        if self.white_balance:
            sub = out[::8, ::8].reshape(-1, 3).astype(np.float32)
            luma = sub.mean(axis=1)
            mid = sub[(luma > 12.0) & (luma < 225.0)]
            if len(mid) < 64:
                mid = sub
            means = mid.mean(axis=0) + 1e-3
            gains = means.mean() / means
            gains = np.clip(gains, 0.72, 1.45)
            self._wb_gains = 0.88 * self._wb_gains + 0.12 * gains
            g = (1.0 + (self._wb_gains - 1.0) * s).astype(np.float32)
            x = np.arange(256, dtype=np.float32).reshape(256, 1)
            lut = np.clip(x * g.reshape(1, 3), 0, 255).astype(np.uint8).reshape(1, 256, 3)
            out = cv2.LUT(out, lut)

        ycrcb = cv2.cvtColor(out, cv2.COLOR_BGR2YCrCb)
        y_chan, cr_chan, cb_chan = cv2.split(ycrcb)

        brightness, darkness = self._meter(y_chan)
        self.last_brightness = brightness

        # Pre-smooth 8-bit quantization steps in dark shadows BEFORE lifting
        # so shadow recovery produces smooth skin tones instead of banding
        if self.denoise and darkness * s > 0.05:
            sigma_c = int(12 + 22 * darkness * s)
            y_chan = cv2.bilateralFilter(y_chan, 5, sigma_c, 7)

        # 2) Face/shadow-priority exposure + shadow recovery curve
        if self.auto_exposure:
            b = float(np.clip(brightness, 0.03, 0.95))
            target = float(np.clip(self.target_brightness, 0.25, 0.80))
            wanted_gamma = float(np.clip(np.log(target) / np.log(b), 0.38, 1.55))
            self._gamma = 0.82 * self._gamma + 0.18 * wanted_gamma
            self._shadow_lift = 0.82 * self._shadow_lift + 0.18 * darkness

            gamma = 1.0 + (self._gamma - 1.0) * s
            lift = self._shadow_lift * s
            hl_protect = max(0.0, (1.0 - gamma) * 0.9 + lift * 0.5)
            if abs(gamma - 1.0) > 0.01 or lift > 0.02:
                y_chan = cv2.LUT(y_chan, self._tone_curve_lut(gamma, lift, hl_protect))

        # 3) Gentle local contrast (kept subtle so it never creates dark halos)
        if self.local_contrast:
            self._clahe.setClipLimit(1.1 + 0.9 * s)
            y_eq = self._clahe.apply(y_chan)
            blend = 0.22 * s * (0.5 + 0.5 * darkness)
            y_chan = cv2.addWeighted(y_eq, blend, y_chan, 1.0 - blend, 0)

        # Preserve natural skin saturation when lifting dark shadows
        if darkness * s > 0.05:
            sat = 1.0 + 0.18 * darkness * s
            chroma_lut = np.clip((np.arange(256, dtype=np.float32) - 128.0) * sat + 128.0,
                                 0, 255).astype(np.uint8)
            cr_chan = cv2.LUT(cr_chan, chroma_lut)
            cb_chan = cv2.LUT(cb_chan, chroma_lut)

        out = cv2.cvtColor(cv2.merge((y_chan, cr_chan, cb_chan)), cv2.COLOR_YCrCb2BGR)

        # 4) Sharpen (unsharp mask)
        if self.sharpen > 0.01:
            blur = cv2.GaussianBlur(out, (0, 0), 1.8)
            amt = float(self.sharpen) * 0.65
            out = cv2.addWeighted(out, 1.0 + amt, blur, -amt, 0)

        return out
