"""Ultra-lightweight automatic visual enhancement ("auto visual").

Zero neural-network overhead, zero background threads, < 0.3 ms per frame:
meters the portrait zone on a tiny subsampled grid every few frames and applies
a fused 3-channel lookup table (`cv2.LUT`) for shadow recovery, highlight
protection, contrast, and auto white balance.
"""
from __future__ import annotations

import cv2
import numpy as np


class AutoEnhancer:
    name = "enhance"

    def __init__(self, enabled: bool = True):
        self.enabled = enabled
        self.strength = 0.85
        self.target_brightness = 0.54
        self._gamma = 1.0
        self._shadow_lift = 0.0
        self._wb_gains = np.ones(3, np.float32)
        self._lut = np.arange(256, dtype=np.uint8).reshape(1, 256, 1).repeat(3, axis=2)
        self._frame_no = 0
        self.last_brightness = 0.5

    def close(self) -> None:
        pass

    def _update_lut(self, frame: np.ndarray) -> None:
        s = float(np.clip(self.strength, 0.0, 1.0))
        # Subsample 1/8th in each dimension -> ~80x60 thumbnail (takes < 0.05 ms!)
        small = frame[::8, ::8]

        # 1) Midtone Auto White Balance
        sub = small.reshape(-1, 3).astype(np.float32)
        luma = sub.mean(axis=1)
        mid = sub[(luma > 16.0) & (luma < 225.0)]
        if len(mid) < 32:
            mid = sub
        means = mid.mean(axis=0) + 1e-3
        gains = np.clip(means.mean() / means, 0.80, 1.35)
        self._wb_gains = 0.85 * self._wb_gains + 0.15 * gains

        # 2) Portrait-zone Shadow & Subject Metering
        sh, sw = small.shape[:2]
        gray = luma.reshape(sh, sw)
        portrait = gray[sh // 5: 9 * sh // 10, sw // 4: 3 * sw // 4]
        p25 = float(np.percentile(gray, 25)) / 255.0
        port_p40 = float(np.percentile(portrait, 40)) / 255.0 if portrait.size else p25
        subj = 0.70 * port_p40 + 0.30 * p25
        self.last_brightness = subj

        target = float(np.clip(self.target_brightness, 0.25, 0.80))
        darkness = float(np.clip((target - subj) / max(target, 0.2), 0.0, 1.0))

        b = float(np.clip(subj, 0.05, 0.95))
        wanted_gamma = float(np.clip(np.log(target) / np.log(b), 0.45, 1.40))
        self._gamma = 0.82 * self._gamma + 0.18 * wanted_gamma
        self._shadow_lift = 0.82 * self._shadow_lift + 0.18 * darkness

        # 3) Build Fused 1x256x3 Lookup Table
        gamma = 1.0 + (self._gamma - 1.0) * s
        lift = self._shadow_lift * s
        hl_protect = max(0.0, (1.0 - gamma) * 0.85 + lift * 0.45)
        wb = 1.0 + (self._wb_gains - 1.0) * s

        x = np.linspace(0.0, 1.0, 256, dtype=np.float32).reshape(256, 1)
        x_wb = np.clip(x * wb.reshape(1, 3), 0.0, 1.0)
        y = np.power(x_wb, gamma)
        if lift > 0.01:
            toe = lift * 0.48 * x_wb * np.power(1.0 - x_wb, 1.8)
            y = np.clip(y + toe, 0.0, 1.0)
        if hl_protect > 0.01:
            knee = 0.72
            mask = y > knee
            over = (y[mask] - knee) / (1.0 - knee)
            y[mask] = knee + (1.0 - knee) * (over / (1.0 + hl_protect * 0.6 * over))

        # Subtle S-curve contrast
        c = 0.12 * s
        y = np.clip(y + c * y * (1.0 - y) * (y - 0.35), 0.0, 1.0)

        self._lut = np.clip(np.round(y * 255.0), 0, 255).astype(np.uint8).reshape(1, 256, 3)

    def process(self, frame: np.ndarray) -> np.ndarray:
        if not self.enabled:
            return frame
        self._frame_no += 1
        if self._frame_no <= 3 or self._frame_no % 8 == 0:
            self._update_lut(frame)
        return cv2.LUT(frame, self._lut)
