# AlapCamera

Aplikasi **virtual camera** ultra-ringan untuk Windows (~382 KB single-file `.exe`, native C# / DirectShow): pilih kamera fisik mana saja yang ada di perangkat, tingkatkan visualnya secara otomatis, hapus latar belakang tanpa kain green screen, lalu kirim hasilnya ke kamera virtual **`AlapCamera`** yang langsung terdeteksi di **Streamlabs / OBS / Zoom / Google Meet / Discord**.

## Fitur

| Fitur | Status |
|---|---|
| **Single-File Portable (`AlapCamera.exe`)** – tanpa install Python, tanpa ZIP, langsung jalan | ✅ |
| **Native DirectShow Capture (FPS Penuh)** – menangkap kamera fisik pada resolusi & FPS asli | ✅ |
| **Auto-Start Virtual Camera (`AlapCamera`)** – driver otomatis terdaftar di HKLM & langsung terbaca Streamlabs/OBS | ✅ |
| **AI Auto Visual Enhance** – otomatis mencerahkan wajah di ruangan gelap/backlight & menyeimbangkan warna | ✅ |
| **Erase Background (No Background)** – hapus latar belakang otomatis (*PNG Cutout*, *Green Screen*, *Blur*) | ✅ |
| **Split Compare (Before/After)** – mode split-screen untuk melihat perbandingan sebelum & sesudah | ✅ |
| **Auto-save Settings** – pengaturan kamera, enhance, dan background otomatis tersimpan | ✅ |

## Cara Pakai (Langsung Download `.exe`)

1. Buka halaman **[Releases](https://github.com/ralfarris/AlapCamera/releases)** lalu download **`AlapCamera.exe`**.
2. Jalankan **`AlapCamera.exe`** *(klik **Yes** pada jendela Admin agar driver `AlapCamera` terbaca oleh Streamlabs Desktop / OBS)*.
3. Di **Streamlabs / OBS**, tambahkan *Video Capture Device* lalu pilih **`AlapCamera`**.

## Build dari Source (Tanpa Install SDK Tambahan)

Dikompilasi menggunakan compiler C# bawaan Windows (`.NET Framework 4.x`):

```powershell
.\build.bat
```
atau:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```
Hasil build tersedia di `dist\AlapCamera.exe`.

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
