"""Tkinter desktop UI for AlapCamera."""
from __future__ import annotations

import sys
import threading
import time
import tkinter as tk
from tkinter import colorchooser, filedialog, messagebox, ttk

import cv2
from PIL import Image, ImageTk

from . import __version__
from .background import BackgroundProcessor
from .camera import CameraInfo, CameraStream, default_camera, list_cameras
from .enhance import AutoEnhancer
from .pipeline import MirrorProcessor, Pipeline
from .virtualcam import VirtualCamera

PREVIEW_W, PREVIEW_H = 640, 360
RESOLUTIONS = {"1280x720": (1280, 720), "1920x1080": (1920, 1080), "640x480": (640, 480)}


class AlapCameraApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        root.title(f"AlapCamera v{__version__}")
        root.protocol("WM_DELETE_WINDOW", self.on_close)
        root.minsize(980, 520)

        self.cameras: list[CameraInfo] = []
        self.stream: CameraStream | None = None
        self.vcam: VirtualCamera | None = None
        self.pipeline = Pipeline()
        self.mirror = self.pipeline.add(MirrorProcessor(enabled=True))
        self._build_pipeline()

        self._latest_output = None
        self._out_lock = threading.Lock()
        self._worker_running = False
        self._worker: threading.Thread | None = None
        self._fps = 0.0

        self._build_ui()
        self.refresh_cameras()
        self._update_preview()

    # ------------------------------------------------------------------ setup
    def _build_pipeline(self) -> None:
        """Register processors (order matters)."""
        self.enhancer = self.pipeline.add(AutoEnhancer(enabled=True))
        self.background = self.pipeline.add(BackgroundProcessor(enabled=False))

    def _build_ui(self) -> None:
        main = ttk.Frame(self.root, padding=8)
        main.pack(fill="both", expand=True)

        # side panel is packed first so it always keeps its width
        self.side = ttk.Frame(main, padding=(10, 0))
        self.side.pack(side="right", fill="y")

        left = ttk.Frame(main)
        left.pack(side="left", fill="both", expand=True)
        self.status = tk.StringVar(value="Ready")
        ttk.Label(left, textvariable=self.status, anchor="w").pack(side="bottom", fill="x", pady=(6, 0))
        self.preview_box = tk.Frame(left, bg="#111111", width=PREVIEW_W, height=PREVIEW_H)
        self.preview_box.pack(fill="both", expand=True)
        self.preview_box.pack_propagate(False)
        self.preview = tk.Label(self.preview_box, text="No camera", bg="#111111", fg="#bbbbbb")
        self.preview.pack(fill="both", expand=True)

        src = ttk.LabelFrame(self.side, text="Source camera", padding=8)
        src.pack(fill="x")
        self.cam_var = tk.StringVar()
        self.cam_combo = ttk.Combobox(src, textvariable=self.cam_var, state="readonly", width=32)
        self.cam_combo.pack(fill="x")
        self.cam_combo.bind("<<ComboboxSelected>>", lambda e: self.start_camera())
        row = ttk.Frame(src)
        row.pack(fill="x", pady=(6, 0))
        ttk.Label(row, text="Resolution").pack(side="left")
        self.res_var = tk.StringVar(value="1280x720")
        res = ttk.Combobox(row, textvariable=self.res_var, values=list(RESOLUTIONS),
                           state="readonly", width=12)
        res.pack(side="right")
        res.bind("<<ComboboxSelected>>", lambda e: self.start_camera())
        ttk.Button(src, text="Refresh cameras", command=self.refresh_cameras).pack(fill="x", pady=(6, 0))
        self.mirror_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(src, text="Mirror", variable=self.mirror_var,
                        command=lambda: setattr(self.mirror, "enabled", self.mirror_var.get())
                        ).pack(anchor="w", pady=(6, 0))

        self._build_feature_panels(self.side)

        out = ttk.LabelFrame(self.side, text="Virtual camera output", padding=8)
        out.pack(fill="x", side="bottom", pady=(8, 0))
        self.vcam_btn = ttk.Button(out, text="Start virtual camera", command=self.toggle_vcam)
        self.vcam_btn.pack(fill="x")
        ttk.Label(out, text="Select it in OBS / Streamlabs / Zoom as a\nvideo capture device.",
                  foreground="gray").pack(anchor="w", pady=(4, 0))

    def _build_feature_panels(self, parent: ttk.Frame) -> None:
        """Feature-specific control panels."""
        self._build_enhance_panel(parent)
        self._build_background_panel(parent)

    def _build_background_panel(self, parent: ttk.Frame) -> None:
        box = ttk.LabelFrame(parent, text="AI background", padding=8)
        box.pack(fill="x", pady=(8, 0))
        b = self.background
        self.bg_enabled_var = self._check(box, "Enable background effect", b, "enabled")

        modes = ttk.Frame(box)
        modes.pack(fill="x", pady=(2, 2))
        self.bg_mode = tk.StringVar(value=b.mode)
        for text, val in (("Blur", "blur"), ("Remove", "remove"), ("Image", "image")):
            ttk.Radiobutton(modes, text=text, value=val, variable=self.bg_mode,
                            command=self._on_bg_mode).pack(side="left", padx=(0, 8))

        row = ttk.Frame(box)
        row.pack(fill="x", pady=1)
        ttk.Label(row, text="AI model", width=12).pack(side="left")
        q = tk.StringVar(value=b.quality)
        cb = ttk.Combobox(row, textvariable=q, values=["fast", "quality"], state="readonly", width=10)
        cb.pack(side="left")
        cb.bind("<<ComboboxSelected>>", lambda e: setattr(b, "quality", q.get()))

        self._slider(box, "Blur amount", b, "blur_strength")
        self._slider(box, "Edge softness", b, "edge_softness")
        self._slider(box, "Cut-off", b, "threshold", 0.2, 0.8)

        row = ttk.Frame(box)
        row.pack(fill="x", pady=(4, 0))
        ttk.Button(row, text="Remove color...", command=self._pick_bg_color).pack(side="left", fill="x", expand=True)
        ttk.Button(row, text="Image...", command=self._pick_bg_image).pack(side="left", fill="x", expand=True, padx=(4, 0))

    def _on_bg_mode(self) -> None:
        mode = self.bg_mode.get()
        if mode == "image" and self.background._bg_image is None:
            if not self._pick_bg_image():
                self.bg_mode.set(self.background.mode)
                return
        self.background.mode = mode
        self.background.enabled = True
        self.bg_enabled_var.set(True)

    def _pick_bg_color(self) -> None:
        b, g, r = self.background.color
        res = colorchooser.askcolor(color=f"#{r:02x}{g:02x}{b:02x}", title="Background color")
        if res and res[0]:
            r, g, b = (int(c) for c in res[0])
            self.background.color = (b, g, r)
            self.background.mode = "remove"
            self.bg_mode.set("remove")

    def _pick_bg_image(self) -> bool:
        path = filedialog.askopenfilename(
            title="Background image",
            filetypes=[("Images", "*.png *.jpg *.jpeg *.bmp *.webp"), ("All files", "*.*")])
        if not path:
            return False
        if not self.background.set_background_image(path):
            messagebox.showerror("Background", "Could not read that image.")
            return False
        self.background.mode = "image"
        self.bg_mode.set("image")
        return True

    # helpers to create bound widgets
    def _check(self, parent, text, obj, attr) -> tk.BooleanVar:
        var = tk.BooleanVar(value=getattr(obj, attr))
        ttk.Checkbutton(parent, text=text, variable=var,
                        command=lambda: setattr(obj, attr, var.get())).pack(anchor="w")
        return var

    def _slider(self, parent, text, obj, attr, lo=0.0, hi=1.0) -> None:
        row = ttk.Frame(parent)
        row.pack(fill="x", pady=1)
        ttk.Label(row, text=text, width=12).pack(side="left")
        var = tk.DoubleVar(value=getattr(obj, attr))
        ttk.Scale(row, from_=lo, to=hi, variable=var,
                  command=lambda v: setattr(obj, attr, float(v))).pack(side="left", fill="x", expand=True)

    def _build_enhance_panel(self, parent: ttk.Frame) -> None:
        box = ttk.LabelFrame(parent, text="Auto visual enhance", padding=8)
        box.pack(fill="x", pady=(8, 0))
        e = self.enhancer
        self._check(box, "Enable auto enhance", e, "enabled")
        self._slider(box, "Strength", e, "strength")
        self._slider(box, "Brightness", e, "target_brightness", 0.25, 0.75)
        self._slider(box, "Sharpen", e, "sharpen")
        grid = ttk.Frame(box)
        grid.pack(fill="x")
        left, right = ttk.Frame(grid), ttk.Frame(grid)
        left.pack(side="left", fill="x", expand=True)
        right.pack(side="left", fill="x", expand=True)
        self._check(left, "Auto exposure", e, "auto_exposure")
        self._check(left, "Face priority", e, "face_priority")
        self._check(left, "White balance", e, "white_balance")
        self._check(right, "Low-light boost", e, "local_contrast")
        self._check(right, "Denoise", e, "denoise")

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
            self.cam_combo.current(default_camera(self.cameras))
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
            except Exception as exc:  # keep streaming even if a filter fails
                print("pipeline error:", exc)
                out = frame
            if self.vcam is not None:
                try:
                    self.vcam.send(out)
                except Exception as exc:
                    print("virtual camera error:", exc)
            with self._out_lock:
                self._latest_output = out
            now = time.perf_counter()
            dt = now - last
            last = now
            if dt > 0:
                self._fps = 0.9 * self._fps + 0.1 * (1.0 / dt)
            # pace roughly to 30 fps (vcam.send already sleeps when active)
            if self.vcam is None:
                time.sleep(max(0.0, 1 / 30 - dt))

    def _update_preview(self) -> None:
        with self._out_lock:
            frame = self._latest_output
        if frame is not None:
            h, w = frame.shape[:2]
            bw = max(160, self.preview_box.winfo_width())
            bh = max(90, self.preview_box.winfo_height())
            scale = min(bw / w, bh / h)
            small = cv2.resize(frame, (max(1, int(w * scale)), max(1, int(h * scale))),
                               interpolation=cv2.INTER_AREA)
            img = ImageTk.PhotoImage(Image.fromarray(cv2.cvtColor(small, cv2.COLOR_BGR2RGB)))
            self.preview.configure(image=img, text="")
            self.preview.image = img
            vc = f" | Virtual cam: {self.vcam.device_name}" if self.vcam else ""
            self.status.set(f"{w}x{h} @ {self._fps:4.1f} fps{vc}")
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
            messagebox.showerror(
                "Virtual camera",
                f"Could not start virtual camera:\n{exc}\n\n"
                "On Windows install OBS Studio (it ships the 'OBS Virtual Camera' "
                "driver) or Unity Capture, then try again.")
            return
        self.vcam = vcam
        self.vcam_btn.configure(text="Stop virtual camera")

    def stop_vcam(self) -> None:
        if self.vcam is not None:
            self.vcam.stop()
            self.vcam = None
        if hasattr(self, "vcam_btn"):
            self.vcam_btn.configure(text="Start virtual camera")

    # ----------------------------------------------------------------- close
    def on_close(self) -> None:
        self.stop_vcam()
        self.stop_camera()
        self.pipeline.close()
        self.root.destroy()


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
    try:
        ttk.Style().theme_use("vista")
    except tk.TclError:
        pass
    root.geometry("1200x760")
    AlapCameraApp(root)
    root.mainloop()
