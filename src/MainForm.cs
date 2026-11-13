using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AlapCamera
{
    sealed class MainForm : Form
    {
        const string AppVersion = "0.2.0";

        readonly FrameProcessor _proc = new FrameProcessor();
        readonly VirtualCam _vcam = new VirtualCam();
        CameraCapture _cap;
        List<CameraDevice> _devices = new List<CameraDevice>();

        readonly object _previewLock = new object();
        byte[] _previewBytes;
        int _previewW, _previewH;
        long _frameSeq, _shownSeq = -1;
        Bitmap _bmp;
        bool _isBusy;
        string _busyCamName = "";
        double _fps;
        long _lastFrameMs;
        string _preferredCam = "";

        // UI
        Panel _stage;
        Label _liveBadge, _statusLbl, _vcamInfoLbl;
        ComboBox _camCombo;
        ToggleSwitch _enhSwitch, _bgSwitch, _mirrorSwitch, _splitSwitch;
        FlatBtn _btnCutout, _btnGreen, _btnBlur;
        Timer _uiTimer;

        static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlapCamera", "settings.json");

        public MainForm()
        {
            Text = "AlapCamera Studio v" + AppVersion;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.UI(9.5f);
            MinimumSize = new Size(920, 560);
            ClientSize = new Size(1160, 720);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            LoadSettings();
            BuildUi();
            Theme.ApplyDarkTitleBar(this);

            _uiTimer = new Timer { Interval = 28 };
            _uiTimer.Tick += (s, e) => RefreshPreview();
            _uiTimer.Start();

            Shown += (s, e) =>
            {
                _vcam.Start();
                UpdateVcamBadge();
                RefreshCameras();
            };
            FormClosing += OnFormClosing;
        }

        void BuildUi()
        {
            // 1) Top Header Bar
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = Theme.S(52),
                BackColor = Theme.Panel,
                Padding = new Padding(Theme.S(16), 0, Theme.S(16), 0)
            };
            header.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Border))
                    e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
                Theme.HQ(e.Graphics);
                Theme.Fill(e.Graphics, new RectangleF(Theme.S(16), Theme.S(20), Theme.S(10), Theme.S(10)), Theme.S(5), Theme.Accent);
            };
            Controls.Add(header);

            var title = new Label
            {
                Text = "AlapCamera",
                Font = Theme.Semi(12.5f),
                ForeColor = Theme.Text,
                AutoSize = true,
                Location = new Point(Theme.S(34), Theme.S(14))
            };
            header.Controls.Add(title);

            var ver = new Label
            {
                Text = "v" + AppVersion,
                Font = Theme.UI(8f),
                ForeColor = Theme.Muted,
                BackColor = Theme.Field,
                AutoSize = true,
                Padding = new Padding(5, 2, 5, 2),
                Location = new Point(Theme.S(146), Theme.S(17))
            };
            header.Controls.Add(ver);

            _statusLbl = new Label
            {
                Text = "Ready",
                Font = Theme.UI(9f),
                ForeColor = Theme.Muted,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                Size = new Size(Theme.S(360), Theme.S(28)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(header.Width - Theme.S(560), Theme.S(12))
            };
            header.Controls.Add(_statusLbl);

            _liveBadge = new Label
            {
                Text = "● STARTING...",
                Font = Theme.Semi(8.5f),
                ForeColor = Theme.Muted,
                BackColor = Theme.Field,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(Theme.S(175), Theme.S(26)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(header.Width - Theme.S(191), Theme.S(13))
            };
            header.Controls.Add(_liveBadge);

            // 2) Main Body
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(14)), BackColor = Theme.Bg };
            Controls.Add(body);
            body.BringToFront();

            // Right Sidebar
            var sidebar = new Panel
            {
                Dock = DockStyle.Right,
                Width = Theme.S(318),
                BackColor = Theme.Bg,
                Padding = new Padding(Theme.S(12), 0, 0, 0)
            };
            body.Controls.Add(sidebar);

            int y = 0;
            int cw = Theme.S(306);

            // Card 1: Camera Source
            var c1 = new StudioCard("1. Camera Source") { Location = new Point(Theme.S(12), y), Size = new Size(cw, Theme.S(112)) };
            _camCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Theme.Field,
                ForeColor = Theme.Text,
                FlatStyle = FlatStyle.Flat,
                Font = Theme.UI(9.5f),
                Location = new Point(Theme.S(12), Theme.S(32)),
                Width = cw - Theme.S(24)
            };
            _camCombo.SelectedIndexChanged += (s, e) => StartSelectedCamera();
            c1.Controls.Add(_camCombo);

            var refBtn = new FlatBtn("↻  Refresh Camera List")
            {
                Location = new Point(Theme.S(12), Theme.S(68)),
                Size = new Size(cw - Theme.S(24), Theme.S(30))
            };
            refBtn.Click += (s, e) => RefreshCameras();
            c1.Controls.Add(refBtn);
            sidebar.Controls.Add(c1);
            y += c1.Height + Theme.S(10);

            // Card 2: Auto Visual Enhance
            var c2 = new StudioCard("2. Auto Visual Enhance") { Location = new Point(Theme.S(12), y), Size = new Size(cw, Theme.S(102)) };
            _enhSwitch = new ToggleSwitch("AI Auto Visual Enhance", _proc.EnhanceEnabled)
            {
                Location = new Point(Theme.S(12), Theme.S(28)),
                Width = cw - Theme.S(24)
            };
            _enhSwitch.CheckedChanged += (s, e) => _proc.EnhanceEnabled = _enhSwitch.Checked;
            c2.Controls.Add(_enhSwitch);

            c2.Controls.Add(new Label
            {
                Text = "Otomatis mencerahkan wajah di ruangan gelap, menyeimbangkan warna & menjaga FPS penuh.",
                Font = Theme.UI(8f),
                ForeColor = Theme.Muted,
                Location = new Point(Theme.S(12), Theme.S(60)),
                Size = new Size(cw - Theme.S(24), Theme.S(34))
            });
            sidebar.Controls.Add(c2);
            y += c2.Height + Theme.S(10);

            // Card 3: Erase Background
            var c3 = new StudioCard("3. Erase Background (No Background)") { Location = new Point(Theme.S(12), y), Size = new Size(cw, Theme.S(154)) };
            _bgSwitch = new ToggleSwitch("Erase Background (PNG)", _proc.BgEnabled)
            {
                Location = new Point(Theme.S(12), Theme.S(28)),
                Width = cw - Theme.S(24)
            };
            _bgSwitch.CheckedChanged += (s, e) => _proc.BgEnabled = _bgSwitch.Checked;
            c3.Controls.Add(_bgSwitch);

            c3.Controls.Add(new Label
            {
                Text = "Hapus latar belakang otomatis tanpa kain green screen.",
                Font = Theme.UI(8f),
                ForeColor = Theme.Muted,
                Location = new Point(Theme.S(12), Theme.S(58)),
                Size = new Size(cw - Theme.S(24), Theme.S(20))
            });

            int bw = (cw - Theme.S(32)) / 3;
            _btnCutout = new FlatBtn("PNG Cutout") { Location = new Point(Theme.S(12), Theme.S(84)), Size = new Size(bw, Theme.S(30)) };
            _btnGreen = new FlatBtn("Green Screen") { Location = new Point(Theme.S(16) + bw, Theme.S(84)), Size = new Size(bw, Theme.S(30)) };
            _btnBlur = new FlatBtn("Blur") { Location = new Point(Theme.S(20) + bw * 2, Theme.S(84)), Size = new Size(bw, Theme.S(30)) };
            _btnCutout.Click += (s, e) => SetBgMode(BgMode.Cutout);
            _btnGreen.Click += (s, e) => SetBgMode(BgMode.GreenScreen);
            _btnBlur.Click += (s, e) => SetBgMode(BgMode.Blur);
            c3.Controls.AddRange(new Control[] { _btnCutout, _btnGreen, _btnBlur });

            var recalBtn = new FlatBtn("↺  Recalibrate Background")
            {
                Location = new Point(Theme.S(12), Theme.S(118)),
                Size = new Size(cw - Theme.S(24), Theme.S(26)),
                Font = Theme.UI(8f)
            };
            recalBtn.Click += (s, e) => _proc.ResetBackground();
            c3.Controls.Add(recalBtn);

            sidebar.Controls.Add(c3);
            UpdateBgModeButtons();

            // Card 4: Virtual Camera (Docked Bottom)
            var c4 = new StudioCard("Virtual Camera (Auto-Start)")
            {
                Dock = DockStyle.Bottom,
                Height = Theme.S(82)
            };
            _vcamInfoLbl = new Label
            {
                Text = "Mempersiapkan 'AlapCamera' untuk Streamlabs / OBS...",
                Font = Theme.UI(8.2f),
                ForeColor = Theme.Muted,
                Location = new Point(Theme.S(12), Theme.S(30)),
                Size = new Size(cw - Theme.S(24), Theme.S(44))
            };
            c4.Controls.Add(_vcamInfoLbl);
            sidebar.Controls.Add(c4);

            // Left Video Stage
            var stageWrap = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(8, 9, 13) };
            body.Controls.Add(stageWrap);
            stageWrap.BringToFront();

            var bar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = Theme.S(42),
                BackColor = Theme.Panel,
                Padding = new Padding(Theme.S(14), Theme.S(6), Theme.S(14), Theme.S(6))
            };
            bar.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Border))
                    e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
            };
            stageWrap.Controls.Add(bar);

            _mirrorSwitch = new ToggleSwitch("Mirror Video", _proc.Mirror)
            {
                Location = new Point(Theme.S(14), Theme.S(7)),
                Width = Theme.S(145)
            };
            _mirrorSwitch.CheckedChanged += (s, e) => _proc.Mirror = _mirrorSwitch.Checked;
            bar.Controls.Add(_mirrorSwitch);

            _splitSwitch = new ToggleSwitch("Split Compare (Before / After)", false)
            {
                Location = new Point(Theme.S(180), Theme.S(7)),
                Width = Theme.S(255)
            };
            bar.Controls.Add(_splitSwitch);

            _stage = new StagePanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(8, 9, 13) };
            _stage.Paint += OnStagePaint;
            stageWrap.Controls.Add(_stage);
            _stage.BringToFront();
        }

        void SetBgMode(BgMode m)
        {
            _proc.Mode = m;
            _proc.BgEnabled = true;
            _bgSwitch.Checked = true;
            UpdateBgModeButtons();
        }

        void UpdateBgModeButtons()
        {
            _btnCutout.Active = _proc.Mode == BgMode.Cutout;
            _btnGreen.Active = _proc.Mode == BgMode.GreenScreen;
            _btnBlur.Active = _proc.Mode == BgMode.Blur;
        }

        void UpdateVcamBadge()
        {
            if (VirtualCam.IsHklmRegistered() && _vcam.IsRunning)
            {
                _liveBadge.Text = "● LIVE · " + VirtualCam.DeviceName;
                _liveBadge.BackColor = Theme.GreenBg;
                _liveBadge.ForeColor = Theme.Green;
                _vcamInfoLbl.Text = "Aktif mengirim ke '" + VirtualCam.DeviceName + "'.\nPilih '" + VirtualCam.DeviceName + "' di Streamlabs / OBS / Zoom.";
                _vcamInfoLbl.ForeColor = Theme.Green;
            }
            else
            {
                _liveBadge.Text = "● DRIVER ERROR";
                _liveBadge.BackColor = Theme.DangerBg;
                _liveBadge.ForeColor = Theme.Danger;
                _vcamInfoLbl.Text = "Jalankan sebagai Administrator agar Streamlabs dapat mendeteksi 'AlapCamera'.";
                _vcamInfoLbl.ForeColor = Theme.Danger;
            }
        }

        void RefreshCameras()
        {
            StopCamera();
            _devices = CameraCapture.ListDevices();
            _camCombo.Items.Clear();
            foreach (var d in _devices) _camCombo.Items.Add(d);
            if (_devices.Count == 0)
            {
                _statusLbl.Text = "No camera found";
                return;
            }
            int idx = CameraCapture.DefaultCameraIndex(_devices);
            if (!string.IsNullOrEmpty(_preferredCam))
            {
                for (int i = 0; i < _devices.Count; i++)
                    if (_devices[i].Name == _preferredCam) { idx = i; break; }
            }
            _camCombo.SelectedIndex = idx;
        }

        void StartSelectedCamera()
        {
            int sel = _camCombo.SelectedIndex;
            if (sel < 0 || sel >= _devices.Count) return;
            StopCamera();
            var dev = _devices[sel];
            _busyCamName = dev.Name;
            _cap = new CameraCapture(dev.Index, dev.Name);
            _cap.OnFrame += OnCameraFrame;
            _cap.Start();
        }

        void StopCamera()
        {
            if (_cap != null)
            {
                _cap.OnFrame -= OnCameraFrame;
                _cap.Dispose();
                _cap = null;
            }
        }

        void OnCameraFrame(byte[] bgr24, int w, int h)
        {
            _isBusy = false;
            byte[] outVcam, outPrev;
            _proc.Process(bgr24, w, h, out outVcam, out outPrev, _splitSwitch.Checked);

            _vcam.SendBgr24(outVcam, w, h);

            lock (_previewLock)
            {
                if (_previewBytes == null || _previewBytes.Length != outPrev.Length)
                    _previewBytes = new byte[outPrev.Length];
                Buffer.BlockCopy(outPrev, 0, _previewBytes, 0, outPrev.Length);
                _previewW = w;
                _previewH = h;
                _frameSeq++;
            }

            long now = Environment.TickCount;
            if (_lastFrameMs != 0)
            {
                double dt = (now - _lastFrameMs) / 1000.0;
                if (dt > 0.001 && dt < 1.0)
                {
                    double inst = 1.0 / dt;
                    _fps = _fps < 1.0 ? inst : (_fps * 0.85 + inst * 0.15);
                }
            }
            _lastFrameMs = now;
        }

        void RefreshPreview()
        {
            if (_cap != null && _cap.IsBusy)
            {
                _isBusy = true;
                _statusLbl.Text = "Kamera '" + _busyCamName + "' dipakai Streamlabs — ganti ke 'AlapCamera'";
                _stage.Invalidate();
                return;
            }

            bool dirty = false;
            lock (_previewLock)
            {
                if (_frameSeq != _shownSeq && _previewBytes != null && _previewW > 0 && _previewH > 0)
                {
                    if (_bmp == null || _bmp.Width != _previewW || _bmp.Height != _previewH)
                    {
                        if (_bmp != null) _bmp.Dispose();
                        _bmp = new Bitmap(_previewW, _previewH, PixelFormat.Format24bppRgb);
                    }
                    var bd = _bmp.LockBits(new Rectangle(0, 0, _previewW, _previewH), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                    Marshal.Copy(_previewBytes, 0, bd.Scan0, _previewW * _previewH * 3);
                    _bmp.UnlockBits(bd);
                    _shownSeq = _frameSeq;
                    dirty = true;
                }
            }
            if (dirty)
            {
                _stage.Invalidate();
                string enh = _proc.EnhanceEnabled ? "  ·  AI Enhance ✓" : "";
                string bg = _proc.BgEnabled ? "  ·  BG Cutout ✓" : "";
                _statusLbl.Text = string.Format("{0}×{1}  ·  {2:0.0} FPS{3}{4}", _previewW, _previewH, _fps, enh, bg);
            }
        }

        void OnStagePaint(object sender, PaintEventArgs e)
        {
            if (_isBusy)
            {
                Theme.DrawText(e.Graphics,
                    "Kamera '" + _busyCamName + "' sedang dipakai aplikasi lain.\n\nDi Streamlabs / OBS, ganti Device ke 'AlapCamera'.\nKamera akan tersambung otomatis begitu dilepas...",
                    Theme.Semi(11f), Theme.Muted, _stage.ClientRectangle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            if (_bmp == null)
            {
                Theme.DrawText(e.Graphics, "Menghubungkan kamera...", Theme.Semi(11f), Theme.Muted,
                    _stage.ClientRectangle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            float scale = Math.Min((float)_stage.Width / _bmp.Width, (float)_stage.Height / _bmp.Height);
            int dw = Math.Max(1, (int)(_bmp.Width * scale));
            int dh = Math.Max(1, (int)(_bmp.Height * scale));
            int dx = (_stage.Width - dw) / 2;
            int dy = (_stage.Height - dh) / 2;
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            e.Graphics.DrawImage(_bmp, dx, dy, dw, dh);
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var js = new JavaScriptSerializer();
                var d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsPath));
                if (d == null) return;
                if (d.ContainsKey("camera") && d["camera"] != null) _preferredCam = d["camera"].ToString();
                if (d.ContainsKey("mirror")) _proc.Mirror = Convert.ToBoolean(d["mirror"]);
                if (d.ContainsKey("enhance")) _proc.EnhanceEnabled = Convert.ToBoolean(d["enhance"]);
                if (d.ContainsKey("bgEnabled")) _proc.BgEnabled = Convert.ToBoolean(d["bgEnabled"]);
                if (d.ContainsKey("bgMode") && d["bgMode"] != null)
                {
                    BgMode m;
                    if (Enum.TryParse(d["bgMode"].ToString(), true, out m)) _proc.Mode = m;
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                string cam = _camCombo.SelectedItem != null ? _camCombo.SelectedItem.ToString() : _preferredCam;
                var d = new Dictionary<string, object>
                {
                    { "camera", cam },
                    { "mirror", _proc.Mirror },
                    { "enhance", _proc.EnhanceEnabled },
                    { "bgEnabled", _proc.BgEnabled },
                    { "bgMode", _proc.Mode.ToString() }
                };
                File.WriteAllText(SettingsPath, new JavaScriptSerializer().Serialize(d));
            }
            catch { }
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
            if (_uiTimer != null) _uiTimer.Stop();
            StopCamera();
            _vcam.Dispose();
            if (_bmp != null) _bmp.Dispose();
        }

        sealed class StagePanel : Panel
        {
            public StagePanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }
        }
    }
}
