"""Ultra-fast AI background removal (PNG cutout / virtual green screen / blur).

All AI segmentation and edge refinement run asynchronously in a background
worker thread so the main camera loop stays at full native FPS (~1 ms blend).
"""
from __future__ import annotations

import threading
import time
from pathlib import Path

import cv2
import numpy as np

from .models import get_model


class BackgroundProcessor:
    name = "background"

    def __init__(self, enabled: bool = False):
        self.enabled = enabled
        # "cutout" (transparent PNG look in preview, pure black/chroma output),
        # "greenscreen" (virtual green screen #00FF00), or "blur"
        self.mode = "cutout"
        self.quality = "fast"
        self.blur_strength = 0.6
        self.color = (0, 0, 0)
        self.threshold = 0.48
        self.edge_softness = 0.45
        self.smoothing = 0.45
        self.image_path: str = ""

        self._segmenter = None
        self._loaded_quality = None
        self._mp = None
        self._prev_mask: np.ndarray | None = None
        self._bg_image: np.ndarray | None = None
        self._bg_cache: np.ndarray | None = None
        self._checker_cache: np.ndarray | None = None
        self._t0 = time.monotonic()
        self._last_ts = -1
        self._has_guided = hasattr(cv2, "ximgproc")
        self.last_error = ""

        self._cond = threading.Condition()
        self._pending: np.ndarray | None = None
        self._refined_small: np.ndarray | None = None
        self._running = False
        self._thread: threading.Thread | None = None

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
            self.image_path = ""
            return True
        try:
            img = cv2.imdecode(np.fromfile(str(path), dtype=np.uint8), cv2.IMREAD_COLOR)
        except Exception:
            return False
        if img is None:
            return False
        self._bg_image = img
        self._bg_cache = None
        self.image_path = str(path)
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
            self._thread.join(timeout=1.5)
            self._thread = None
        self._close_segmenter()
        self._refined_small = None

    def _segment_and_refine_small(self, small: np.ndarray) -> np.ndarray:
        rgb = np.ascontiguousarray(cv2.cvtColor(small, cv2.COLOR_BGR2RGB))
        image = self._mp.Image(image_format=self._mp.ImageFormat.SRGB, data=rgb)
        ts = int((time.monotonic() - self._t0) * 1000)
        if ts <= self._last_ts:
            ts = self._last_ts + 1
        self._last_ts = ts
        result = self._segmenter.segment_for_video(image, ts)
        masks = result.confidence_masks
        if self._loaded_quality == "quality" and len(masks) > 1:
            mask = 1.0 - masks[0].numpy_view()
        else:
            mask = masks[0].numpy_view()
        mask = np.squeeze(np.asarray(mask, dtype=np.float32))

        if self._prev_mask is not None and self._prev_mask.shape == mask.shape:
            a = float(np.clip(self.smoothing, 0.0, 0.85))
            mask = a * self._prev_mask + (1.0 - a) * mask
        self._prev_mask = mask

        t = float(np.clip(self.threshold, 0.1, 0.9))
        soft = 0.04 + 0.12 * float(self.edge_softness)
        mask = np.clip((mask - (t - soft)) / (2.0 * soft), 0.0, 1.0).astype(np.float32)

        if self._has_guided:
            guide = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY)
            if mask.shape[:2] != guide.shape[:2]:
                mask = cv2.resize(mask, (guide.shape[1], guide.shape[0]))
            mask = cv2.ximgproc.guidedFilter(guide, mask, 3, 1e-3)
        mask = cv2.GaussianBlur(mask, (3, 3), 0)
        # Pull outer feather slightly inward to prevent bright background halos around hair
        mask = np.clip(mask, 0.0, 1.0)
        return mask * mask

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
                refined = self._segment_and_refine_small(small)
                self.last_error = ""
            except Exception as exc:
                self.last_error = str(exc)
                refined = None
            with self._cond:
                if refined is not None:
                    self._refined_small = refined
                self._cond.notify_all()

    def _submit(self, frame: np.ndarray) -> np.ndarray | None:
        h, w = frame.shape[:2]
        sw = 256
        sh = max(1, int(h * sw / w))
        small = cv2.resize(frame, (sw, sh), interpolation=cv2.INTER_NEAREST)
        with self._cond:
            if self._thread is None:
                self._running = True
                self._thread = threading.Thread(target=self._worker_loop, daemon=True)
                self._thread.start()
            self._pending = small
            self._cond.notify()
            if self._refined_small is None:
                self._cond.wait(timeout=1.5)
            return self._refined_small

    def _checkerboard(self, w: int, h: int) -> np.ndarray:
        if self._checker_cache is None or self._checker_cache.shape[:2] != (h, w):
            tile = 16
            y = np.arange(h) // tile
            x = np.arange(w) // tile
            grid = (y[:, None] + x[None, :]) % 2
            c = np.where(grid[..., None] == 0, 28, 42).astype(np.uint8)
            self._checker_cache = np.repeat(c, 3, axis=2)
        return self._checker_cache

    def render_preview(self, frame: np.ndarray, out: np.ndarray) -> np.ndarray:
        """Render PNG checkerboard behind cutout in the UI preview window."""
        if not self.enabled or self.mode != "cutout" or self._refined_small is None:
            return out
        h, w = out.shape[:2]
        mask = cv2.resize(self._refined_small, (w, h), interpolation=cv2.INTER_LINEAR)
        checker = self._checkerboard(w, h)
        return cv2.blendLinear(frame, checker, mask, 1.0 - mask)

    def process(self, frame: np.ndarray) -> np.ndarray:
        raw_mask = self._submit(frame)
        if raw_mask is None:
            return frame
        h, w = frame.shape[:2]
        mask = cv2.resize(raw_mask, (w, h), interpolation=cv2.INTER_LINEAR)

        if self.mode == "cutout":
            # Pure PNG-style person cutout (background erased to 0)
            m_u8 = (mask * 255.0).astype(np.uint8)
            m3 = cv2.merge((m_u8, m_u8, m_u8))
            return cv2.multiply(frame, m3, scale=1.0 / 255.0, dtype=cv2.CV_8U)

        if self.mode == "greenscreen":
            bg = np.full_like(frame, (0, 255, 0), dtype=np.uint8)
            return cv2.blendLinear(frame, bg, mask, 1.0 - mask)

        if self.mode == "blur":
            small = cv2.resize(frame, (w // 4, h // 4), interpolation=cv2.INTER_NEAREST)
            small = cv2.GaussianBlur(small, (0, 0), 6.0)
            bg = cv2.resize(small, (w, h), interpolation=cv2.INTER_LINEAR)
            return cv2.blendLinear(frame, bg, mask, 1.0 - mask)

        bg = np.full_like(frame, self.color, dtype=np.uint8)
        return cv2.blendLinear(frame, bg, mask, 1.0 - mask)
