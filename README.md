# AlapCamera

Aplikasi **virtual camera & mic booster** super ringan untuk Windows (~857 KB single-file `.exe` termasuk model AI *Selfie Segmentation*, tanpa Python & tanpa Chromium/Electron). Hubungkan kamera fisik mana saja, tingkatkan pencahayaan & warna otomatis di ruangan gelap, hapus atau **blur** latar belakang otomatis tanpa kain *green screen*, perkuat volume mic langsung hingga `200%`, lalu kirim ke **Streamlabs Desktop / OBS / Zoom / Google Meet / Discord**.

[![Download Latest Release](https://img.shields.io/github/v/release/ralfarris/AlapCamera?label=Download%20AlapCamera&style=for-the-badge&color=7c5cff&cacheSeconds=60)](https://github.com/ralfarris/AlapCamera/releases/latest)

## Cara Menggunakan (Untuk Pengguna Langsung)
1. Buka halaman [**GitHub Releases**](https://github.com/ralfarris/AlapCamera/releases/latest) (atau klik tombol ungu di atas).
2. Download file **`AlapCamera.exe`**.
3. Langsung jalankan **`AlapCamera.exe`** *(klik **Yes** jika muncul jendela izin Admin agar driver virtual camera `AlapCamera` terbaca oleh Streamlabs/OBS)*.
4. Buka **Streamlabs Desktop / OBS** → tambahkan atau buka sumber **Video Capture Device** → pada bagian **Device**, pilih **`AlapCamera`**.
5. Pilih mode background di Card **`3. Erase / Blur Background`**:
   - **`PNG Cutout`**: Klik kanan source **`AlapCamera`** di Streamlabs/OBS → **Filters** → **Edit Filters** → **`+` (Add Filter)** → pilih **Chroma Key** *(hijau)* agar background langsung transparan.
   - **`Blur`**: Latar belakang langsung blur bokeh halus otomatis di Streamlabs/OBS *(matikan filter Chroma Key jika sebelumnya aktif)*.
6. *(Opsional untuk Mic Booster 0%–200%)* Di Card **`4. Mic Booster`**, pilih **Mic Input** kamu (misalnya `Microphone Array (Realtek(R) Audio)`), nyalakan **Mic Input Booster**, dan geser slider **`0%–200%`**. Di Streamlabs cukup pilih mic yang sama (`0%` CPU, tanpa perlu virtual cable tambahan).
   > **Catatan:** Jangan memilih kamera fisik secara langsung di Streamlabs supaya tidak bentrok — biarkan **AlapCamera** yang mengambil kamera fisik dan Streamlabs mengambil **`AlapCamera`**.

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
- **Single-File Portable (`AlapCamera.exe`)** – cukup 1 file `.exe` (~857 KB), langsung jalan tanpa install Python atau ekstrak ZIP.
- **Native DirectShow Capture (FPS Penuh)** – menangkap kamera fisik pada resolusi & FPS asli tanpa lag, dilengkapi kalibrasi sensor otomatis agar ruangan gelap tidak pecah.
- **Auto-Start Virtual Camera (`AlapCamera`)** – driver otomatis terdaftar di sistem Windows (`HKLM`) dan langsung terdeteksi oleh Streamlabs Desktop, OBS, Zoom, dan Discord.
- **Auto Visual Enhance** – otomatis mencerahkan wajah di ruangan gelap/backlight dan menyeimbangkan warna secara real-time.
- **Erase / Blur Background (`PNG Cutout` & `Blur`)** – hapus atau blur latar belakang otomatis tanpa kain green screen dengan akurasi AI ONNX (*15 Hz motion-adaptive mask, bilinear edge upsampling, 2-pass bokeh blur, 30 FPS output*).
- **Mic Booster (0% - 200%)** – perkuat *hardware gain* mikrofon Windows CoreAudio secara langsung hingga `+30 dB` (`0%` CPU baik saat ON maupun OFF, tanpa perlu VB-Cable).
- **Split Compare (Before/After)** – mode layar terbagi untuk membandingkan kamera asli dengan hasil olahan secara langsung.
- **Auto-Save Settings** – pilihan kamera, mode background, mic, dan pengaturan efek otomatis tersimpan.

## Struktur Proyek
```text
src/
  Program.cs         # entry point & DPI awareness
  MainForm.cs        # UI studio modern dark mode & preview real-time
  Theme.cs           # komponen UI custom (StudioCard, ToggleSwitch, FlatBtn)
  DirectShow.cs      # capture kamera fisik & kalibrasi sensor (COM DirectShow)
  VirtualCam.cs      # auto-registrasi driver HKLM & output shared memory
  FrameProcessor.cs  # auto visual enhance, PNG cutout & bokeh blur (unsafe C#)
  OnnxSeg.cs         # inferensi AI Selfie Segmentation via onnxruntime.dll
  AudioBooster.cs    # direct Windows CoreAudio mic gain booster 0%-200%
  app.manifest       # manifest UAC Administrator & High-DPI
assets/
  alapcamera.ico
  UnityCaptureFilter64.dll / UnityCaptureFilter32.dll
build.ps1 / build.bat
.github/workflows/release.yml
```
