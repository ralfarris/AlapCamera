"""Modern Dark Studio UI for AlapCamera."""
from __future__ import annotations

import json
import subprocess
import sys
import threading
import time
import tkinter as tk
from pathlib import Path
from tkinter import colorchooser, filedialog, messagebox, ttk

import cv2
import numpy as np
from PIL import Image, ImageTk

from . import __version__
from .background import BackgroundProcessor
from .camera import CameraInfo, CameraStream, default_camera, list_cameras
from .enhance import AutoEnhancer
from .pipeline import MirrorProcessor, Pipeline
from .virtualcam import VirtualCamera

PREVIEW_W, PREVIEW_H = 640, 360
RESOLUTIONS = {"1280x720": (1280, 720), "1920x1080": (1920, 1080), "640x480": (640, 480)}
SETTINGS_FILE = Path(__file__).resolve().parent.parent / "alapcamera_settings.json"

# Modern Studio Dark Palette
BG_APP = "#0d0f14"
BG_PANEL = "#151922"
BG_CARD = "#1b202c"
BG_INPUT = "#242b3a"
BG_HOVER = "#2e374a"
BORDER = "#283042"
TEXT_MAIN = "#f3f4f6"
TEXT_DIM = "#9ca3af"
TEXT_MUTED = "#64748b"
ACCENT = "#6366f1"
ACCENT_HOVER = "#818cf8"
ACCENT_DIM = "#1e223d"
SUCCESS = "#10b981"
SUCCESS_HOVER = "#059669"

FONT_TITLE = ("Segoe UI Semibold", 13)
FONT_CARD = ("Segoe UI Semibold", 9)
FONT_BODY = ("Segoe UI", 9)
FONT_SMALL = ("Segoe UI", 8)


class ModernSwitch(tk.Canvas):
    """Sleek studio toggle switch with label."""

    def __init__(self, parent, text: str, variable: tk.BooleanVar, command=None,
                 bg=BG_CARD, width: int = 200):
        super().__init__(parent, width=width, height=24, bg=bg,
                         highlightthickness=0, bd=0, cursor="hand2")
        self.var = variable
        self.command = command
        self.text = text
        self._req_w = width
        self.bind("<Button-1>", self._toggle)
        self.bind("<Configure>", lambda e: self._draw())
        self.var.trace_add("write", lambda *_: self._draw())
        self._draw()

    def _toggle(self, _event=None) -> None:
        self.var.set(not self.var.get())
        if self.command:
            self.command()

    def _draw(self) -> None:
        self.delete("all")
        on = bool(self.var.get())
        w = self.winfo_width()
        if w <= 1:
            w = self._req_w
        self.create_text(2, 12, text=self.text, anchor="w",
                         fill=TEXT_MAIN if on else TEXT_DIM, font=FONT_BODY)
        tw, th = 34, 18
        tx = w - tw - 2
        ty = 3
        track_color = ACCENT if on else BG_INPUT
        r = th // 2
        self.create_oval(tx, ty, tx + th, ty + th, fill=track_color, outline="")
        self.create_oval(tx + tw - th, ty, tx + tw, ty + th, fill=track_color, outline="")
        self.create_rectangle(tx + r, ty, tx + tw - r, ty + th, fill=track_color, outline="")
        kx = (tx + tw - th + 3) if on else (tx + 3)
        self.create_oval(kx, ty + 3, kx + th - 6, ty + th - 3, fill="#ffffff", outline="")


class ToggleChip(tk.Label):
    """Compact pill toggle chip for secondary AI sub-options."""

    def __init__(self, parent, text: str, variable: tk.BooleanVar, command=None):
        super().__init__(parent, font=FONT_SMALL, padx=8, pady=4, cursor="hand2", anchor="w")
        self.var = variable
        self.text = text
        self.command = command
        self.bind("<Button-1>", self._toggle)
        self.var.trace_add("write", lambda *_: self._sync())
        self._sync()

    def _toggle(self, _e=None) -> None:
        self.var.set(not self.var.get())
        if self.command:
            self.command()

    def _sync(self) -> None:
        on = bool(self.var.get())
        if on:
            self.configure(
                text=f"●  {self.text}",
                bg=ACCENT_DIM,
                fg=ACCENT_HOVER,
                highlightbackground=ACCENT,
                highlightthickness=1,
            )
        else:
            self.configure(
                text=f"○  {self.text}",
                bg=BG_INPUT,
                fg=TEXT_DIM,
                highlightbackground=BORDER,
                highlightthickness=1,
            )


