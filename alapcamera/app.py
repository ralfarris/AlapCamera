"""Lightweight, Automatic Modern Dark Studio UI for AlapCamera."""
from __future__ import annotations

import json
import subprocess
import sys
import threading
import time
import tkinter as tk
from pathlib import Path
from tkinter import messagebox, ttk

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
ACCENT = "#6366f1"
ACCENT_HOVER = "#818cf8"
SUCCESS = "#10b981"

FONT_TITLE = ("Segoe UI Semibold", 13)
FONT_BODY = ("Segoe UI", 9)
FONT_SMALL = ("Segoe UI", 8)


class ModernSwitch(tk.Canvas):
    """Sleek studio toggle switch with label."""

    def __init__(self, parent, text: str, variable: tk.BooleanVar, command=None,
                 bg=BG_CARD, width: int = 220):
        super().__init__(parent, width=width, height=28, bg=bg,
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
        self.create_text(2, 14, text=self.text, anchor="w",
                         fill=TEXT_MAIN if on else TEXT_DIM, font=("Segoe UI Semibold", 9))
        tw, th = 38, 20
        tx = w - tw - 2
        ty = 4
        track_color = ACCENT if on else BG_INPUT
        r = th // 2
        self.create_oval(tx, ty, tx + th, ty + th, fill=track_color, outline="")
        self.create_oval(tx + tw - th, ty, tx + tw, ty + th, fill=track_color, outline="")
        self.create_rectangle(tx + r, ty, tx + tw - r, ty + th, fill=track_color, outline="")
        kx = (tx + tw - th + 3) if on else (tx + 3)
        self.create_oval(kx, ty + 3, kx + th - 6, ty + th - 3, fill="#ffffff", outline="")


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
        root.minsize(960, 560)
        _apply_dark_titlebar(root)
        _configure_ttk_styles(root)

        self.cameras: list[CameraInfo] = []
        self.stream: CameraStream | None = None
        self.vcam: VirtualCamera | None = None
        self.pipeline = Pipeline()
        self.mirror = self.pipeline.add(MirrorProcessor(enabled=True))
        self._build_pipeline()

        self._latest_preview = None
        self._frame_seq = 0
        self._shown_seq = -1
        self._last_preview_size = (0, 0)
        self._out_lock = threading.Lock()
        self._worker_running = False
        self._worker: threading.Thread | None = None
        self._fps = 0.0
        self._preferred_camera: str = ""

        self._load_settings()
        self._build_ui()
        self.refresh_cameras()
        self._update_preview()

    # ------------------------------------------------------------------ setup
    def _build_pipeline(self) -> None:
        self.enhancer = self.pipeline.add(AutoEnhancer(enabled=True))
        self.background = self.pipeline.add(BackgroundProcessor(enabled=True))

    def _card(self, parent: tk.Widget, title: str) -> tk.Frame:
        outer = tk.Frame(parent, bg=BORDER, padx=1, pady=1)
        outer.pack(fill="x", pady=(0, 8))
        inner = tk.Frame(outer, bg=BG_CARD, padx=12, pady=10)
        inner.pack(fill="x")
        tk.Label(inner, text=title.upper(), bg=BG_CARD, fg=ACCENT_HOVER,
                 font=("Segoe UI Semibold", 8)).pack(anchor="w", pady=(0, 5))
        return inner

    def _build_ui(self) -> None:
        # 1) Top Studio Header Bar
        header = tk.Frame(self.root, bg=BG_PANEL, padx=16, pady=10,
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
        self.live_badge = tk.Label(hdr_right, text="● STARTING...", bg=BG_INPUT, fg=TEXT_DIM,
                                   font=("Segoe UI Semibold", 8), padx=10, pady=4)
        self.live_badge.pack(side="left", padx=(0, 12))
        self.status = tk.StringVar(value="Ready")
        tk.Label(hdr_right, textvariable=self.status, bg=BG_PANEL, fg=TEXT_DIM,
                 font=FONT_BODY).pack(side="left")

        # 2) Main Body
        body = tk.Frame(self.root, bg=BG_APP, padx=14, pady=14)
        body.pack(fill="both", expand=True)

        # Right Sidebar (Clean, Minimal, Automatic Controls)
        sidebar = tk.Frame(body, bg=BG_APP, width=320)
        sidebar.pack(side="right", fill="y", padx=(14, 0))
        sidebar.pack_propagate(False)

        # Card 1: Camera Source
        src_card = self._card(sidebar, "1. Camera Source")
        self.cam_var = tk.StringVar()
        self.cam_combo = ttk.Combobox(src_card, textvariable=self.cam_var,
                                      state="readonly", style="Dark.TCombobox")
        self.cam_combo.pack(fill="x", pady=(0, 8))
        self.cam_combo.bind("<<ComboboxSelected>>", lambda e: self.start_camera())
        FlatButton(src_card, text="↻  Refresh Camera List", command=self.refresh_cameras,
                   bg=BG_INPUT, hover=BG_HOVER, fg=TEXT_DIM, font=FONT_SMALL, pady=5).pack(fill="x")

        # Card 2: Automatic AI Visual Enhance
        enh_card = self._card(sidebar, "2. Auto Visual Enhance")
        self.enh_var = tk.BooleanVar(value=self.enhancer.enabled)
        ModernSwitch(enh_card, "AI Auto Visual Enhance", self.enh_var,
                     command=lambda: setattr(self.enhancer, "enabled", self.enh_var.get()),
                     bg=BG_CARD, width=285).pack(fill="x")
        tk.Label(enh_card,
                 text="Otomatis mencerahkan wajah di ruangan gelap, menyeimbangkan warna, dan menjaga FPS tetap penuh.",
                 bg=BG_CARD, fg=TEXT_DIM, font=FONT_SMALL, justify="left",
                 wraplength=285).pack(anchor="w", pady=(5, 0))

        # Card 3: Erase Background (PNG Cutout / Virtual Green Screen)
        bg_card = self._card(sidebar, "3. Erase Background (No Background)")
        self.bg_enabled_var = tk.BooleanVar(value=self.background.enabled)
        ModernSwitch(bg_card, "Erase Background (PNG)", self.bg_enabled_var,
                     command=self._on_toggle_bg, bg=BG_CARD, width=285).pack(fill="x")
        tk.Label(bg_card,
                 text="Hapus latar belakang otomatis tanpa kain green screen (hanya menampilkan Anda seperti PNG transparan).",
                 bg=BG_CARD, fg=TEXT_DIM, font=FONT_SMALL, justify="left",
                 wraplength=285).pack(anchor="w", pady=(5, 8))

        tk.Label(bg_card, text="Mode Output ke Streamlabs / OBS:", bg=BG_CARD,
                 fg=TEXT_DIM, font=FONT_SMALL).pack(anchor="w", pady=(0, 4))
        seg = tk.Frame(bg_card, bg=BG_INPUT, padx=2, pady=2)
        seg.pack(fill="x")
        if self.background.mode not in ("cutout", "greenscreen", "blur"):
            self.background.mode = "cutout"
        self.bg_mode = tk.StringVar(value=self.background.mode)
        self._mode_btns: dict[str, FlatButton] = {}
        for label, val in (("PNG Cutout", "cutout"), ("Green Screen", "greenscreen"), ("Blur", "blur")):
            btn = FlatButton(seg, text=label, command=lambda m=val: self._select_bg_mode(m),
                             bg=ACCENT if self.background.mode == val else BG_INPUT,
                             hover=ACCENT_HOVER if self.background.mode == val else BG_HOVER,
                             font=FONT_SMALL, pady=5, padx=4)
            btn.pack(side="left", fill="x", expand=True, padx=1)
            self._mode_btns[val] = btn
        self._refresh_mode_buttons()

        # Card 4: Auto Virtual Camera Status (Docked at bottom)
        vcam_outer = tk.Frame(sidebar, bg=BORDER, padx=1, pady=1)
        vcam_outer.pack(side="bottom", fill="x")
        vcam_card = tk.Frame(vcam_outer, bg=BG_CARD, padx=12, pady=10)
        vcam_card.pack(fill="x")
        tk.Label(vcam_card, text="VIRTUAL CAMERA (AUTO-START)", bg=BG_CARD, fg=ACCENT_HOVER,
                 font=("Segoe UI Semibold", 8)).pack(anchor="w", pady=(0, 4))
        self.vcam_info = tk.Label(
            vcam_card,
            text="Virtual camera otomatis aktif saat aplikasi dibuka. Pilih 'AlapCamera' di Streamlabs / OBS.",
            bg=BG_CARD, fg=TEXT_DIM, font=FONT_SMALL, justify="left", wraplength=285,
        )
        self.vcam_info.pack(anchor="w")
        self.drv_btn = FlatButton(vcam_card, text="⚡ Install 'AlapCamera' Driver (1x Setup)",
                                  command=self.install_vcam_driver,
                                  bg=BG_INPUT, hover=BG_HOVER, fg=TEXT_MAIN,
                                  font=FONT_SMALL, pady=6)
        self.drv_btn.pack(fill="x", pady=(8, 0))

        # Left Video Stage
        stage = tk.Frame(body, bg=BORDER, padx=1, pady=1)
        stage.pack(side="left", fill="both", expand=True)
        stage_in = tk.Frame(stage, bg="#08090d")
        stage_in.pack(fill="both", expand=True)

        bar = tk.Frame(stage_in, bg=BG_PANEL, padx=14, pady=8)
        bar.pack(side="bottom", fill="x")
        self.mirror_var = tk.BooleanVar(value=self.mirror.enabled)
        ModernSwitch(bar, "Mirror Video", self.mirror_var,
                     command=lambda: setattr(self.mirror, "enabled", self.mirror_var.get()),
                     bg=BG_PANEL, width=135).pack(side="left")
        self.split_var = tk.BooleanVar(value=False)
        ModernSwitch(bar, "Split Compare (Before / After)", self.split_var,
                     bg=BG_PANEL, width=260).pack(side="left", padx=(24, 0))

        self.preview_box = tk.Frame(stage_in, bg="#08090d", width=PREVIEW_W, height=PREVIEW_H)
        self.preview_box.pack(fill="both", expand=True)
        self.preview_box.pack_propagate(False)
        self.preview = tk.Label(self.preview_box, text="Scanning cameras...",
                                bg="#08090d", fg=TEXT_DIM, font=FONT_TITLE)
        self.preview.pack(fill="both", expand=True)

    def _on_toggle_bg(self) -> None:
        self.background.enabled = self.bg_enabled_var.get()

    def _refresh_mode_buttons(self) -> None:
        cur = self.bg_mode.get()
        for val, btn in self._mode_btns.items():
            if val == cur:
                btn.set_colors(ACCENT, ACCENT_HOVER, "#ffffff")
            else:
                btn.set_colors(BG_INPUT, BG_HOVER, TEXT_DIM)

    def _select_bg_mode(self, mode: str) -> None:
        self.bg_mode.set(mode)
        self.background.mode = mode
        self.background.enabled = True
        self.bg_enabled_var.set(True)
        self._refresh_mode_buttons()

    def install_vcam_driver(self) -> None:
        script = Path(__file__).resolve().parent.parent / "scripts" / "install_virtualcam.ps1"
        if not script.exists():
            messagebox.showerror("Virtual camera", f"Installer not found:\n{script}")
            return
        subprocess.Popen(["powershell", "-ExecutionPolicy", "Bypass", "-File", str(script)])
        messagebox.showinfo("Virtual camera",
                            "Klik Yes pada jendela Admin untuk memasang driver 'AlapCamera'.\n"
                            "Setelah selesai, kamera virtual akan langsung aktif otomatis.")

    # --------------------------------------------------------------- cameras
    def refresh_cameras(self) -> None:
        self.status.set("Scanning cameras...")
        self.stop_vcam()
        self.stop_camera()
        with self._out_lock:
            self._latest_preview = None
        self.preview.configure(image="", text="Scanning cameras...")
        self.preview.image = None

        def scan():
            cams = list_cameras()
            try:
                self.root.after(0, lambda: self._on_cameras(cams))
            except Exception:
                pass

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
            self.start_camera()
        else:
            self.status.set("No camera found")
            if self._latest_preview is None:
                self.preview.configure(text="No camera found")

    def start_camera(self) -> None:
        sel = self.cam_combo.current()
        if sel < 0 or sel >= len(self.cameras):
            return
        self.stop_vcam()
        self.stop_camera()
        cam_info = self.cameras[sel]
        try:
            # Open at camera's native resolution so USB webcams run at full 30 FPS
            self.stream = CameraStream(cam_info.index).start()
        except RuntimeError as exc:
            messagebox.showerror("Camera", str(exc))
            self.stream = None
            return
        # Automatically start virtual camera without asking user to click Start
        self._auto_start_vcam()
        self._worker_running = True
        self._worker = threading.Thread(target=self._process_loop, daemon=True)
        self._worker.start()

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
            frame = self.stream.wait_read(timeout=0.05)
            if frame is None:
                continue
            try:
                out = self.pipeline.process(frame)
            except Exception as exc:
                print("pipeline error:", exc)
                out = frame
            if self.vcam is not None:
                try:
                    self.vcam.send(out)
                except Exception:
                    pass

            # Build preview frame (shows transparent PNG checkerboard when in cutout mode)
            if self.background.enabled and self.background.mode == "cutout":
                base = cv2.flip(frame, 1) if self.mirror.enabled else frame
                if self.enhancer.enabled:
                    base = cv2.LUT(base, self.enhancer._lut)
                prev = self.background.render_preview(base, out)
            else:
                prev = out

            if self.split_var.get():
                raw = cv2.flip(frame, 1) if self.mirror.enabled else frame
                if raw.shape == prev.shape:
                    prev = _make_split(raw, prev)

            with self._out_lock:
                self._latest_preview = prev
                self._frame_seq += 1

            now = time.perf_counter()
            dt = now - last
            last = now
            if 0.001 < dt < 1.0:
                inst = 1.0 / dt
                self._fps = inst if self._fps <= 1.0 else (0.85 * self._fps + 0.15 * inst)

    def _update_preview(self) -> None:
        bw = self.preview_box.winfo_width()
        bh = self.preview_box.winfo_height()
        if bw < 200 or bh < 150:
            bw, bh = PREVIEW_W, PREVIEW_H
        size_changed = (bw, bh) != self._last_preview_size
        with self._out_lock:
            seq = self._frame_seq
            frame = self._latest_preview if (seq != self._shown_seq or size_changed) else None
            if frame is not None:
                self._shown_seq = seq
                self._last_preview_size = (bw, bh)
        if frame is not None:
            h, w = frame.shape[:2]
            scale = min(bw / w, bh / h)
            small = cv2.resize(frame, (max(1, int(w * scale)), max(1, int(h * scale))),
                               interpolation=cv2.INTER_NEAREST)
            img = ImageTk.PhotoImage(Image.fromarray(cv2.cvtColor(small, cv2.COLOR_BGR2RGB)))
            self.preview.configure(image=img, text="")
            self.preview.image = img
            face_tag = "  ·  AI Enhance ✓" if self.enhancer.enabled else ""
            bg_tag = "  ·  BG Cutout ✓" if self.background.enabled else ""
            self.status.set(f"{w}×{h}  ·  {self._fps:4.1f} FPS{face_tag}{bg_tag}")
        self.root.after(15, self._update_preview)

    # --------------------------------------------------------- virtual cam
    def _auto_start_vcam(self) -> None:
        if self.stream is None:
            return
        w, h = self.stream.size
        vcam = VirtualCamera(w, h, 30)
        try:
            vcam.start()
            self.vcam = vcam
            self.live_badge.configure(text=f"● LIVE · {vcam.device_name}", bg="#064e3b", fg="#34d399")
            self.vcam_info.configure(
                text=f"Aktif mengirim video ke '{vcam.device_name}' ({w}×{h}).\n"
                     f"Langsung pilih '{vcam.device_name}' di Streamlabs / OBS.",
                fg="#34d399",
            )
        except Exception:
            self.vcam = None
            self.live_badge.configure(text="● DRIVER BELUM TERPASANG", bg="#3f1d24", fg="#f87171")
            self.vcam_info.configure(
                text="Driver kamera virtual belum terpasang di Windows.\n"
                     "Klik tombol di bawah sekali saja untuk mengaktifkan.",
                fg="#f87171",
            )

    def stop_vcam(self) -> None:
        if self.vcam is not None:
            self.vcam.stop()
            self.vcam = None

    # -------------------------------------------------------------- settings
    def _load_settings(self) -> None:
        if not SETTINGS_FILE.exists():
            return
        try:
            data = json.loads(SETTINGS_FILE.read_text(encoding="utf-8"))
        except Exception:
            return
        self._preferred_camera = str(data.get("camera", ""))
        if "mirror" in data:
            self.mirror.enabled = bool(data["mirror"])
        enh = data.get("enhance", {})
        if "enabled" in enh:
            self.enhancer.enabled = bool(enh["enabled"])
        bg = data.get("background", {})
        if "enabled" in bg:
            self.background.enabled = bool(bg["enabled"])
        if bg.get("mode") in ("cutout", "greenscreen", "blur"):
            self.background.mode = bg["mode"]

    def _save_settings(self) -> None:
        sel = self.cam_combo.current()
        cam_name = self.cameras[sel].name if 0 <= sel < len(self.cameras) else self._preferred_camera
        data = {
            "camera": cam_name,
            "mirror": self.mirror.enabled,
            "enhance": {"enabled": self.enhancer.enabled},
            "background": {
                "enabled": self.background.enabled,
                "mode": self.background.mode,
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
    cv2.putText(out, "ORIGINAL", (14, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.65, (220, 220, 220), 2, cv2.LINE_AA)
    cv2.putText(out, "ENHANCED", (mid + 14, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.65, (129, 140, 248), 2, cv2.LINE_AA)
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
        padding=5,
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
    root.geometry("1180x730")
    AlapCameraApp(root)
    root.mainloop()
