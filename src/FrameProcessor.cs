using System;

namespace AlapCamera
{
    enum BgMode { Cutout, GreenScreen, Blur }

    sealed class FrameProcessor : IDisposable
    {
        public bool Mirror = false;
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
        float[] _maskTmp;    // 256*256 scratch for spatial 3x3 Gaussian feather
        byte[] _smallRgb;    // 256*256*3
        byte[] _blurBuf;
        byte[] _blurSmallA;
        byte[] _blurSmallB;
        byte[] _blurW0;
        byte[] _blurW1;
        int[] _bx0, _bx1, _bfx;
        int _bxLutW, _bxLutBw;
        byte[] _prevBuf;
        byte[] _workBuf;
        byte[] _rawCopy;
        int[] _mx0, _mx1;
        float[] _mfx;
        int _mxLutW;
        readonly int[] _hist = new int[256];

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

        public unsafe void Process(byte[] bgr, int w, int h, out byte[] outVcam, out byte[] outPreview, bool splitCompare, bool needPreview = true)
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
                    // Run AI inference every 2nd frame (15 Hz mask, full 30 FPS output) for smooth motion & low CPU
                    if (_maskSmooth == null || (_segFrameIdx++ % 2) == 0)
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
                                    a = (a - 0.20f) / 0.64f;
                                    if (a <= 0f) a = 0f;
                                    else if (a >= 1f) a = 1f;
                                    else a = a * a * (3f - 2f * a);
                                    if (a < 0f) a = 0f;
                                    else if (a > 1f) a = 1f;
                                    float prev = _maskSmooth[i];
                                    float diff = a > prev ? a - prev : prev - a;
                                    float k = diff > 0.20f ? 0.95f : (diff > 0.08f ? 0.84f : 0.72f);
                                    _maskSmooth[i] = prev + (a - prev) * k;
                                }
                                SmoothMask3x3();
                            }
                        }
                    }
                }
                else
                {
                    if (_bgRef == null) InitBackground(_workBuf, w, h);
                    UpdateMask(_workBuf, w, h);
                }
                ApplyBackground(_workBuf, w, h, out outVcam, out outPreview, needPreview);
            }
            else
            {
                outVcam = _workBuf;
                outPreview = _workBuf;
            }

            if (splitCompare && needPreview)
            {
                if (outPreview == outVcam)
                {
                    if (_prevBuf == null || _prevBuf.Length != len) _prevBuf = new byte[len];
                    Buffer.BlockCopy(outPreview, 0, _prevBuf, 0, len);
                    outPreview = _prevBuf;
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
                int[] hist = _hist;
                Array.Clear(hist, 0, 256);
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

        unsafe void ApplyBackground(byte[] fg, int w, int h, out byte[] outVcam, out byte[] outPreview, bool needPreview)
        {
            int len = w * h * 3;
            if (Mode == BgMode.Blur)
            {
                if (_blurBuf == null || _blurBuf.Length != len) _blurBuf = new byte[len];
                RenderBokehBlur(fg, _blurBuf, w, h);

                outVcam = _blurBuf;
                outPreview = _blurBuf;
                return;
            }

            // Cutout & GreenScreen both output crisp #00FF00 chroma-key green to virtual cam,
            // while Cutout renders a transparent PNG checkerboard in the app preview!
            if (_blurBuf == null || _blurBuf.Length != len) _blurBuf = new byte[len];
            BlendSolidGreen(fg, _blurBuf, w, h);
            outVcam = _blurBuf;

            if (Mode == BgMode.Cutout && needPreview)
            {
                if (_prevBuf == null || _prevBuf.Length != len) _prevBuf = new byte[len];
                BlendCheckerboard(fg, _prevBuf, w, h);
                outPreview = _prevBuf;
            }
            else
            {
                outPreview = _blurBuf;
            }
        }

        void EnsureMxLuts(int w)
        {
            if (_mx0 != null && _mxLutW == w) return;
            _mx0 = new int[w];
            _mx1 = new int[w];
            _mfx = new float[w];
            float scale = (Mw - 1f) / Math.Max(1, w - 1);
            for (int x = 0; x < w; x++)
            {
                float fx = x * scale;
                int x0 = (int)fx;
                if (x0 >= Mw - 1) { x0 = Mw - 1; _mx0[x] = x0; _mx1[x] = x0; _mfx[x] = 0f; }
                else { _mx0[x] = x0; _mx1[x] = x0 + 1; _mfx[x] = fx - x0; }
            }
            _mxLutW = w;
        }

        unsafe void BlendSolidGreen(byte[] fg, byte[] dst, int w, int h)
        {
            EnsureMxLuts(w);
            float yScale = (Mh - 1f) / Math.Max(1, h - 1);
            fixed (byte* pF = fg)
            fixed (byte* pD = dst)
            fixed (float* pM = _maskSmooth)
            fixed (int* pX0 = _mx0)
            fixed (int* pX1 = _mx1)
            fixed (float* pFx = _mfx)
            {
                for (int y = 0; y < h; y++)
                {
                    float fyFull = y * yScale;
                    int y0 = (int)fyFull;
                    int y1 = y0 >= Mh - 1 ? Mh - 1 : y0 + 1;
                    float fy = fyFull - y0;
                    float invFy = 1f - fy;
                    byte* fRow = pF + y * w * 3;
                    byte* dRow = pD + y * w * 3;
                    float* m0 = pM + y0 * Mw;
                    float* m1 = pM + y1 * Mw;
                    for (int x = 0; x < w; x++)
                    {
                        int x0 = pX0[x], x1 = pX1[x];
                        float fx = pFx[x], invFx = 1f - fx;
                        float a = (m0[x0] * invFx + m0[x1] * fx) * invFy + (m1[x0] * invFx + m1[x1] * fx) * fy;
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
            EnsureMxLuts(w);
            float yScale = (Mh - 1f) / Math.Max(1, h - 1);
            fixed (byte* pF = fg)
            fixed (byte* pD = dst)
            fixed (float* pM = _maskSmooth)
            fixed (int* pX0 = _mx0)
            fixed (int* pX1 = _mx1)
            fixed (float* pFx = _mfx)
            {
                for (int y = 0; y < h; y++)
                {
                    float fyFull = y * yScale;
                    int y0 = (int)fyFull;
                    int y1 = y0 >= Mh - 1 ? Mh - 1 : y0 + 1;
                    float fy = fyFull - y0;
                    float invFy = 1f - fy;
                    int cy = (y >> 4) & 1;
                    byte* fRow = pF + y * w * 3;
                    byte* dRow = pD + y * w * 3;
                    float* m0 = pM + y0 * Mw;
                    float* m1 = pM + y1 * Mw;
                    for (int x = 0; x < w; x++)
                    {
                        int x0 = pX0[x], x1 = pX1[x];
                        float fx = pFx[x], invFx = 1f - fx;
                        float a = (m0[x0] * invFx + m0[x1] * fx) * invFy + (m1[x0] * invFx + m1[x1] * fx) * fy;
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

        unsafe void SmoothMask3x3()
        {
            if (_maskSmooth == null || _maskSmooth.Length != Mw * Mh) return;
            if (_maskTmp == null || _maskTmp.Length != Mw * Mh) _maskTmp = new float[Mw * Mh];

            fixed (float* pS = _maskSmooth)
            fixed (float* pT = _maskTmp)
            {
                // Horizontal [1, 2, 1] / 4
                for (int y = 0; y < Mh; y++)
                {
                    float* sRow = pS + y * Mw;
                    float* tRow = pT + y * Mw;
                    tRow[0] = (sRow[0] * 3f + sRow[1]) * 0.25f;
                    for (int x = 1; x < Mw - 1; x++)
                        tRow[x] = (sRow[x - 1] + sRow[x] * 2f + sRow[x + 1]) * 0.25f;
                    tRow[Mw - 1] = (sRow[Mw - 2] + sRow[Mw - 1] * 3f) * 0.25f;
                }

                // Vertical [1, 2, 1] / 4
                for (int y = 0; y < Mh; y++)
                {
                    int ym1 = y > 0 ? y - 1 : 0;
                    int yp1 = y < Mh - 1 ? y + 1 : Mh - 1;
                    float* r0 = pT + ym1 * Mw;
                    float* r1 = pT + y * Mw;
                    float* r2 = pT + yp1 * Mw;
                    float* dRow = pS + y * Mw;
                    for (int x = 0; x < Mw; x++)
                        dRow[x] = (r0[x] + r1[x] * 2f + r2[x]) * 0.25f;
                }
            }
        }

        void EnsureBxLuts(int w, int bw)
        {
            if (_bx0 != null && _bxLutW == w && _bxLutBw == bw) return;
            _bx0 = new int[w];
            _bx1 = new int[w];
            _bfx = new int[w];
            int bRowBytes = bw * 3;
            int denom = Math.Max(1, w - 1);
            for (int x = 0; x < w; x++)
            {
                int gx = (x * (bw - 1) << 8) / denom;
                int x0 = (gx >> 8) * 3;
                int x1 = (x0 < bRowBytes - 3) ? x0 + 3 : bRowBytes - 3;
                _bx0[x] = x0;
                _bx1[x] = x1;
                _bfx[x] = gx & 255;
            }
            _bxLutW = w;
            _bxLutBw = bw;
        }

        unsafe void RenderBokehBlur(byte[] fg, byte[] dst, int w, int h)
        {
            // Contiguous 7-tap (-3..+3) 4-pass mask-weighted Gaussian bokeh on 1/4 grid (zero skipped neighbors -> zero pixel blocks!)
            EnsureMxLuts(w);
            int bw = Math.Max(16, w / 4), bh = Math.Max(16, h / 4);
            EnsureBxLuts(w, bw);

            int sCells = bw * bh;
            int sLen = sCells * 3;
            if (_blurSmallA == null || _blurSmallA.Length != sLen) _blurSmallA = new byte[sLen];
            if (_blurSmallB == null || _blurSmallB.Length != sLen) _blurSmallB = new byte[sLen];
            if (_blurW0 == null || _blurW0.Length != sCells) _blurW0 = new byte[sCells];
            if (_blurW1 == null || _blurW1.Length != sCells) _blurW1 = new byte[sCells];

            int stride = w * 3;
            fixed (byte* pF = fg)
            fixed (byte* pD = dst)
            fixed (byte* pA = _blurSmallA)
            fixed (byte* pB = _blurSmallB)
            fixed (byte* pW0 = _blurW0)
            fixed (byte* pW1 = _blurW1)
            fixed (float* pM = _maskSmooth)
            fixed (int* pMx0 = _mx0)
            fixed (int* pMx1 = _mx1)
            fixed (float* pMfx = _mfx)
            fixed (int* pBx0 = _bx0)
            fixed (int* pBx1 = _bx1)
            fixed (int* pBfx = _bfx)
            {
                // 1) True 4x4 area-average downsample into 1/4 grid + background mask weight (eliminates all subsampling pixel blocks)
                for (int y = 0; y < bh; y++)
                {
                    int sy0 = (y * h) / bh;
                    int sy1 = Math.Min(h - 1, sy0 + 1);
                    int sy2 = Math.Min(h - 1, sy0 + 2);
                    int sy3 = Math.Min(h - 1, sy0 + 3);
                    byte* r0 = pF + sy0 * stride;
                    byte* r1 = pF + sy1 * stride;
                    byte* r2 = pF + sy2 * stride;
                    byte* r3 = pF + sy3 * stride;

                    int my = (y * Mh) / bh;
                    float* mRow = pM + my * Mw;
                    byte* dRow = pA + y * bw * 3;
                    byte* wRow = pW0 + y * bw;

                    for (int x = 0; x < bw; x++)
                    {
                        int xBase = (x * w) / bw;
                        int sx0 = xBase * 3;
                        int sx1 = Math.Min(w - 1, xBase + 1) * 3;
                        int sx2 = Math.Min(w - 1, xBase + 2) * 3;
                        int sx3 = Math.Min(w - 1, xBase + 3) * 3;

                        int sumB = r0[sx0] + r0[sx1] + r0[sx2] + r0[sx3]
                                 + r1[sx0] + r1[sx1] + r1[sx2] + r1[sx3]
                                 + r2[sx0] + r2[sx1] + r2[sx2] + r2[sx3]
                                 + r3[sx0] + r3[sx1] + r3[sx2] + r3[sx3];
                        int sumG = r0[sx0 + 1] + r0[sx1 + 1] + r0[sx2 + 1] + r0[sx3 + 1]
                                 + r1[sx0 + 1] + r1[sx1 + 1] + r1[sx2 + 1] + r1[sx3 + 1]
                                 + r2[sx0 + 1] + r2[sx1 + 1] + r2[sx2 + 1] + r2[sx3 + 1]
                                 + r3[sx0 + 1] + r3[sx1 + 1] + r3[sx2 + 1] + r3[sx3 + 1];
                        int sumR = r0[sx0 + 2] + r0[sx1 + 2] + r0[sx2 + 2] + r0[sx3 + 2]
                                 + r1[sx0 + 2] + r1[sx1 + 2] + r1[sx2 + 2] + r1[sx3 + 2]
                                 + r2[sx0 + 2] + r2[sx1 + 2] + r2[sx2 + 2] + r2[sx3 + 2]
                                 + r3[sx0 + 2] + r3[sx1 + 2] + r3[sx2 + 2] + r3[sx3 + 2];

                        dRow[0] = (byte)((sumB + 8) >> 4);
                        dRow[1] = (byte)((sumG + 8) >> 4);
                        dRow[2] = (byte)((sumR + 8) >> 4);

                        float ma = mRow[(x * Mw) / bw];
                        int bgW = (int)((1f - ma) * 254f) + 1;
                        if (bgW < 1) bgW = 1; else if (bgW > 255) bgW = 255;
                        wRow[x] = (byte)bgW;
                        dRow += 3;
                    }
                }

                // 2) 4-pass multi-scale À-Trous Gaussian bokeh (stride 1 -> stride 2 -> stride 2 -> stride 1)
                // Pass 0 (stride 1) band-limits high frequencies; Passes 1-2 (stride 2) double the optical bokeh radius;
                // Pass 3 (stride 1) polishes adjacent texels so zero pixel grid can ever appear.
                int bStride = bw * 3;
                for (int pass = 0; pass < 4; pass++)
                {
                    int step = (pass == 1 || pass == 2) ? 2 : 1;
                    int s1 = step, s2 = step * 2, s3 = step * 3;

                    // Horizontal: (pA, pW0) -> (pB, pW1)
                    for (int y = 0; y < bh; y++)
                    {
                        byte* sRow = pA + y * bStride;
                        byte* dRow = pB + y * bStride;
                        byte* swRow = pW0 + y * bw;
                        byte* dwRow = pW1 + y * bw;
                        for (int x = 0; x < bw; x++)
                        {
                            int i0 = x >= s3 ? x - s3 : 0;
                            int i1 = x >= s2 ? x - s2 : 0;
                            int i2 = x >= s1 ? x - s1 : 0;
                            int i3 = x;
                            int i4 = x + s1 < bw ? x + s1 : bw - 1;
                            int i5 = x + s2 < bw ? x + s2 : bw - 1;
                            int i6 = x + s3 < bw ? x + s3 : bw - 1;

                            int w0 = swRow[i0];
                            int w1 = swRow[i1] * 3;
                            int w2 = swRow[i2] * 6;
                            int w3 = swRow[i3] << 3;
                            int w4 = swRow[i4] * 6;
                            int w5 = swRow[i5] * 3;
                            int w6 = swRow[i6];
                            int wSum = w0 + w1 + w2 + w3 + w4 + w5 + w6;

                            int o0 = i0 * 3, o1 = i1 * 3, o2 = i2 * 3, o3 = i3 * 3, o4 = i4 * 3, o5 = i5 * 3, o6 = i6 * 3;
                            int dx = x * 3;
                            dRow[dx] = (byte)((sRow[o0] * w0 + sRow[o1] * w1 + sRow[o2] * w2 + sRow[o3] * w3 + sRow[o4] * w4 + sRow[o5] * w5 + sRow[o6] * w6 + (wSum >> 1)) / wSum);
                            dRow[dx + 1] = (byte)((sRow[o0 + 1] * w0 + sRow[o1 + 1] * w1 + sRow[o2 + 1] * w2 + sRow[o3 + 1] * w3 + sRow[o4 + 1] * w4 + sRow[o5 + 1] * w5 + sRow[o6 + 1] * w6 + (wSum >> 1)) / wSum);
                            dRow[dx + 2] = (byte)((sRow[o0 + 2] * w0 + sRow[o1 + 2] * w1 + sRow[o2 + 2] * w2 + sRow[o3 + 2] * w3 + sRow[o4 + 2] * w4 + sRow[o5 + 2] * w5 + sRow[o6 + 2] * w6 + (wSum >> 1)) / wSum);
                            dwRow[x] = (byte)((wSum + 14) / 28);
                        }
                    }

                    // Vertical: (pB, pW1) -> (pA, pW0)
                    for (int y = 0; y < bh; y++)
                    {
                        int y0 = y >= s3 ? y - s3 : 0;
                        int y1 = y >= s2 ? y - s2 : 0;
                        int y2 = y >= s1 ? y - s1 : 0;
                        int y3 = y;
                        int y4 = y + s1 < bh ? y + s1 : bh - 1;
                        int y5 = y + s2 < bh ? y + s2 : bh - 1;
                        int y6 = y + s3 < bh ? y + s3 : bh - 1;

                        byte* r0 = pB + y0 * bStride, r1 = pB + y1 * bStride, r2 = pB + y2 * bStride, r3 = pB + y3 * bStride, r4 = pB + y4 * bStride, r5 = pB + y5 * bStride, r6 = pB + y6 * bStride;
                        byte* wr0 = pW1 + y0 * bw, wr1 = pW1 + y1 * bw, wr2 = pW1 + y2 * bw, wr3 = pW1 + y3 * bw, wr4 = pW1 + y4 * bw, wr5 = pW1 + y5 * bw, wr6 = pW1 + y6 * bw;
                        byte* dRow = pA + y * bStride;
                        byte* dwRow = pW0 + y * bw;
                        for (int x = 0; x < bw; x++)
                        {
                            int w0 = wr0[x];
                            int w1 = wr1[x] * 3;
                            int w2 = wr2[x] * 6;
                            int w3 = wr3[x] << 3;
                            int w4 = wr4[x] * 6;
                            int w5 = wr5[x] * 3;
                            int w6 = wr6[x];
                            int wSum = w0 + w1 + w2 + w3 + w4 + w5 + w6;

                            int ox = x * 3;
                            dRow[ox] = (byte)((r0[ox] * w0 + r1[ox] * w1 + r2[ox] * w2 + r3[ox] * w3 + r4[ox] * w4 + r5[ox] * w5 + r6[ox] * w6 + (wSum >> 1)) / wSum);
                            dRow[ox + 1] = (byte)((r0[ox + 1] * w0 + r1[ox + 1] * w1 + r2[ox + 1] * w2 + r3[ox + 1] * w3 + r4[ox + 1] * w4 + r5[ox + 1] * w5 + r6[ox + 1] * w6 + (wSum >> 1)) / wSum);
                            dRow[ox + 2] = (byte)((r0[ox + 2] * w0 + r1[ox + 2] * w1 + r2[ox + 2] * w2 + r3[ox + 2] * w3 + r4[ox + 2] * w4 + r5[ox + 2] * w5 + r6[ox + 2] * w6 + (wSum >> 1)) / wSum);
                            dwRow[x] = (byte)((wSum + 14) / 28);
                        }
                    }
                }

                // 3) Fused single-pass color-guided bilinear mask + bilinear bokeh upsample
                float yMaskScale = (Mh - 1f) / Math.Max(1, h - 1);
                int hDenom = Math.Max(1, h - 1);
                for (int y = 0; y < h; y++)
                {
                    float fyFull = y * yMaskScale;
                    int my0 = (int)fyFull;
                    int my1 = my0 >= Mh - 1 ? Mh - 1 : my0 + 1;
                    float mfy = fyFull - my0;
                    float invMfy = 1f - mfy;
                    float* m0 = pM + my0 * Mw;
                    float* m1 = pM + my1 * Mw;

                    int gy = (y * (bh - 1) << 8) / hDenom;
                    int by0 = gy >> 8;
                    int by1 = by0 < bh - 1 ? by0 + 1 : bh - 1;
                    int bfy = gy & 255;
                    int ibfy = 256 - bfy;
                    byte* s0 = pA + by0 * bStride;
                    byte* s1 = pA + by1 * bStride;

                    byte* fRow = pF + y * stride;
                    byte* dRow = pD + y * stride;

                    for (int x = 0; x < w; x++)
                    {
                        int mx0 = pMx0[x], mx1 = pMx1[x];
                        float mfx = pMfx[x], invMfx = 1f - mfx;
                        float a = (m0[mx0] * invMfx + m0[mx1] * mfx) * invMfy + (m1[mx0] * invMfx + m1[mx1] * mfx) * mfy;

                        if (a > 0.98f)
                        {
                            dRow[0] = fRow[0];
                            dRow[1] = fRow[1];
                            dRow[2] = fRow[2];
                        }
                        else
                        {
                            int bx0 = pBx0[x], bx1 = pBx1[x];
                            int bfx = pBfx[x], ibfx = 256 - bfx;
                            int w00 = ibfx * ibfy, w10 = bfx * ibfy, w01 = ibfx * bfy, w11 = bfx * bfy;
                            int bb = (s0[bx0] * w00 + s0[bx1] * w10 + s1[bx0] * w01 + s1[bx1] * w11 + 32768) >> 16;
                            int bg = (s0[bx0 + 1] * w00 + s0[bx1 + 1] * w10 + s1[bx0 + 1] * w01 + s1[bx1 + 1] * w11 + 32768) >> 16;
                            int br = (s0[bx0 + 2] * w00 + s0[bx1 + 2] * w10 + s1[bx0 + 2] * w01 + s1[bx1 + 2] * w11 + 32768) >> 16;

                            if (a < 0.02f)
                            {
                                dRow[0] = (byte)bb;
                                dRow[1] = (byte)bg;
                                dRow[2] = (byte)br;
                            }
                            else
                            {
                                // Color-guided edge refinement: snap soft mask boundary to true high-res foreground/background edge
                                int db = fRow[0] - bb; if (db < 0) db = -db;
                                int dg = fRow[1] - bg; if (dg < 0) dg = -dg;
                                int dr = fRow[2] - br; if (dr < 0) dr = -dr;
                                int cDiff = db + dg + dr;
                                if (cDiff > 96)
                                    a = a + (1f - a) * 0.25f;
                                else if (cDiff < 24)
                                    a = a * 0.75f;

                                float sa = a * a * (3f - 2f * a);
                                float isa = 1f - sa;
                                dRow[0] = (byte)(fRow[0] * sa + bb * isa);
                                dRow[1] = (byte)(fRow[1] * sa + bg * isa);
                                dRow[2] = (byte)(fRow[2] * sa + br * isa);
                            }
                        }
                        fRow += 3;
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