class ModernSlider(tk.Canvas):
    """Sleek accent slider with live percentage readout."""

    def __init__(self, parent, text: str, variable: tk.DoubleVar, lo=0.0, hi=1.0,
                 command=None, bg=BG_CARD):
        super().__init__(parent, width=220, height=25, bg=bg,
                         highlightthickness=0, bd=0, cursor="hand2")
        self.text = text
        self.var = variable
        self.lo = lo
        self.hi = hi
        self.command = command
        self.bind("<Button-1>", self._on_drag)
        self.bind("<B1-Motion>", self._on_drag)
        self.bind("<Configure>", lambda e: self._draw())
        self.var.trace_add("write", lambda *_: self._draw())
        self._draw()

    def _on_drag(self, event) -> None:
        w = max(self.winfo_width(), 120)
        pad = 7
        ratio = float(np.clip((event.x - pad) / max(1, w - 2 * pad), 0.0, 1.0))
        val = self.lo + ratio * (self.hi - self.lo)
        self.var.set(val)
        if self.command:
            self.command(val)

    def _draw(self) -> None:
        self.delete("all")
        w = self.winfo_width()
        if w <= 1:
            w = 240
        val = float(np.clip(self.var.get(), self.lo, self.hi))
        ratio = (val - self.lo) / max(1e-6, self.hi - self.lo)
        pct = int(round(ratio * 100))
        self.create_text(2, 8, text=self.text, anchor="w", fill=TEXT_DIM, font=FONT_SMALL)
        self.create_text(w - 2, 8, text=f"{pct}%", anchor="e", fill=TEXT_MAIN, font=FONT_SMALL)

        pad = 7
        y = 18
        x0, x1 = pad, w - pad
        xm = x0 + ratio * (x1 - x0)
        self.create_line(x0, y, x1, y, fill=BG_INPUT, width=4, capstyle="round")
        if xm > x0 + 1:
            self.create_line(x0, y, xm, y, fill=ACCENT, width=4, capstyle="round")
        self.create_oval(xm - 5, y - 5, xm + 5, y + 5, fill="#ffffff", outline=ACCENT, width=2)


class FlatButton(tk.Label):
    """Modern flat button with hover state."""

    def __init__(self, parent, text: str, command=None, bg=BG_INPUT, hover=BG_HOVER,
                 fg=TEXT_MAIN, font=FONT_BODY, pady=6, padx=10):
        super().__init__(parent, text=text, bg=bg, fg=fg, font=font,
                         padx=padx, pady=pady, cursor="hand2", anchor="center")
        self._bg = bg
        self._hover = hover
        self._command = command
        self.bind("<Enter>", lambda e: self.configure(bg=self._hover))
        self.bind("<Leave>", lambda e: self.configure(bg=self._bg))
        self.bind("<Button-1>", self._click)

    def set_colors(self, bg: str, hover: str, fg: str = TEXT_MAIN) -> None:
        self._bg = bg
        self._hover = hover
        self.configure(bg=bg, fg=fg)

    def _click(self, _e=None) -> None:
        if self._command:
            self._command()


class AlapCameraApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        root.title(f"AlapCamera Studio v{__version__}")
        root.configure(bg=BG_APP)
        root.protocol("WM_DELETE_WINDOW", self.on_close)
        root.minsize(1040, 660)
        _apply_dark_titlebar(root)
        _configure_ttk_styles(root)

        self.cameras: list[CameraInfo] = []
        self.stream: CameraStream | None = None
        self.vcam: VirtualCamera | None = None
        self.pipeline = Pipeline()
        self.mirror = self.pipeline.add(MirrorProcessor(enabled=True))
        self._build_pipeline()

        self._latest_output = None
        self._latest_raw = None
        self._out_lock = threading.Lock()
        self._worker_running = False
        self._worker: threading.Thread | None = None
        self._fps = 0.0
        self._bound_vars: list[tuple[tk.Variable, object, str]] = []
        self._preferred_camera: str = ""

        self._load_settings()
        self._build_ui()
        self.refresh_cameras()
        self._update_preview()

    # ------------------------------------------------------------------ setup
    def _build_pipeline(self) -> None:
        self.enhancer = self.pipeline.add(AutoEnhancer(enabled=True))
        self.background = self.pipeline.add(BackgroundProcessor(enabled=False))

    def _card(self, parent: tk.Widget, title: str) -> tk.Frame:
        outer = tk.Frame(parent, bg=BORDER, padx=1, pady=1)
        outer.pack(fill="x", pady=(0, 6))
        inner = tk.Frame(outer, bg=BG_CARD, padx=10, pady=6)
        inner.pack(fill="x")
        hdr = tk.Frame(inner, bg=BG_CARD)
        hdr.pack(fill="x", pady=(0, 3))
        tk.Label(hdr, text=title.upper(), bg=BG_CARD, fg=ACCENT_HOVER,
                 font=("Segoe UI Semibold", 8)).pack(side="left")
        return inner

    def _build_ui(self) -> None:
        # 1) Top Studio Header Bar
        header = tk.Frame(self.root, bg=BG_PANEL, padx=16, pady=8,
                          highlightbackground=BORDER, highlightthickness=1)
        header.pack(side="top", fill="x")

        brand = tk.Frame(header, bg=BG_PANEL)
        brand.pack(side="left")
        dot = tk.Canvas(brand, width=12, height=12, bg=BG_PANEL, highlightthickness=0)
        dot.pack(side="left", padx=(0, 8))
        dot.create_oval(1, 1, 11, 11, fill=ACCENT, outline="")
        tk.Label(brand, text="AlapCamera", bg=BG_PANEL, fg=TEXT_MAIN, font=FONT_TITLE).pack(side="left")
        tk.Label(brand, text=f"v{__version__}", bg=BG_INPUT, fg=TEXT_DIM,
                 font=FONT_SMALL, padx=6, pady=1).pack(side="left", padx=(8, 0))

        hdr_right = tk.Frame(header, bg=BG_PANEL)
        hdr_right.pack(side="right")
        self.live_badge = tk.Label(hdr_right, text="● STANDBY", bg=BG_INPUT, fg=TEXT_DIM,
                                   font=("Segoe UI Semibold", 8), padx=8, pady=3)
        self.live_badge.pack(side="left", padx=(0, 10))
        self.status = tk.StringVar(value="Ready")
        tk.Label(hdr_right, textvariable=self.status, bg=BG_PANEL, fg=TEXT_DIM,
                 font=FONT_BODY).pack(side="left")

        # 2) Main Body
        body = tk.Frame(self.root, bg=BG_APP, padx=12, pady=10)
        body.pack(fill="both", expand=True)

        # Right Inspector Sidebar (fixed width 340px)
        sidebar = tk.Frame(body, bg=BG_APP, width=340)
        sidebar.pack(side="right", fill="y", padx=(12, 0))
        sidebar.pack_propagate(False)

        # Docked Virtual Camera Output Card at the very bottom of sidebar (never clipped!)
        vcam_outer = tk.Frame(sidebar, bg=BORDER, padx=1, pady=1)
        vcam_outer.pack(side="bottom", fill="x", pady=(6, 0))
        vcam_card = tk.Frame(vcam_outer, bg=BG_CARD, padx=10, pady=8)
        vcam_card.pack(fill="x")
        tk.Label(vcam_card, text="VIRTUAL CAMERA OUTPUT", bg=BG_CARD, fg=ACCENT_HOVER,
                 font=("Segoe UI Semibold", 8)).pack(anchor="w", pady=(0, 5))
        self.vcam_btn = FlatButton(vcam_card, text="▶  Start Virtual Camera",
                                   command=self.toggle_vcam, bg=ACCENT, hover=ACCENT_HOVER,
                                   font=("Segoe UI Semibold", 10), pady=7)
        self.vcam_btn.pack(fill="x")
        row_drv = tk.Frame(vcam_card, bg=BG_CARD)
        row_drv.pack(fill="x", pady=(5, 0))
        tk.Label(row_drv, text="Streamlabs / OBS / Zoom: 'AlapCamera'", bg=BG_CARD, fg=TEXT_DIM,
                 font=FONT_SMALL).pack(side="left")
        FlatButton(row_drv, text="Install Driver", command=self.install_vcam_driver,
                   bg=BG_INPUT, hover=BG_HOVER, fg=TEXT_DIM, font=FONT_SMALL,
                   pady=2, padx=8).pack(side="right")

        # Scrollable cards container above the docked Virtual Camera card
        scroll_wrap = tk.Frame(sidebar, bg=BG_APP)
        scroll_wrap.pack(side="top", fill="both", expand=True)
        self.side_canvas = tk.Canvas(scroll_wrap, bg=BG_APP, highlightthickness=0, bd=0)
        scrollbar = ttk.Scrollbar(scroll_wrap, orient="vertical", command=self.side_canvas.yview)
        self.side = tk.Frame(self.side_canvas, bg=BG_APP)
        self._side_win = self.side_canvas.create_window((0, 0), window=self.side, anchor="nw")
        self.side_canvas.configure(yscrollcommand=scrollbar.set)
        self.side_canvas.pack(side="left", fill="both", expand=True)

        def _on_side_configure(_e=None):
            self.side_canvas.configure(scrollregion=self.side_canvas.bbox("all"))
            self.side_canvas.itemconfigure(self._side_win, width=self.side_canvas.winfo_width())

        self.side.bind("<Configure>", _on_side_configure)
        self.side_canvas.bind("<Configure>", _on_side_configure)
        self.root.bind_all("<MouseWheel>", self._on_mousewheel)

        # Left Video Stage
        stage = tk.Frame(body, bg=BORDER, padx=1, pady=1)
        stage.pack(side="left", fill="both", expand=True)
        stage_in = tk.Frame(stage, bg="#08090d")
        stage_in.pack(fill="both", expand=True)

        # Bottom quick toolbar under the video preview
        bar = tk.Frame(stage_in, bg=BG_PANEL, padx=14, pady=7)
        bar.pack(side="bottom", fill="x")
        self.mirror_var = tk.BooleanVar(value=self.mirror.enabled)
        ModernSwitch(bar, "Mirror Video", self.mirror_var,
                     command=lambda: setattr(self.mirror, "enabled", self.mirror_var.get()),
                     bg=BG_PANEL, width=135).pack(side="left")
        self.split_var = tk.BooleanVar(value=False)
        ModernSwitch(bar, "Split Compare (Before / After)", self.split_var,
                     bg=BG_PANEL, width=260).pack(side="left", padx=(24, 0))
        FlatButton(bar, text="↻  Refresh Cameras", command=self.refresh_cameras,
                   bg=BG_INPUT, hover=BG_HOVER, font=FONT_SMALL, pady=4, padx=12).pack(side="right")

        self.preview_box = tk.Frame(stage_in, bg="#08090d", width=PREVIEW_W, height=PREVIEW_H)
        self.preview_box.pack(fill="both", expand=True)
        self.preview_box.pack_propagate(False)
        self.preview = tk.Label(self.preview_box, text="Scanning cameras...",
                                bg="#08090d", fg=TEXT_DIM, font=FONT_TITLE)
        self.preview.pack(fill="both", expand=True)

        # Build Inspector Cards
        self._build_source_card(self.side)
        self._build_enhance_panel(self.side)
        self._build_background_panel(self.side)

    def _on_mousewheel(self, event) -> None:
        try:
            x, y = self.root.winfo_pointerxy()
            sx = self.side_canvas.winfo_rootx()
            sy = self.side_canvas.winfo_rooty()
            if sx <= x <= sx + self.side_canvas.winfo_width() and sy <= y <= sy + self.side_canvas.winfo_height():
                self.side_canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")
        except Exception:
            pass

    def _build_source_card(self, parent: tk.Frame) -> None:
        card = self._card(parent, "Camera Source")
        self.cam_var = tk.StringVar()
        self.cam_combo = ttk.Combobox(card, textvariable=self.cam_var, state="readonly", style="Dark.TCombobox")
        self.cam_combo.pack(fill="x", pady=(0, 6))
        self.cam_combo.bind("<<ComboboxSelected>>", lambda e: self.start_camera())

        row = tk.Frame(card, bg=BG_CARD)
        row.pack(fill="x")
        tk.Label(row, text="Output Resolution", bg=BG_CARD, fg=TEXT_DIM, font=FONT_BODY).pack(side="left")
        self.res_var = tk.StringVar(value=getattr(self, "_initial_resolution", "1280x720"))
        res = ttk.Combobox(row, textvariable=self.res_var, values=list(RESOLUTIONS),
                           state="readonly", width=11, style="Dark.TCombobox")
        res.pack(side="right")
        res.bind("<<ComboboxSelected>>", lambda e: self.start_camera())

    def install_vcam_driver(self) -> None:
        script = Path(__file__).resolve().parent.parent / "scripts" / "install_virtualcam.ps1"
        if not script.exists():
            messagebox.showerror("Virtual camera", f"Installer not found:\n{script}")
            return
        subprocess.Popen(["powershell", "-ExecutionPolicy", "Bypass", "-File", str(script)])
        messagebox.showinfo("Virtual camera",
                            "Accept the admin prompt to install 'AlapCamera'.\n"
                            "Then click 'Start Virtual Camera' and restart Streamlabs/OBS.")

    # --------------------------------------------------------- feature panels
    def _check(self, parent, text: str, obj, attr: str) -> tk.BooleanVar:
        var = tk.BooleanVar(value=getattr(obj, attr))
        sw = ModernSwitch(parent, text, var, command=lambda: setattr(obj, attr, var.get()), bg=BG_CARD)
        sw.pack(fill="x", pady=1)
        self._bound_vars.append((var, obj, attr))
        return var

    def _chip(self, parent, text: str, obj, attr: str) -> tk.BooleanVar:
        var = tk.BooleanVar(value=getattr(obj, attr))
        chip = ToggleChip(parent, text, var, command=lambda: setattr(obj, attr, var.get()))
        self._bound_vars.append((var, obj, attr))
        return var, chip

    def _slider(self, parent, text: str, obj, attr: str, lo=0.0, hi=1.0) -> tk.DoubleVar:
        var = tk.DoubleVar(value=getattr(obj, attr))
        sl = ModernSlider(parent, text, var, lo=lo, hi=hi,
                          command=lambda v: setattr(obj, attr, float(v)), bg=BG_CARD)
        sl.pack(fill="x", pady=1)
        self._bound_vars.append((var, obj, attr))
        return var

    def _build_enhance_panel(self, parent: tk.Frame) -> None:
        card = self._card(parent, "AI Visual Enhance")
        e = self.enhancer
        self._check(card, "Enable Auto Enhance", e, "enabled")
        self._slider(card, "Enhance Strength", e, "strength")
        self._slider(card, "Target Brightness", e, "target_brightness", 0.25, 0.75)
        self._slider(card, "Detail Sharpen", e, "sharpen")

        grid = tk.Frame(card, bg=BG_CARD)
        grid.pack(fill="x", pady=(4, 0))
        grid.columnconfigure(0, weight=1, uniform="chip")
        grid.columnconfigure(1, weight=1, uniform="chip")

        items = [
            ("Auto Exposure", "auto_exposure"),
            ("Face Priority", "face_priority"),
            ("White Balance", "white_balance"),
            ("Shadow Boost", "local_contrast"),
            ("Denoise", "denoise"),
        ]
        for i, (label, attr) in enumerate(items):
            _, chip = self._chip(grid, label, e, attr)
            chip.grid(row=i // 2, column=i % 2, sticky="ew", padx=(0 if i % 2 == 0 else 3, 3 if i % 2 == 0 else 0), pady=2)

        rst = FlatButton(grid, text="↺ Reset Defaults", command=self._reset_enhance,
                         bg=BG_INPUT, hover=BG_HOVER, fg=TEXT_DIM, font=FONT_SMALL, pady=4, padx=6)
        rst.grid(row=2, column=1, sticky="ew", padx=(3, 0), pady=2)

    def _reset_enhance(self) -> None:
        defaults = AutoEnhancer(enabled=True)
        for attr in ("enabled", "strength", "target_brightness", "sharpen",
                     "auto_exposure", "face_priority", "white_balance",
                     "local_contrast", "denoise"):
            setattr(self.enhancer, attr, getattr(defaults, attr))
        defaults.close()
        for var, obj, attr in self._bound_vars:
            if obj is self.enhancer:
                var.set(getattr(obj, attr))

    def _build_background_panel(self, parent: tk.Frame) -> None:
        card = self._card(parent, "AI Background Studio")
        b = self.background
        self.bg_enabled_var = self._check(card, "Enable Background Effect", b, "enabled")

        self.bg_mode = tk.StringVar(value=b.mode)
        seg = tk.Frame(card, bg=BG_INPUT, padx=2, pady=2)
        seg.pack(fill="x", pady=(4, 4))
        self._mode_btns: dict[str, FlatButton] = {}
        for label, val in (("Blur", "blur"), ("Remove (Key)", "remove"), ("Custom Image", "image")):
            btn = FlatButton(seg, text=label, command=lambda m=val: self._select_bg_mode(m),
                             bg=ACCENT if b.mode == val else BG_INPUT,
                             hover=ACCENT_HOVER if b.mode == val else BG_HOVER,
                             font=FONT_SMALL, pady=4, padx=6)
            btn.pack(side="left", fill="x", expand=True, padx=1)
            self._mode_btns[val] = btn

        row = tk.Frame(card, bg=BG_CARD)
        row.pack(fill="x", pady=(1, 2))
        tk.Label(row, text="AI Model Quality", bg=BG_CARD, fg=TEXT_DIM, font=FONT_SMALL).pack(side="left")
        q = tk.StringVar(value=b.quality)
        cb = ttk.Combobox(row, textvariable=q, values=["fast", "quality"],
                          state="readonly", width=10, style="Dark.TCombobox")
        cb.pack(side="right")
        cb.bind("<<ComboboxSelected>>", lambda e: setattr(b, "quality", q.get()))

        self._slider(card, "Blur Strength", b, "blur_strength")
        self._slider(card, "Edge Softness", b, "edge_softness")
        self._slider(card, "Mask Cut-off", b, "threshold", 0.2, 0.8)

        row_btn = tk.Frame(card, bg=BG_CARD)
        row_btn.pack(fill="x", pady=(4, 0))
        cb_b, cb_g, cb_r = b.color
        self._color_swatch = tk.Frame(row_btn, bg=f"#{cb_r:02x}{cb_g:02x}{cb_b:02x}", width=14, height=14)
        self._color_swatch.pack(side="left", padx=(0, 6))
        FlatButton(row_btn, text="Backdrop Color...", command=self._pick_bg_color,
                   bg=BG_INPUT, hover=BG_HOVER, font=FONT_SMALL, pady=4).pack(side="left", fill="x", expand=True)
        FlatButton(row_btn, text="Choose Image...", command=self._pick_bg_image,
                   bg=BG_INPUT, hover=BG_HOVER, font=FONT_SMALL, pady=4).pack(side="left", fill="x", expand=True, padx=(6, 0))

    def _refresh_mode_buttons(self) -> None:
        cur = self.bg_mode.get()
        for val, btn in self._mode_btns.items():
            if val == cur:
                btn.set_colors(ACCENT, ACCENT_HOVER, "#ffffff")
            else:
                btn.set_colors(BG_INPUT, BG_HOVER, TEXT_DIM)

    def _select_bg_mode(self, mode: str) -> None:
        if mode == "image" and self.background._bg_image is None:
            if not self._pick_bg_image():
                return
        self.bg_mode.set(mode)
        self.background.mode = mode
        self.background.enabled = True
        self.bg_enabled_var.set(True)
        self._refresh_mode_buttons()

    def _pick_bg_color(self) -> None:
        b, g, r = self.background.color
        res = colorchooser.askcolor(color=f"#{r:02x}{g:02x}{b:02x}", title="Background color")
        if res and res[0]:
            r, g, b = (int(c) for c in res[0])
            self.background.color = (b, g, r)
            self._color_swatch.configure(bg=f"#{r:02x}{g:02x}{b:02x}")
            self._select_bg_mode("remove")

    def _pick_bg_image(self) -> bool:
        path = filedialog.askopenfilename(
            title="Background image",
            filetypes=[("Images", "*.png *.jpg *.jpeg *.bmp *.webp"), ("All files", "*.*")])
        if not path:
            return False
        if not self.background.set_background_image(path):
            messagebox.showerror("Background", "Could not read that image.")
            return False
        self.bg_mode.set("image")
        self.background.mode = "image"
        self.background.enabled = True
        self.bg_enabled_var.set(True)
        self._refresh_mode_buttons()
        return True

    # --------------------------------------------------------------- cameras
    def refresh_cameras(self) -> None:
        """Scan cameras in a background thread so the UI never freezes."""
        self.status.set("Scanning cameras...")
        self.stop_vcam()
        self.stop_camera()
        with self._out_lock:
            self._latest_output = None
        self.preview.configure(image="", text="Scanning cameras...")
        self.preview.image = None

        def scan():
            cams = list_cameras()
            self.root.after(0, lambda: self._on_cameras(cams))

        threading.Thread(target=scan, daemon=True).start()

    def _on_cameras(self, cams: list[CameraInfo]) -> None:
        self.cameras = cams
        self.cam_combo["values"] = [str(c) for c in self.cameras]
        if self.cameras:
            idx = default_camera(self.cameras)
            if self._preferred_camera:
                for i, c in enumerate(self.cameras):
                    if c.name == self._preferred_camera:
                        idx = i
                        break
            self.cam_combo.current(idx)
            self.status.set(f"Found {len(self.cameras)} camera(s)")
            self.start_camera()
        else:
            self.status.set("No camera found")
            self.preview.configure(text="No camera found")

    def start_camera(self) -> None:
        sel = self.cam_combo.current()
        if sel < 0 or sel >= len(self.cameras):
            return
        restart_vcam = self.vcam is not None
        self.stop_vcam()
        self.stop_camera()
        w, h = RESOLUTIONS[self.res_var.get()]
        try:
            self.stream = CameraStream(self.cameras[sel].index, w, h).start()
        except RuntimeError as exc:
            messagebox.showerror("Camera", str(exc))
            self.stream = None
            return
        self._worker_running = True
        self._worker = threading.Thread(target=self._process_loop, daemon=True)
        self._worker.start()
        if restart_vcam:
            self.start_vcam()

    def stop_camera(self) -> None:
        self._worker_running = False
        if self._worker is not None:
            self._worker.join(timeout=1.0)
            self._worker = None
        if self.stream is not None:
            self.stream.stop()
            self.stream = None

    # ------------------------------------------------------------ processing
    def _process_loop(self) -> None:
        last = time.perf_counter()
        while self._worker_running and self.stream is not None:
            frame = self.stream.read()
            if frame is None:
                time.sleep(0.005)
                continue
            try:
                out = self.pipeline.process(frame)
            except Exception as exc:
                print("pipeline error:", exc)
                out = frame
            if self.vcam is not None:
                try:
                    self.vcam.send(out)
                except Exception as exc:
                    print("virtual camera error:", exc)
            with self._out_lock:
                self._latest_raw = cv2.flip(frame, 1) if self.mirror.enabled else frame
                self._latest_output = out
            now = time.perf_counter()
            dt = now - last
            last = now
            if dt > 0:
                self._fps = 0.9 * self._fps + 0.1 * (1.0 / dt)
            if self.vcam is None:
                time.sleep(max(0.0, 1 / 30 - dt))

    def _update_preview(self) -> None:
        with self._out_lock:
            frame = self._latest_output
            raw = self._latest_raw
        if frame is not None:
            if self.split_var.get() and raw is not None and raw.shape == frame.shape:
                frame = _make_split(raw, frame)
            h, w = frame.shape[:2]
            bw = max(160, self.preview_box.winfo_width())
            bh = max(90, self.preview_box.winfo_height())
            scale = min(bw / w, bh / h)
            small = cv2.resize(frame, (max(1, int(w * scale)), max(1, int(h * scale))),
                               interpolation=cv2.INTER_AREA)
            img = ImageTk.PhotoImage(Image.fromarray(cv2.cvtColor(small, cv2.COLOR_BGR2RGB)))
            self.preview.configure(image=img, text="")
            self.preview.image = img
            face_tag = " · Face AI ✓" if (self.enhancer.enabled and self.enhancer.face_box) else ""
            self.status.set(f"{w}×{h}  ·  {self._fps:4.1f} FPS{face_tag}")
        self.root.after(33, self._update_preview)

    # --------------------------------------------------------- virtual cam
    def toggle_vcam(self) -> None:
        if self.vcam is None:
            self.start_vcam()
        else:
            self.stop_vcam()

    def start_vcam(self) -> None:
        if self.stream is None:
            messagebox.showwarning("Virtual camera", "Select a source camera first.")
            return
        w, h = RESOLUTIONS[self.res_var.get()]
        vcam = VirtualCamera(w, h, 30)
        try:
            vcam.start()
        except Exception as exc:
            if messagebox.askyesno(
                    "Virtual camera",
                    f"Could not start virtual camera:\n{exc}\n\n"
                    "Install the 'AlapCamera' virtual webcam now? (needs admin)"):
                self.install_vcam_driver()
            return
        self.vcam = vcam
        self.vcam_btn.configure(text=f"■  Stop Virtual Camera ({vcam.device_name})")
        self.vcam_btn.set_colors(SUCCESS, SUCCESS_HOVER, "#ffffff")
        self.live_badge.configure(text=f"● LIVE · {vcam.device_name}", bg="#064e3b", fg="#34d399")

    def stop_vcam(self) -> None:
        if self.vcam is not None:
            self.vcam.stop()
            self.vcam = None
        if hasattr(self, "vcam_btn"):
            self.vcam_btn.configure(text="▶  Start Virtual Camera")
            self.vcam_btn.set_colors(ACCENT, ACCENT_HOVER, "#ffffff")
        if hasattr(self, "live_badge"):
            self.live_badge.configure(text="● STANDBY", bg=BG_INPUT, fg=TEXT_DIM)

    # -------------------------------------------------------------- settings
    def _load_settings(self) -> None:
        self._initial_resolution = "1280x720"
        if not SETTINGS_FILE.exists():
            return
        try:
            data = json.loads(SETTINGS_FILE.read_text(encoding="utf-8"))
        except Exception:
            return
        self._preferred_camera = str(data.get("camera", ""))
        if data.get("resolution") in RESOLUTIONS:
            self._initial_resolution = data["resolution"]
        if "mirror" in data:
            self.mirror.enabled = bool(data["mirror"])
        enh = data.get("enhance", {})
        for k in ("enabled", "strength", "target_brightness", "sharpen",
                  "auto_exposure", "face_priority", "white_balance",
                  "local_contrast", "denoise"):
            if k in enh:
                setattr(self.enhancer, k, enh[k])
        bg = data.get("background", {})
        for k in ("enabled", "mode", "quality", "blur_strength",
                  "edge_softness", "threshold"):
            if k in bg:
                setattr(self.background, k, bg[k])
        if isinstance(bg.get("color"), list) and len(bg["color"]) == 3:
            self.background.color = tuple(int(x) for x in bg["color"])
        if bg.get("image_path"):
            self.background.set_background_image(bg["image_path"])

    def _save_settings(self) -> None:
        sel = self.cam_combo.current()
        cam_name = self.cameras[sel].name if 0 <= sel < len(self.cameras) else self._preferred_camera
        data = {
            "camera": cam_name,
            "resolution": self.res_var.get(),
            "mirror": self.mirror.enabled,
            "enhance": {k: getattr(self.enhancer, k) for k in (
                "enabled", "strength", "target_brightness", "sharpen",
                "auto_exposure", "face_priority", "white_balance",
                "local_contrast", "denoise")},
            "background": {
                "enabled": self.background.enabled,
                "mode": self.background.mode,
                "quality": self.background.quality,
                "blur_strength": self.background.blur_strength,
                "edge_softness": self.background.edge_softness,
                "threshold": self.background.threshold,
                "color": list(self.background.color),
                "image_path": self.background.image_path,
            },
        }
        try:
            SETTINGS_FILE.write_text(json.dumps(data, indent=2), encoding="utf-8")
        except Exception:
            pass

    # ----------------------------------------------------------------- close
    def on_close(self) -> None:
        self._save_settings()
        self.stop_vcam()
        self.stop_camera()
        self.pipeline.close()
        self.root.destroy()


def _make_split(before: np.ndarray, after: np.ndarray) -> np.ndarray:
    """Side-by-side Before (left) / After (right) split on a single frame."""
    out = after.copy()
    h, w = out.shape[:2]
    mid = w // 2
    out[:, :mid] = before[:, :mid]
    cv2.line(out, (mid, 0), (mid, h), (99, 102, 241), 2)
    cv2.putText(out, "BEFORE", (14, 32), cv2.FONT_HERSHEY_SIMPLEX, 0.75, (220, 220, 220), 2, cv2.LINE_AA)
    cv2.putText(out, "AFTER", (mid + 14, 32), cv2.FONT_HERSHEY_SIMPLEX, 0.75, (129, 140, 248), 2, cv2.LINE_AA)
    return out


def _apply_dark_titlebar(root: tk.Tk) -> None:
    """Enable Windows 10/11 dark immersive title bar."""
    if sys.platform != "win32":
        return
    try:
        import ctypes
        root.update_idletasks()
        hwnd = ctypes.windll.user32.GetParent(root.winfo_id())
        val = ctypes.c_int(1)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, 20, ctypes.byref(val), ctypes.sizeof(val))
    except Exception:
        pass


