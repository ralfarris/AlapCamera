# AlapCamera

Aplikasi **virtual camera** untuk Windows (mirip Camo Camera): pilih kamera fisik
mana saja yang ada di perangkat, olah gambarnya, lalu kirim hasilnya ke sebuah
kamera virtual yang bisa dipilih di **OBS / Streamlabs / Zoom / Google Meet / Discord**.

## Fitur

| Fitur | Status |
|---|---|
| Pilih kamera sumber + resolusi, mirror | ✅ |
| Output ke virtual camera (OBS Virtual Camera / Unity Capture) | ✅ |

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
  app.py         # UI desktop (Tkinter)
main.py          # entry point
```
