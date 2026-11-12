"""AI background removal / blur / replacement.

Uses Google's pre-trained MediaPipe selfie segmentation models (no dataset or
training needed):

* ``fast``    - ``selfie_segmenter`` (256x256, very fast)
* ``quality`` - ``selfie_multiclass_256x256`` (better hair / edges, slower)

The raw mask is refined with temporal smoothing, an edge-aware guided filter
(when opencv-contrib is available) and feathering for natural edges.
"""
from __future__ import annotations

import threading
import time
from pathlib import Path

import cv2
import numpy as np

from .models import get_model

MODES = ("blur", "remove", "image")


class BackgroundProcessor:
    name = "background"

    def __init__(self, enabled: bool = False):
        self.enabled = enabled
        self.mode = "blur"            # blur | remove | image
        self.quality = "fast"         # fast | quality
        self.blur_strength = 0.6      # 0..1
        self.color = (0, 255, 0)      # BGR, used by "remove" (green = easy chroma key)
        self.threshold = 0.5          # mask cut-off
        self.edge_softness = 0.5      # 0..1
        self.smoothing = 0.5          # temporal smoothing 0..1

        self._segmenter = None
        self._loaded_quality = None
        self._mp = None
        self._prev_mask: np.ndarray | None = None
        self._bg_image: np.ndarray | None = None
        self._bg_cache: np.ndarray | None = None
        self._t0 = time.monotonic()
        self._last_ts = -1
        self._has_guided = hasattr(cv2, "ximgproc")
        self.last_error = ""
        # async segmentation worker
        self._cond = threading.Condition()
        self._pending: np.ndarray | None = None
        self._mask_small: np.ndarray | None = None
        self._mask_ready = False
        self._running = False
        self._thread: threading.Thread | None = None

    # ---------------------------------------------------------------- models
    def _ensure_segmenter(self) -> None:
        if self._segmenter is not None and self._loaded_quality == self.quality:
            return
        self._close_segmenter()
        import mediapipe as mp
        from mediapipe.tasks.python import BaseOptions, vision

        key = "multiclass" if self.quality == "quality" else "selfie"
        opts = vision.ImageSegmenterOptions(
            base_options=BaseOptions(model_asset_path=str(get_model(key))),
            running_mode=vision.RunningMode.VIDEO,
            output_confidence_masks=True,
            output_category_mask=False,
        )
        self._segmenter = vision.ImageSegmenter.create_from_options(opts)
        self._mp = mp
        self._loaded_quality = self.quality
        self._prev_mask = None

    def set_background_image(self, path: str | Path | None) -> bool:
        if not path:
            self._bg_image = None
            self._bg_cache = None
            return True
        img = cv2.imdecode(np.fromfile(str(path), dtype=np.uint8), cv2.IMREAD_COLOR)
        if img is None:
            return False
        self._bg_image = img
        self._bg_cache = None
        return True

    def _close_segmenter(self) -> None:
        if self._segmenter is not None:
            self._segmenter.close()
        self._segmenter = None

    def close(self) -> None:
        with self._cond:
            self._running = False
            self._cond.notify_all()
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None
        self._close_segmenter()
        self._mask_small = None
        self._mask_ready = False

    # ---------------------------------------------------------------- mask
    def _segment_small(self, small: np.ndarray) -> np.ndarray:
        """Run the AI model on a small frame and return a raw person mask."""
        rgb = np.ascontiguousarray(cv2.cvtColor(small, cv2.COLOR_BGR2RGB))
        image = self._mp.Image(image_format=self._mp.ImageFormat.SRGB, data=rgb)
        ts = int((time.monotonic() - self._t0) * 1000)
        if ts <= self._last_ts:
            ts = self._last_ts + 1
        self._last_ts = ts
        result = self._segmenter.segment_for_video(image, ts)
        masks = result.confidence_masks
        if self._loaded_quality == "quality" and len(masks) > 1:
            mask = 1.0 - masks[0].numpy_view()          # 1 - background
        else:
            mask = masks[0].numpy_view()                # person confidence
        mask = np.squeeze(np.asarray(mask, dtype=np.float32))

        # temporal smoothing reduces flicker
        if self._prev_mask is not None and self._prev_mask.shape == mask.shape:
            a = float(np.clip(self.smoothing, 0, 0.9))
            mask = a * self._prev_mask + (1 - a) * mask
        self._prev_mask = mask
        return mask

    def _worker_loop(self) -> None:
        while True:
            with self._cond:
                while self._pending is None and self._running:
                    self._cond.wait()
                if not self._running:
                    return
                small, self._pending = self._pending, None
            try:
                self._ensure_segmenter()
                mask = self._segment_small(small)
                self.last_error = ""
            except Exception as exc:
                self.last_error = str(exc)
                print("background error:", exc)
                mask = None
            with self._cond:
                self._mask_small = mask
                self._mask_ready = True
                self._cond.notify_all()

    def _submit(self, small: np.ndarray) -> np.ndarray | None:
        with self._cond:
            if self._thread is None:
                self._running = True
                self._thread = threading.Thread(target=self._worker_loop, daemon=True)
                self._thread.start()
            self._pending = small
            self._cond.notify_all()
            if self._mask_small is None:   # first frame: wait for a mask
                self._cond.wait_for(lambda: self._mask_ready, timeout=3.0)
            return self._mask_small

    def _refine(self, mask: np.ndarray, small: np.ndarray, w: int, h: int) -> np.ndarray:
        if mask.shape[:2] != small.shape[:2]:
            mask = cv2.resize(mask, (small.shape[1], small.shape[0]))
        # contrast the mask around the threshold
        t = float(np.clip(self.threshold, 0.05, 0.95))
        soft = 0.04 + 0.25 * float(self.edge_softness)
        mask = np.clip((mask - (t - soft)) / (2 * soft), 0, 1).astype(np.float32)
        # edge-aware refinement guided by the *current* frame
        if self._has_guided:
            guide = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY)
            mask = cv2.ximgproc.guidedFilter(guide, mask, 4, 1e-3)
        mask = cv2.resize(mask, (w, h), interpolation=cv2.INTER_LINEAR)
        k = 1 + 2 * int(2 + 8 * self.edge_softness)
        mask = cv2.GaussianBlur(mask, (k, k), 0)
        return np.clip(mask, 0, 1)

    # ---------------------------------------------------------------- bg
    def _background(self, frame: np.ndarray) -> np.ndarray:
        h, w = frame.shape[:2]
        if self.mode == "blur":
            # blur on a downscaled copy: much faster, same look
            small = cv2.resize(frame, (w // 4, h // 4), interpolation=cv2.INTER_AREA)
            sigma = 1 + 12 * float(self.blur_strength)
            small = cv2.GaussianBlur(small, (0, 0), sigma)
            return cv2.resize(small, (w, h), interpolation=cv2.INTER_LINEAR)
        if self.mode == "image" and self._bg_image is not None:
            if self._bg_cache is None or self._bg_cache.shape[:2] != (h, w):
                self._bg_cache = _cover(self._bg_image, w, h)
            return self._bg_cache
        return np.full_like(frame, self.color, dtype=np.uint8)

    def process(self, frame: np.ndarray) -> np.ndarray:
        h, w = frame.shape[:2]
        sw = 512
        small = cv2.resize(frame, (sw, int(h * sw / w)), interpolation=cv2.INTER_AREA)
        raw = self._submit(small)
        if raw is None:
            return frame
        mask = self._refine(raw, small, w, h)
        bg = self._background(frame)
        mask = np.ascontiguousarray(mask, dtype=np.float32)
        return cv2.blendLinear(frame, bg, mask, 1.0 - mask)


def _cover(img: np.ndarray, w: int, h: int) -> np.ndarray:
    """Resize + center-crop an image so it fully covers (w, h)."""
    ih, iw = img.shape[:2]
    scale = max(w / iw, h / ih)
    nw, nh = int(round(iw * scale)), int(round(ih * scale))
    r = cv2.resize(img, (nw, nh), interpolation=cv2.INTER_AREA)
    x, y = (nw - w) // 2, (nh - h) // 2
    return r[y:y + h, x:x + w].copy()