def _configure_ttk_styles(root: tk.Tk) -> None:
    style = ttk.Style(root)
    try:
        style.theme_use("clam")
    except tk.TclError:
        pass
    style.configure(
        "Dark.TCombobox",
        fieldbackground=BG_INPUT,
        background=BG_INPUT,
        foreground=TEXT_MAIN,
        arrowcolor=TEXT_MAIN,
        bordercolor=BORDER,
        lightcolor=BORDER,
        darkcolor=BORDER,
        padding=4,
    )
    style.map(
        "Dark.TCombobox",
        fieldbackground=[("readonly", BG_INPUT)],
        selectbackground=[("readonly", BG_INPUT)],
        selectforeground=[("readonly", TEXT_MAIN)],
        foreground=[("readonly", TEXT_MAIN)],
        background=[("readonly", BG_INPUT)],
    )
    root.option_add("*TCombobox*Listbox.background", BG_CARD)
    root.option_add("*TCombobox*Listbox.foreground", TEXT_MAIN)
    root.option_add("*TCombobox*Listbox.selectBackground", ACCENT)
    root.option_add("*TCombobox*Listbox.selectForeground", "#ffffff")


def _enable_dpi_awareness() -> None:
    """Crisp UI on high-DPI Windows displays."""
    if sys.platform == "win32":
        try:
            import ctypes
            ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except Exception:
            pass


def run() -> None:
    _enable_dpi_awareness()
    root = tk.Tk()
    root.geometry("1240x820")
    AlapCameraApp(root)
    root.mainloop()
