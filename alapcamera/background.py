"""Ultra-lightweight AI background removal (PNG cutout / virtual green screen / blur).

Runs MediaPipe selfie_segmenter at 160x120 in a throttled background thread so
CPU usage stays very low while the main video stream runs at full native FPS.
"""
from __future__ import annotations

import threading
import time

import cv2
import numpy as np

from .models import get_model


class BackgroundProcessor:
    name = "background"

    def __init__(self, enabled: bool = False):
        # Off by default on startup for maximum lightness; 1-click to enable
        self.enabled = enabled
        self.mode = "cutout"  # "cutout" | "greenscreen" | "blur"
        self.threshold = 0.52
        self.edge_softness = 0.35
        self.smoothing = 0.40

        self._segmenter = None
        self._mp = None
        self._prev_mask: np.ndarray | None = None
        self._checker_cache: np.ndarray | None = None
        self._green_cache: np.ndarray | None = None
        self._t0 = time.monotonic()
        self._last_ts = -1
        self.last_error = ""

        self._cond = threading.Condition()
        self._pending: np.ndarray | None = None
        self._mask_u8_small: np.ndarray | None = None
        self._running = False
        self._thread: threading.Thread | None = None

    def _ensure_segmenter(self) -> None:
        if self._segmenter is not None:
            return
        import mediapipe as mp
        from mediapipe.tasks.python import BaseOptions, vision

        opts = vision.ImageSegmenterOptions(
            base_options=BaseOptions(model_asset_path=str(get_model("selfie"))),
            running_mode=vision.RunningMode.VIDEO,
            output_confidence_masks=True,
            output_category_mask=False,
        )
        self._segmenter = vision.ImageSegmenter.create_from_options(opts)
        self._mp = mp
        self._prev_mask = None

    def close(self) -> None:
        with self._cond:
            self._running = False
            self._cond.notify_all()
        if self._thread is not None:
            self._thread.join(timeout=1.5)
            self._thread = None
        if self._segmenter is not None:
            self._segmenter.close()
            self._segmenter = None
        self._mask_u8_small = None

    def _segment_small(self, small: np.ndarray) -> np.ndarray:
        rgb = np.ascontiguousarray(cv2.cvtColor(small, cv2.COLOR_BGR2RGB))
        image = self._mp.Image(image_format=self._mp.ImageFormat.SRGB, data=rgb)
        ts = int((time.monotonic() - self._t0) * 1000)
        if ts <= self._last_ts:
            ts = self._last_ts + 1
        self._last_ts = ts
        result = self._segmenter.segment_for_video(image, ts)
        mask = np.squeeze(np.asarray(result.confidence_masks[0].numpy_view(), dtype=np.float32))

        if self._prev_mask is not None and self._prev_mask.shape == mask.shape:
            a = float(self.smoothing)
            mask = a * self._prev_mask + (1.0 - a) * mask
        self._prev_mask = mask

        t = float(self.threshold)
        soft = 0.05 + 0.12 * float(self.edge_softness)
        mask = np.clip((mask - (t - soft)) / (2.0 * soft), 0.0, 1.0)
        mask = cv2.GaussianBlur(mask, (3, 3), 0)
        mask = mask * mask
        return np.clip(mask * 255.0, 0.0, 255.0).astype(np.uint8)

    def _worker_loop(self) -> None:
        while True:
            with self._cond:
                while self._pending is None and self._running:
                    self._cond.wait()
                if not self._running:
                    return
                small, self._pending = self._pending, None
            t0 = time.perf_counter()
            try:
                self._ensure_segmenter()
                m_u8 = self._segment_small(small)
                self.last_error = ""
            except Exception as exc:
                self.last_error = str(exc)
                m_u8 = None
            with self._cond:
                if m_u8 is not None:
                    self._mask_u8_small = m_u8
                self._cond.notify_all()
            # Cap segmentation rate to ~20 Hz so CPU stays cool and video FPS stays maxed
            elapsed = time.perf_counter() - t0
            if elapsed < 0.045:
                time.sleep(0.045 - elapsed)

    def _submit(self, frame: np.ndarray) -> np.ndarray | None:
        small = cv2.resize(frame, (160, 120), interpolation=cv2.INTER_NEAREST)
        with self._cond:
            if self._thread is None:
                self._running = True
                self._thread = threading.Thread(target=self._worker_loop, daemon=True)
                self._thread.start()
            self._pending = small
            self._cond.notify()
            return self._mask_u8_small

    def _checkerboard(self, w: int, h: int) -> np.ndarray:
        if self._checker_cache is None or self._checker_cache.shape[:2] != (h, w):
            tile = 16
            y = np.arange(h) // tile
            x = np.arange(w) // tile
            grid = (y[:, None] + x[None, :]) % 2
            c = np.where(grid[..., None] == 0, 28, 44).astype(np.uint8)
            self._checker_cache = np.repeat(c, 3, axis=2)
        return self._checker_cache

    def render_preview(self, frame: np.ndarray, out: np.ndarray) -> np.ndarray:
        """Render PNG checkerboard behind cutout in the UI preview window."""
        if not self.enabled or self.mode != "cutout" or self._mask_u8_small is None:
            return out
        h, w = out.shape[:2]
        m_u8 = cv2.resize(self._mask_u8_small, (w, h), interpolation=cv2.INTER_LINEAR)
        inv_u8 = 255 - m_u8
        checker = self._checkerboard(w, h)
        fg = cv2.multiply(frame, cv2.merge((m_u8, m_u8, m_u8)), scale=1.0 / 255.0, dtype=cv2.CV_8U)
        bg = cv2.multiply(checker, cv2.merge((inv_u8, inv_u8, inv_u8)), scale=1.0 / 255.0, dtype=cv2.CV_8U)
        return cv2.add(fg, bg)

    def process(self, frame: np.ndarray) -> np.ndarray:
        raw_u8 = self._submit(frame)
        if raw_u8 is None:
            return frame
        h, w = frame.shape[:2]
        m_u8 = cv2.resize(raw_u8, (w, h), interpolation=cv2.INTER_LINEAR)
        m3 = cv2.merge((m_u8, m_u8, m_u8))
        fg = cv2.multiply(frame, m3, scale=1.0 / 255.0, dtype=cv2.CV_8U)

        if self.mode == "cutout":
            return fg

        inv_u8 = 255 - m_u8
        inv3 = cv2.merge((inv_u8, inv_u8, inv_u8))
        if self.mode == "greenscreen":
            if self._green_cache is None or self._green_cache.shape[:2] != (h, w):
                self._green_cache = np.full((h, w, 3), (0, 255, 0), dtype=np.uint8)
            bg = cv2.multiply(self._green_cache, inv3, scale=1.0 / 255.0, dtype=cv2.CV_8U)
            return cv2.add(fg, bg)

        # blur mode
        small = cv2.resize(frame, (w // 4, h // 4), interpolation=cv2.INTER_NEAREST)
        small = cv2.GaussianBlur(small, (0, 0), 5.0)
        blurred = cv2.resize(small, (w, h), interpolation=cv2.INTER_LINEAR)
        bg = cv2.multiply(blurred, inv3, scale=1.0 / 255.0, dtype=cv2.CV_8U)
        return cv2.add(fg, bg)
