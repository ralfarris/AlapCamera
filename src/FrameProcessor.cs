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
        float[] _maskTmp;    // 256*256 scratch for guided filter
        byte[] _maskLuma;    // 256*256 luma guide for 5x5 edge-preserving mask refinement
        byte[] _smallRgb;    // 256*256*3
        byte[] _blurBuf;
        byte[] _prevBuf;
        int[] _lxLut;
        int _lxLutW;
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
                                RefineMaskGuided5x5(_workBuf, w, h);
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
            if (_blurBuf == null || _blurBuf.Length != len) _blurBuf = new byte[len];

            if (needPreview)
            {
                if (_prevBuf == null || _prevBuf.Length != len) _prevBuf = new byte[len];
                // Single fused pass computes bilinear mask once per pixel for both VirtualCam (#00FF00) and App Preview (checkerboard)
                BlendCutoutDual(fg, _blurBuf, _prevBuf, w, h);
                outVcam = _blurBuf;
                outPreview = _prevBuf;
            }
            else
            {
                BlendSolidGreen(fg, _blurBuf, w, h);
                outVcam = _blurBuf;
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

        unsafe void BlendCutoutDual(byte[] fg, byte[] dstGreen, byte[] dstPrev, int w, int h)
        {
            EnsureMxLuts(w);
            float yScale = (Mh - 1f) / Math.Max(1, h - 1);
            fixed (byte* pF = fg)
            fixed (byte* pG = dstGreen)
            fixed (byte* pP = dstPrev)
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
                    byte* gRow = pG + y * w * 3;
                    byte* pRow = pP + y * w * 3;
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
                            byte b = fRow[0], g = fRow[1], r = fRow[2];
                            gRow[0] = b; gRow[1] = g; gRow[2] = r;
                            pRow[0] = b; pRow[1] = g; pRow[2] = r;
                        }
                        else if (a < 0.04f)
                        {
                            gRow[0] = 0; gRow[1] = 255; gRow[2] = 0;
                            pRow[0] = bg; pRow[1] = bg; pRow[2] = bg;
                        }
                        else
                        {
                            float ia = 1f - a;
                            float fb = fRow[0] * a, fgC = fRow[1] * a, fr = fRow[2] * a;
                            float bgIa = bg * ia;
                            gRow[0] = (byte)fb;
                            gRow[1] = (byte)(fgC + 255f * ia);
                            gRow[2] = (byte)fr;
                            pRow[0] = (byte)(fb + bgIa);
                            pRow[1] = (byte)(fgC + bgIa);
                            pRow[2] = (byte)(fr + bgIa);
                        }
                        fRow += 3;
                        gRow += 3;
                        pRow += 3;
                    }
                }
            }
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

        unsafe void RefineMaskGuided5x5(byte[] bgr, int w, int h)
        {
            if (_maskSmooth == null || _maskSmooth.Length != Mw * Mh) return;
            if (_maskTmp == null || _maskTmp.Length != Mw * Mh) _maskTmp = new float[Mw * Mh];
            if (_maskLuma == null || _maskLuma.Length != Mw * Mh) _maskLuma = new byte[Mw * Mh];
            if (_lxLut == null || _lxLutW != w)
            {
                _lxLut = new int[Mw];
                for (int x = 0; x < Mw; x++) _lxLut[x] = ((x * w) / Mw) * 3;
                _lxLutW = w;
            }

            int stride = w * 3;
            fixed (byte* pBgr = bgr)
            fixed (byte* pL = _maskLuma)
            fixed (float* pS = _maskSmooth)
            fixed (float* pT = _maskTmp)
            fixed (int* pLx = _lxLut)
            {
                for (int y = 0; y < Mh; y++)
                {
                    byte* sRow = pBgr + ((y * h) / Mh) * stride;
                    byte* lRow = pL + y * Mw;
                    for (int x = 0; x < Mw; x++)
                    {
                        byte* px = sRow + pLx[x];
                        lRow[x] = (byte)((px[0] * 29 + px[1] * 150 + px[2] * 77) >> 8);
                    }
                }

                for (int y = 0; y < Mh; y++)
                {
                    float* sRow = pS + y * Mw;
                    float* tRow = pT + y * Mw;
                    byte* lRow = pL + y * Mw;
                    for (int x = 0; x < Mw; x++)
                    {
                        float cVal = sRow[x];
                        if (cVal <= 0.01f || cVal >= 0.99f) { tRow[x] = cVal; continue; }
                        int cLum = lRow[x];
                        int x0 = x > 1 ? x - 2 : 0;
                        int x1 = x > 0 ? x - 1 : 0;
                        int x3 = x < Mw - 1 ? x + 1 : Mw - 1;
                        int x4 = x < Mw - 2 ? x + 2 : Mw - 1;

                        int d0 = cLum - lRow[x0]; if (d0 < 0) d0 = -d0;
                        int d1 = cLum - lRow[x1]; if (d1 < 0) d1 = -d1;
                        int d3 = cLum - lRow[x3]; if (d3 < 0) d3 = -d3;
                        int d4 = cLum - lRow[x4]; if (d4 < 0) d4 = -d4;

                        float w0 = 1f / (1f + d0 * 0.12f);
                        float w1 = 2f / (1f + d1 * 0.12f);
                        float w2 = 3f;
                        float w3 = 2f / (1f + d3 * 0.12f);
                        float w4 = 1f / (1f + d4 * 0.12f);
                        float wSum = w0 + w1 + w2 + w3 + w4;
                        tRow[x] = (sRow[x0] * w0 + sRow[x1] * w1 + cVal * w2 + sRow[x3] * w3 + sRow[x4] * w4) / wSum;
                    }
                }

                for (int y = 0; y < Mh; y++)
                {
                    int y0 = y > 1 ? y - 2 : 0;
                    int y1 = y > 0 ? y - 1 : 0;
                    int y3 = y < Mh - 1 ? y + 1 : Mh - 1;
                    int y4 = y < Mh - 2 ? y + 2 : Mh - 1;
                    float* r0 = pT + y0 * Mw, r1 = pT + y1 * Mw, r2 = pT + y * Mw, r3 = pT + y3 * Mw, r4 = pT + y4 * Mw;
                    byte* l0 = pL + y0 * Mw, l1 = pL + y1 * Mw, l2 = pL + y * Mw, l3 = pL + y3 * Mw, l4 = pL + y4 * Mw;
                    float* dRow = pS + y * Mw;
                    for (int x = 0; x < Mw; x++)
                    {
                        float cVal = r2[x];
                        if (cVal <= 0.01f || cVal >= 0.99f) { dRow[x] = cVal; continue; }
                        int cLum = l2[x];
                        int d0 = cLum - l0[x]; if (d0 < 0) d0 = -d0;
                        int d1 = cLum - l1[x]; if (d1 < 0) d1 = -d1;
                        int d3 = cLum - l3[x]; if (d3 < 0) d3 = -d3;
                        int d4 = cLum - l4[x]; if (d4 < 0) d4 = -d4;

                        float w0 = 1f / (1f + d0 * 0.12f);
                        float w1 = 2f / (1f + d1 * 0.12f);
                        float w2 = 3f;
                        float w3 = 2f / (1f + d3 * 0.12f);
                        float w4 = 1f / (1f + d4 * 0.12f);
                        float wSum = w0 + w1 + w2 + w3 + w4;
                        dRow[x] = (r0[x] * w0 + r1[x] * w1 + cVal * w2 + r3[x] * w3 + r4[x] * w4) / wSum;
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
