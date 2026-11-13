using System;

namespace AlapCamera
{
    enum BgMode { Cutout, GreenScreen, Blur }

    sealed class FrameProcessor : IDisposable
    {
        public bool Mirror = true;
        public bool EnhanceEnabled = true;
        public bool BgEnabled = true;
        public BgMode Mode = BgMode.Cutout;

        // Auto-enhance state (EMA smoothed)
        float _emaGamma = 1.0f;
        float _emaGainB = 1.0f, _emaGainG = 1.0f, _emaGainR = 1.0f;
        float _emaLift = 0.0f;
        int _frameIdx;
        int _segFrameIdx;
        readonly byte[] _lutB = new byte[256];
        readonly byte[] _lutG = new byte[256];
        readonly byte[] _lutR = new byte[256];

        // Background segmentation state (256x256)
        const int Mw = 256, Mh = 256;
        readonly OnnxSeg _onnx = new OnnxSeg();
        readonly float[] _aiMask = new float[Mw * Mh];
        byte[] _bgRef;       // 256*256*3 static background reference (fallback)
        float[] _maskSmooth; // 256*256 smoothed alpha [0..1]
        byte[] _smallRgb;    // 256*256*3
        byte[] _blurBuf;
        byte[] _workBuf;
        byte[] _rawCopy;

        public string SegStatus { get { return _onnx.StatusText; } }

        public FrameProcessor()
        {
            for (int i = 0; i < 256; i++)
                _lutB[i] = _lutG[i] = _lutR[i] = (byte)i;
        }

        public void Dispose()
        {
            _onnx.Dispose();
        }

        public void ResetBackground()
        {
            _bgRef = null;
            _maskSmooth = null;
        }

