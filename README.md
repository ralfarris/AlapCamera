# AlapCamera

Aplikasi **virtual camera** untuk Windows: pilih kamera fisik
mana saja yang ada di perangkat, olah gambarnya, lalu kirim hasilnya ke sebuah
kamera virtual yang bisa dipilih di **OBS / Streamlabs / Zoom / Google Meet / Discord**.

## Fitur

| Fitur | Status |
|---|---|
| Pilih kamera sumber + resolusi, mirror | ✅ |
| Output ke virtual camera bernama **AlapCamera** (fallback: OBS Virtual Camera) | ✅ |
| **Auto visual enhance** – auto exposure berbasis wajah (AI), low-light boost, auto white balance, denoise, sharpen | ✅ |
| **AI background** – blur, hapus (warna solid / green screen), ganti gambar | ✅ |
| **Compare (Before/After)** – mode split-screen untuk melihat perbandingan sebelum & sesudah secara langsung | ✅ |
| **Auto-save settings** – pengaturan kamera, resolusi, enhance, dan background otomatis tersimpan | ✅ |

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

### AI background (blur / remove / ganti gambar)

Juga **tanpa dataset** – memakai model segmentasi orang pre-trained dari Google
MediaPipe:

| Model | Kecepatan | Keterangan |
|---|---|---|
| `fast` – selfie_segmenter | sangat cepat | cocok untuk laptop biasa |
| `quality` – selfie_multiclass_256x256 | lebih berat | tepi rambut lebih rapi |

Mode:
- **Blur** – background diburamkan (efek bokeh).
- **Remove** – background diganti warna solid (default hijau → mudah di-*chroma key*
  di OBS/Streamlabs untuk efek transparan).
- **Image** – background diganti gambar pilihan.

Segmentasi berjalan di thread terpisah sehingga FPS output tetap lancar. Mask
dihaluskan antar-frame, diperhalus dengan *guided filter* mengikuti tepi gambar,
dan di-*feather* agar tepi natural.

## Persyaratan

- Windows 10/11, Python 3.10+
- Driver virtual camera — pilih salah satu:
  - **Direkomendasikan:** jalankan installer bawaan, akan muncul webcam bernama
    **"AlapCamera"** di Streamlabs/OBS/Zoom:
    ```powershell
    powershell -ExecutionPolicy Bypass -File scripts\install_virtualcam.ps1
    ```
    (bisa juga lewat tombol *Install 'AlapCamera' device...* di aplikasi; butuh izin admin.
    Hapus dengan parameter `-Uninstall`.) Memakai filter open-source
    [Unity Capture](https://github.com/schellingb/UnityCapture) (MIT).
  - Atau install **OBS Studio** (driver *OBS Virtual Camera*).

## Instalasi & menjalankan

```bash
pip install -r requirements.txt
python main.py
```

Atau di Windows cukup klik dua kali **`run.bat`**.

1. Pilih kamera sumber di panel kanan (nama asli perangkat tampil, mis. webcam USB atau kamera eksternal).
2. Atur *Auto visual enhance* dan *AI background* sesuai selera.
3. Klik **Start virtual camera**.
4. Di Streamlabs/OBS tambahkan source *Video Capture Device* dan pilih
   **AlapCamera** (atau *OBS Virtual Camera*).

## Struktur proyek

```
alapcamera/
  camera.py      # deteksi & capture kamera fisik (thread terpisah)
  virtualcam.py  # output ke virtual camera (pyvirtualcam)
  pipeline.py    # rantai pemrosesan frame (filter/efek)
  enhance.py     # auto visual enhance (exposure, low-light, WB, denoise)
  background.py  # AI background blur / remove / replace
  models.py      # unduh & cache model AI pre-trained
  app.py         # UI desktop (Tkinter)
scripts/
  install_virtualcam.ps1  # pasang webcam virtual "AlapCamera"
main.py          # entry point
```
