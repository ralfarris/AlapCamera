# AlapCamera

Aplikasi **virtual camera** untuk Windows (mirip Camo Camera): pilih kamera fisik
mana saja yang ada di perangkat, olah gambarnya, lalu kirim hasilnya ke sebuah
kamera virtual yang bisa dipilih di **OBS / Streamlabs / Zoom / Google Meet / Discord**.

## Fitur

| Fitur | Status |
|---|---|
| Pilih kamera sumber + resolusi, mirror | ✅ |
| Output ke virtual camera (OBS Virtual Camera / Unity Capture) | ✅ |
| **Auto visual enhance** – auto exposure berbasis wajah (AI), low-light boost, auto white balance, denoise, sharpen | ✅ |

### Auto visual enhance

Tidak perlu dataset / training. Menggunakan model AI pre-trained
(MediaPipe BlazeFace) + algoritma computer vision klasik, real-time di CPU:

- **Face-priority auto exposure** – wajah dideteksi, kecerahan diukur di area
  wajah, lalu kurva gamma adaptif menaikkan/menurunkan exposure (dihaluskan antar
  frame agar tidak berkedip). Kamera gelap → wajah tetap terang.
- **Low-light boost** – CLAHE pada kanal luminance (kontras lokal).
- **Auto white balance** – koreksi warna gray-world.
- **Denoise** – bilateral filter, kekuatannya otomatis naik saat kondisi gelap.
- **Sharpen** – unsharp mask.

Model AI diunduh otomatis sekali saat pertama jalan ke folder `models/`.

## Persyaratan

- Python 3.10+
- Driver virtual camera. Cara termudah: install **OBS Studio** (sudah termasuk
  driver *OBS Virtual Camera*). Alternatif: [Unity Capture](https://github.com/schellingb/UnityCapture).

## Instalasi & menjalankan

```bash
pip install -r requirements.txt
python main.py
```

1. Pilih kamera sumber di panel kanan.
2. Klik **Start virtual camera**.
3. Di Streamlabs/OBS tambahkan source *Video Capture Device* dan pilih
   **OBS Virtual Camera** (atau *Unity Video Capture*).

## Struktur proyek

```
alapcamera/
  camera.py      # deteksi & capture kamera fisik (thread terpisah)
  virtualcam.py  # output ke virtual camera (pyvirtualcam)
  pipeline.py    # rantai pemrosesan frame (filter/efek)
  enhance.py     # auto visual enhance (exposure, low-light, WB, denoise)
  models.py      # unduh & cache model AI pre-trained
  app.py         # UI desktop (Tkinter)
main.py          # entry point
```
