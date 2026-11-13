# AlapCamera

Aplikasi **virtual camera** super ringan untuk Windows (~844 KB single-file `.exe` termasuk model AI *Selfie Segmentation*, tanpa Python & tanpa Chromium/Electron). Hubungkan kamera fisik mana saja, tingkatkan pencahayaan & warna otomatis di ruangan gelap, hapus latar belakang tanpa kain *green screen*, lalu kirim ke **Streamlabs Desktop / OBS / Zoom / Google Meet / Discord**.

[![Download Latest Release](https://img.shields.io/github/v/release/ralfarris/AlapCamera?label=Download%20AlapCamera&style=for-the-badge&color=7c5cff&cacheSeconds=60)](https://github.com/ralfarris/AlapCamera/releases/latest)

## Cara Menggunakan (Untuk Pengguna Langsung)
1. Buka halaman [**GitHub Releases**](https://github.com/ralfarris/AlapCamera/releases/latest) (atau klik tombol ungu di atas).
2. Download file **`AlapCamera.exe`**.
3. Langsung jalankan **`AlapCamera.exe`** *(klik **Yes** jika muncul jendela izin Admin agar driver virtual camera `AlapCamera` terbaca oleh Streamlabs/OBS)*.
4. Buka **Streamlabs Desktop / OBS** → tambahkan atau buka sumber **Video Capture Device** → pada bagian **Device**, pilih **`AlapCamera`**.
5. *(Opsional untuk mode PNG Cutout)* Klik kanan source **`AlapCamera`** di Streamlabs/OBS → **Filters** → **Edit Filters** → **`+` (Add Filter)** → pilih **Chroma Key** *(hijau)* agar background langsung transparan.
   > **Catatan:** Jangan memilih kamera fisik (misalnya *NYK NEMESIS* / webcam bawaan) secara langsung di Streamlabs supaya tidak bentrok — biarkan **AlapCamera** yang mengambil kamera fisik dan Streamlabs mengambil **`AlapCamera`**.

---

## Build dari Source Code (Developer)
Tidak perlu install SDK tambahan — dikompilasi langsung menggunakan compiler C# bawaan Windows (`.NET Framework 4.x`):

```powershell
.\build.bat
```
atau:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Hasil executable siap pakai akan dibuat di **`dist\AlapCamera.exe`**.

## Fitur
- **Single-File Portable (`AlapCamera.exe`)** – cukup 1 file `.exe` (~382 KB), langsung jalan tanpa install Python atau ekstrak ZIP.
- **Native DirectShow Capture (FPS Penuh)** – menangkap kamera fisik pada resolusi & FPS asli tanpa lag, dilengkapi kalibrasi sensor otomatis agar ruangan gelap tidak pecah.
- **Auto-Start Virtual Camera (`AlapCamera`)** – driver otomatis terdaftar di sistem Windows (`HKLM`) dan langsung terdeteksi oleh Streamlabs Desktop, OBS, Zoom, dan Discord.
- **Auto Visual Enhance** – otomatis mencerahkan wajah di ruangan gelap/backlight dan menyeimbangkan warna secara real-time.
- **Erase Background (No Background)** – hapus latar belakang otomatis tanpa kain green screen (*PNG Cutout*, *Green Screen*, *Blur*).
- **Split Compare (Before/After)** – mode layar terbagi untuk membandingkan kamera asli dengan hasil olahan secara langsung.
- **Auto-Save Settings** – pilihan kamera dan pengaturan efek otomatis tersimpan.

## Struktur Proyek
```text
src/
  Program.cs         # entry point & DPI awareness
  MainForm.cs        # UI studio modern dark mode & preview real-time
  Theme.cs           # komponen UI custom (StudioCard, ToggleSwitch, FlatBtn)
  DirectShow.cs      # capture kamera fisik & kalibrasi sensor (COM DirectShow)
  VirtualCam.cs      # auto-registrasi driver HKLM & output shared memory
  FrameProcessor.cs  # auto visual enhance & erase background (unsafe C#)
  app.manifest       # manifest UAC Administrator & High-DPI
assets/
  alapcamera.ico
  UnityCaptureFilter64.dll / UnityCaptureFilter32.dll
build.ps1 / build.bat
.github/workflows/release.yml
```