        public unsafe void Process(byte[] bgr, int w, int h, out byte[] outVcam, out byte[] outPreview, bool splitCompare)
        {
            int len = w * h * 3;
            if (_workBuf == null || _workBuf.Length != len) _workBuf = new byte[len];
            if (_rawCopy == null || _rawCopy.Length != len) _rawCopy = new byte[len];

            if (Mirror)
                FlipHorizontal(bgr, _workBuf, w, h);
            else
                Buffer.BlockCopy(bgr, 0, _workBuf, 0, len);

            if (splitCompare)
                Buffer.BlockCopy(_workBuf, 0, _rawCopy, 0, len);

            if (EnhanceEnabled)
                ApplyAutoEnhance(_workBuf, w, h);

            if (BgEnabled)
            {
                if (_onnx.IsAvailable)
                {
                    // Run AI inference every 2nd frame (15 Hz mask, full 30 FPS output) to cut CPU ~50%
                    if (_maskSmooth == null || (_segFrameIdx++ & 1) == 0)
                    {
                        if (_onnx.Infer(_workBuf, w, h, _aiMask))
                        {
                            if (_maskSmooth == null || _maskSmooth.Length != Mw * Mh)
                            {
                                _maskSmooth = new float[Mw * Mh];
                                Buffer.BlockCopy(_aiMask, 0, _maskSmooth, 0, _aiMask.Length * sizeof(float));
                            }
                            else
                            {
                                for (int i = 0; i < _aiMask.Length; i++)
                                {
                                    float a = _aiMask[i];
                                    a = (a - 0.15f) / 0.70f;
                                    if (a < 0f) a = 0f;
                                    else if (a > 1f) a = 1f;
                                    _maskSmooth[i] = 0.70f * a + 0.30f * _maskSmooth[i];
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (_bgRef == null) InitBackground(_workBuf, w, h);
                    UpdateMask(_workBuf, w, h);
                }
                ApplyBackground(_workBuf, w, h, out outVcam, out outPreview);
            }
            else
            {
                outVcam = _workBuf;
                outPreview = _workBuf;
            }

            if (splitCompare)
            {
                if (outPreview == outVcam)
                {
                    var copy = new byte[len];
                    Buffer.BlockCopy(outPreview, 0, copy, 0, len);
                    outPreview = copy;
                }
                ApplySplit(_rawCopy, outPreview, w, h);
            }
        }

        static unsafe void FlipHorizontal(byte[] src, byte[] dst, int w, int h)
        {
            int stride = w * 3;
            fixed (byte* pSrc = src)
            fixed (byte* pDst = dst)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* sRow = pSrc + y * stride;
                    byte* dRow = pDst + y * stride + (w - 1) * 3;
                    for (int x = 0; x < w; x++)
                    {
                        dRow[0] = sRow[0];
                        dRow[1] = sRow[1];
                        dRow[2] = sRow[2];
                        sRow += 3;
                        dRow -= 3;
                    }
                }
            }
        }

        unsafe void ApplyAutoEnhance(byte[] buf, int w, int h)
        {
            _frameIdx++;
            if (_frameIdx % 3 == 1)
            {
                // Fast portrait-zone metering on 8x subsampled grid
                int y0 = (int)(h * 0.15f), y1 = (int)(h * 0.85f);
                int x0 = (int)(w * 0.20f), x1 = (int)(w * 0.80f);
                int[] hist = new int[256];
                long sumB = 0, sumG = 0, sumR = 0;
                int count = 0;

                fixed (byte* p = buf)
                {
                    for (int y = y0; y < y1; y += 8)
                    {
                        byte* row = p + y * w * 3;
                        for (int x = x0; x < x1; x += 8)
                        {
                            byte* px = row + x * 3;
                            int b = px[0], g = px[1], r = px[2];
                            int lum = (b * 29 + g * 150 + r * 77) >> 8;
                            hist[lum]++;
                            sumB += b; sumG += g; sumR += r;
                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    int p05 = Percentile(hist, count, 5);
                    int p50 = Percentile(hist, count, 50);

                    // Target portrait median ~132/255
                    float target = 132f;
                    float cur = Math.Max(14f, p50);
                    float gamma = (float)(Math.Log(target / 255.0) / Math.Log(cur / 255.0));
                    gamma = Clamp(gamma, 0.42f, 1.35f);
                    float lift = p05 < 16 ? Math.Min(18f, (16 - p05) * 0.8f) : 0f;

                    float avgB = sumB / (float)count;
                    float avgG = sumG / (float)count;
                    float avgR = sumR / (float)count;
                    float avgAll = (avgB + avgG + avgR) / 3f;
                    float gb = Clamp(avgAll / Math.Max(12f, avgB), 0.88f, 1.15f);
                    float gg = Clamp(avgAll / Math.Max(12f, avgG), 0.90f, 1.12f);
                    float gr = Clamp(avgAll / Math.Max(12f, avgR), 0.88f, 1.15f);
                    // Blend 45% toward neutral WB so skin tones stay natural
                    gb = 1f + (gb - 1f) * 0.45f;
                    gg = 1f + (gg - 1f) * 0.45f;
                    gr = 1f + (gr - 1f) * 0.45f;

                    float a = _frameIdx < 10 ? 0.35f : 0.12f;
                    _emaGamma += (gamma - _emaGamma) * a;
                    _emaLift += (lift - _emaLift) * a;
                    _emaGainB += (gb - _emaGainB) * a;
                    _emaGainG += (gg - _emaGainG) * a;
                    _emaGainR += (gr - _emaGainR) * a;

                    BuildLut(_lutB, _emaGamma, _emaLift, _emaGainB);
                    BuildLut(_lutG, _emaGamma, _emaLift, _emaGainG);
                    BuildLut(_lutR, _emaGamma, _emaLift, _emaGainR);
                }
            }

            int total = w * h * 3;
            fixed (byte* p = buf)
            fixed (byte* lb = _lutB)
            fixed (byte* lg = _lutG)
            fixed (byte* lr = _lutR)
            {
                for (int i = 0; i < total; i += 3)
                {
                    p[i] = lb[p[i]];
                    p[i + 1] = lg[p[i + 1]];
                    p[i + 2] = lr[p[i + 2]];
                }
            }
        }

        static void BuildLut(byte[] lut, float gamma, float lift, float gain)
        {
            for (int i = 0; i < 256; i++)
            {
                float x = (i + lift) / (255f + lift);
                float v = (float)Math.Pow(Clamp(x, 0f, 1f), gamma) * 255f * gain;
                // Gentle S-curve contrast boost
                float n = v / 255f;
                n = n + 0.06f * (float)Math.Sin(n * Math.PI * 2.0) * -0.5f;
                int iv = (int)Math.Round(Clamp(n * 255f, 0f, 255f));
                lut[i] = (byte)iv;
            }
        }

        unsafe void InitBackground(byte[] buf, int w, int h)
        {
            _bgRef = new byte[Mw * Mh * 3];
            _smallRgb = new byte[Mw * Mh * 3];
            _maskSmooth = new float[Mw * Mh];
            Downsample(buf, w, h, _bgRef);
        }

        unsafe void Downsample(byte[] src, int w, int h, byte[] dst)
        {
            fixed (byte* pS = src)
            fixed (byte* pD = dst)
            {
                for (int y = 0; y < Mh; y++)
                {
                    int sy = (y * h) / Mh;
                    byte* sRow = pS + sy * w * 3;
                    byte* dRow = pD + y * Mw * 3;
                    for (int x = 0; x < Mw; x++)
                    {
                        int sx = (x * w) / Mw;
                        byte* px = sRow + sx * 3;
                        dRow[0] = px[0];
                        dRow[1] = px[1];
                        dRow[2] = px[2];
                        dRow += 3;
                    }
                }
            }
        }

        unsafe void UpdateMask(byte[] buf, int w, int h)
        {
            Downsample(buf, w, h, _smallRgb);

            // Estimate border color (top/left/right edges are usually background)
            int bSum = 0, gSum = 0, rSum = 0, bCnt = 0;
            for (int y = 0; y < Mh; y++)
            {
                for (int x = 0; x < Mw; x++)
                {
                    if (y < 14 || x < 14 || x > Mw - 15)
                    {
                        int idx = (y * Mw + x) * 3;
                        bSum += _smallRgb[idx];
                        gSum += _smallRgb[idx + 1];
                        rSum += _smallRgb[idx + 2];
                        bCnt++;
                    }
                }
            }
            int edgeB = bSum / Math.Max(1, bCnt);
            int edgeG = gSum / Math.Max(1, bCnt);
            int edgeR = rSum / Math.Max(1, bCnt);

            float[] raw = new float[Mw * Mh];
            for (int y = 0; y < Mh; y++)
            {
                float ny = (y / (float)Mh - 0.62f) / 0.55f;
                for (int x = 0; x < Mw; x++)
                {
                    float nx = (x / (float)Mw - 0.50f) / 0.34f;
                    float distCenter = nx * nx + ny * ny;

                    int idx = (y * Mw + x) * 3;
                    int b = _smallRgb[idx], g = _smallRgb[idx + 1], r = _smallRgb[idx + 2];

                    int db = b - _bgRef[idx], dg = g - _bgRef[idx + 1], dr = r - _bgRef[idx + 2];
                    float motionDiff = (Math.Abs(db) + Math.Abs(dg) + Math.Abs(dr)) / 3f;

                    int eb = b - edgeB, eg = g - edgeG, er = r - edgeR;
                    float edgeDiff = (Math.Abs(eb) + Math.Abs(eg) + Math.Abs(er)) / 3f;

                    // Skin tone heuristic (YCbCr)
                    float cb = 128 - 0.1687f * r - 0.3313f * g + 0.5f * b;
                    float cr = 128 + 0.5f * r - 0.4187f * g - 0.0813f * b;
                    bool isSkin = (r > 55 && r > g && r > b && cb >= 77 && cb <= 128 && cr >= 132 && cr <= 176);

                    float score = 0f;
                    if (isSkin && distCenter < 1.8f) score += 0.85f;
                    if (motionDiff > 16f) score += Clamp((motionDiff - 14f) / 28f, 0f, 0.9f);
                    if (edgeDiff > 22f && distCenter < 1.35f) score += Clamp((edgeDiff - 20f) / 36f, 0f, 0.75f);
                    if (distCenter < 0.72f) score += 0.45f; // portrait torso/head prior

                    float alpha = Clamp((score - 0.38f) * 2.2f, 0f, 1f);
                    raw[y * Mw + x] = alpha;

                    // Slowly adapt static background outside foreground
                    if (alpha < 0.15f && distCenter > 0.95f)
                    {
                        _bgRef[idx] = (byte)((_bgRef[idx] * 15 + b) >> 4);
                        _bgRef[idx + 1] = (byte)((_bgRef[idx + 1] * 15 + g) >> 4);
                        _bgRef[idx + 2] = (byte)((_bgRef[idx + 2] * 15 + r) >> 4);
                    }
                }
            }

            // 3x3 box smooth + temporal EMA
            for (int y = 1; y < Mh - 1; y++)
            {
                for (int x = 1; x < Mw - 1; x++)
                {
                    float sum = 0f;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            sum += raw[(y + dy) * Mw + (x + dx)];
                    float sm = sum / 9f;
                    int i = y * Mw + x;
                    _maskSmooth[i] = _maskSmooth[i] * 0.65f + sm * 0.35f;
                }
            }
        }

        unsafe void ApplyBackground(byte[] fg, int w, int h, out byte[] outVcam, out byte[] outPreview)
        {
            int len = w * h * 3;
            if (Mode == BgMode.Blur)
            {
                if (_blurBuf == null || _blurBuf.Length != len) _blurBuf = new byte[len];
                FastBlur(fg, _blurBuf, w, h);
                BlendWithMask(fg, _blurBuf, w, h, false);
                outVcam = _blurBuf;
                outPreview = _blurBuf;
                return;
            }

            // Cutout & GreenScreen both output crisp #00FF00 chroma-key green to virtual cam,
            // while Cutout renders a transparent PNG checkerboard in the app preview!
            if (_blurBuf == null || _blurBuf.Length != len) _blurBuf = new byte[len];
            BlendSolidGreen(fg, _blurBuf, w, h);
            outVcam = _blurBuf;

            if (Mode == BgMode.Cutout)
            {
                var prev = new byte[len];
                BlendCheckerboard(fg, prev, w, h);
                outPreview = prev;
            }
            else
            {
                outPreview = _blurBuf;
            }
        }

        unsafe void BlendSolidGreen(byte[] fg, byte[] dst, int w, int h)
        {
            fixed (byte* pF = fg)
            fixed (byte* pD = dst)
            fixed (float* pM = _maskSmooth)
            {
                for (int y = 0; y < h; y++)
                {
                    int my = (y * Mh) / h;
                    byte* fRow = pF + y * w * 3;
                    byte* dRow = pD + y * w * 3;
                    float* mRow = pM + my * Mw;
                    for (int x = 0; x < w; x++)
                    {
                        float a = mRow[(x * Mw) / w];
                        if (a > 0.96f)
                        {
                            dRow[0] = fRow[0]; dRow[1] = fRow[1]; dRow[2] = fRow[2];
                        }
                        else if (a < 0.04f)
                        {
                            dRow[0] = 0; dRow[1] = 255; dRow[2] = 0;
                        }
                        else
                        {
                            float ia = 1f - a;
                            dRow[0] = (byte)(fRow[0] * a);
                            dRow[1] = (byte)(fRow[1] * a + 255f * ia);
                            dRow[2] = (byte)(fRow[2] * a);
                        }
                        fRow += 3;
                        dRow += 3;
                    }
                }
            }
        }

        unsafe void BlendCheckerboard(byte[] fg, byte[] dst, int w, int h)
        {
            fixed (byte* pF = fg)
            fixed (byte* pD = dst)
            fixed (float* pM = _maskSmooth)
            {
                for (int y = 0; y < h; y++)
                {
                    int my = (y * Mh) / h;
                    int cy = (y >> 4) & 1;
                    byte* fRow = pF + y * w * 3;
                    byte* dRow = pD + y * w * 3;
                    float* mRow = pM + my * Mw;
                    for (int x = 0; x < w; x++)
                    {
                        float a = mRow[(x * Mw) / w];
                        byte bg = (byte)((((x >> 4) & 1) ^ cy) == 0 ? 44 : 30);
                        if (a > 0.96f)
                        {
                            dRow[0] = fRow[0]; dRow[1] = fRow[1]; dRow[2] = fRow[2];
                        }
                        else if (a < 0.04f)
                        {
                            dRow[0] = bg; dRow[1] = bg; dRow[2] = bg;
                        }
                        else
                        {
                            float ia = 1f - a;
                            dRow[0] = (byte)(fRow[0] * a + bg * ia);
                            dRow[1] = (byte)(fRow[1] * a + bg * ia);
                            dRow[2] = (byte)(fRow[2] * a + bg * ia);
                        }
                        fRow += 3;
                        dRow += 3;
                    }
                }
            }
        }

        unsafe void BlendWithMask(byte[] fg, byte[] bg, int w, int h, bool unused)
        {
            fixed (byte* pF = fg)
            fixed (byte* pB = bg)
            fixed (float* pM = _maskSmooth)
            {
                for (int y = 0; y < h; y++)
                {
                    int my = (y * Mh) / h;
                    byte* fRow = pF + y * w * 3;
                    byte* bRow = pB + y * w * 3;
                    float* mRow = pM + my * Mw;
                    for (int x = 0; x < w; x++)
                    {
                        float a = mRow[(x * Mw) / w];
                        if (a > 0.96f)
                        {
                            bRow[0] = fRow[0]; bRow[1] = fRow[1]; bRow[2] = fRow[2];
                        }
                        else if (a > 0.04f)
                        {
                            float ia = 1f - a;
                            bRow[0] = (byte)(fRow[0] * a + bRow[0] * ia);
                            bRow[1] = (byte)(fRow[1] * a + bRow[1] * ia);
                            bRow[2] = (byte)(fRow[2] * a + bRow[2] * ia);
                        }
                        fRow += 3;
                        bRow += 3;
                    }
                }
            }
        }

        static unsafe void FastBlur(byte[] src, byte[] dst, int w, int h)
        {
            // Fast 4x downsampled bokeh-like box blur
            int bw = Math.Max(1, w / 8), bh = Math.Max(1, h / 8);
            byte[] small = new byte[bw * bh * 3];
            fixed (byte* pS = src)
            fixed (byte* pSm = small)
            fixed (byte* pD = dst)
            {
                for (int y = 0; y < bh; y++)
                {
                    int sy = y * 8;
                    for (int x = 0; x < bw; x++)
                    {
                        int sx = x * 8;
                        int idx = (sy * w + sx) * 3;
                        int o = (y * bw + x) * 3;
                        pSm[o] = pS[idx];
                        pSm[o + 1] = pS[idx + 1];
                        pSm[o + 2] = pS[idx + 2];
                    }
                }
                for (int y = 0; y < h; y++)
                {
                    int sy = Math.Min(bh - 1, y / 8);
                    byte* dRow = pD + y * w * 3;
                    for (int x = 0; x < w; x++)
                    {
                        int sx = Math.Min(bw - 1, x / 8);
                        int o = (sy * bw + sx) * 3;
                        dRow[0] = pSm[o]; dRow[1] = pSm[o + 1]; dRow[2] = pSm[o + 2];
                        dRow += 3;
                    }
                }
            }
        }

        static unsafe void ApplySplit(byte[] before, byte[] after, int w, int h)
        {
            int mid = w / 2;
            int stride = w * 3;
            int leftBytes = mid * 3;
            fixed (byte* pB = before)
            fixed (byte* pA = after)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* s = pB + y * stride;
                    byte* d = pA + y * stride;
                    for (int i = 0; i < leftBytes; i++) d[i] = s[i];
                    // Accent divider line
                    int m = mid * 3;
                    d[m] = 255; d[m + 1] = 92; d[m + 2] = 124;
                }
            }
        }

        static int Percentile(int[] hist, int total, int pct)
        {
            int target = (total * pct) / 100;
            int acc = 0;
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= target) return i; }
            return 255;
        }

        static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
