#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <ks.h>
#include <ksproxy.h>
#include <ksmedia.h>
#include <initguid.h>
#include <atomic>
#include <cstring>
#include <cstdio>

// {A1A9C0DE-0001-4000-8000-414C41504341}
DEFINE_GUID(CLSID_AlapMfCam,
    0xA1A9C0DE, 0x0001, 0x4000, 0x80, 0x00, 0x41, 0x4C, 0x41, 0x50, 0x43, 0x41);

#ifndef MF_DEVICESTREAM_FRAMESERVER_SHARED
DEFINE_GUID(MF_DEVICESTREAM_FRAMESERVER_SHARED,
    0x1cb373bc, 0xc97b, 0x4a50, 0x93, 0x76, 0x4f, 0x6f, 0x77, 0x5e, 0x3b, 0x07);
#endif

#ifndef MF_DEVICESTREAM_STREAM_CATEGORY
DEFINE_GUID(MF_DEVICESTREAM_STREAM_CATEGORY,
    0x2939e7b8, 0xa62e, 0x4579, 0xb6, 0x74, 0xd4, 0x07, 0x3d, 0xfa, 0xbb, 0xba);
#endif

#ifndef MF_DEVICESTREAM_STREAM_ID
DEFINE_GUID(MF_DEVICESTREAM_STREAM_ID,
    0x11bd5120, 0xd124, 0x446b, 0x88, 0xe6, 0x17, 0x06, 0x02, 0x57, 0xff, 0x90);
#endif

static const GUID ALAP_PINNAME_VIDEO_CAPTURE =
    { 0xfb6c4281, 0x0353, 0x11d1, { 0x90, 0x5f, 0x00, 0x00, 0xc0, 0xcc, 0x16, 0xba } };
static const GUID ALAP_IID_IKsControl =
    { 0x28f54685, 0x06fd, 0x11d2, { 0xb2, 0x7a, 0x00, 0xa0, 0xc9, 0x22, 0x31, 0x96 } };

static HMODULE g_hModule = nullptr;
static std::atomic<long> g_dllRef{0};

struct SharedFrameHeader {
    volatile LONG seq;
    int width;
    int height;
    int stride;
    int format; // 0 = BGR24
    volatile LONG active;
    LONGLONG timestamp100ns;
};

static constexpr int MAX_SHARED_BYTES = 64 + 1920 * 1080 * 3;
static constexpr const wchar_t* SHARED_MEM_NAME = L"Global\\AlapCamera_MF_Frame";

static HRESULT CreateVideoMediaType(UINT32 w, UINT32 h, IMFMediaType** ppType) {
    IMFMediaType* pType = nullptr;
    HRESULT hr = MFCreateMediaType(&pType);
    if (FAILED(hr)) return hr;
    pType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    pType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    pType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    pType->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
    MFSetAttributeSize(pType, MF_MT_FRAME_SIZE, w, h);
    MFSetAttributeRatio(pType, MF_MT_FRAME_RATE, 30, 1);
    MFSetAttributeRatio(pType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    pType->SetUINT32(MF_MT_DEFAULT_STRIDE, w);
    pType->SetUINT32(MF_MT_SAMPLE_SIZE, w * h * 3 / 2);
    *ppType = pType;
    return S_OK;
}

class AlapMediaSource;

class AlapMediaStream : public IMFMediaStream {
    std::atomic<long> m_ref{1};
    CRITICAL_SECTION m_cs;
    AlapMediaSource* m_source;
    IMFStreamDescriptor* m_sd = nullptr;
    IMFMediaEventQueue* m_events = nullptr;
    bool m_shutdown = false;
    bool m_started = false;
    UINT32 m_width = 1280;
    UINT32 m_height = 720;
    LONGLONG m_startTime = 0;
    LARGE_INTEGER m_qpcFreq{};
    LARGE_INTEGER m_qpcStart{};
    HANDLE m_hMap = nullptr;
    const BYTE* m_pView = nullptr;
    BYTE* m_tempBgr = nullptr;

    void EnsureSharedMem() {
        if (m_pView) return;
        m_hMap = OpenFileMappingW(FILE_MAP_READ, FALSE, SHARED_MEM_NAME);
        if (!m_hMap) {
            HANDLE hFile = CreateFileW(
                L"C:\\ProgramData\\AlapCamera\\driver\\frame_shm.bin",
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                nullptr);
            if (hFile != INVALID_HANDLE_VALUE) {
                m_hMap = CreateFileMappingW(hFile, nullptr, PAGE_READONLY, 0, 0, nullptr);
                CloseHandle(hFile);
            }
        }
        if (m_hMap) {
            m_pView = (const BYTE*)MapViewOfFile(m_hMap, FILE_MAP_READ, 0, 0, 0);
            if (!m_pView) {
                CloseHandle(m_hMap);
                m_hMap = nullptr;
            }
        }
    }

    static inline BYTE ClampByte(int v) {
        return (BYTE)(v < 0 ? 0 : (v > 255 ? 255 : v));
    }

    void FillSampleNV12(BYTE* dst) {
        UINT32 w = m_width;
        UINT32 h = m_height;
        BYTE* yPlane = dst;
        BYTE* uvPlane = dst + w * h;

        EnsureSharedMem();
        bool gotFrame = false;
        int srcW = 0, srcH = 0, srcStride = 0;

        if (m_pView) {
            const SharedFrameHeader* hdr = (const SharedFrameHeader*)m_pView;
            if (hdr->active && hdr->width > 0 && hdr->height > 0 && hdr->width <= 1920 && hdr->height <= 1080) {
                LONG s1 = hdr->seq;
                MemoryBarrier();
                if ((s1 & 1) == 0) {
                    srcW = hdr->width;
                    srcH = hdr->height;
                    srcStride = hdr->stride > 0 ? hdr->stride : srcW * 3;
                    size_t copyBytes = (size_t)srcH * (size_t)srcStride;
                    if (copyBytes <= (size_t)(MAX_SHARED_BYTES - 64)) {
                        if (!m_tempBgr) m_tempBgr = new BYTE[1920 * 1080 * 3];
                        memcpy(m_tempBgr, m_pView + 64, copyBytes);
                        MemoryBarrier();
                        LONG s2 = hdr->seq;
                        if (s1 == s2) gotFrame = true;
                    }
                }
            }
        }

        if (!gotFrame) {
            memset(yPlane, 24, (size_t)w * h);
            memset(uvPlane, 128, (size_t)w * h / 2);
            return;
        }

        for (UINT32 y = 0; y < h; ++y) {
            UINT32 sy = (UINT32)((ULONGLONG)y * (UINT32)srcH / h);
            const BYTE* row = m_tempBgr + (size_t)sy * (size_t)srcStride;
            BYTE* yRow = yPlane + (size_t)y * w;
            bool doUV = ((y & 1) == 0);
            BYTE* uvRow = doUV ? (uvPlane + (size_t)(y >> 1) * w) : nullptr;

            for (UINT32 x = 0; x < w; ++x) {
                UINT32 sx = (UINT32)((ULONGLONG)x * (UINT32)srcW / w);
                const BYTE* px = row + (size_t)sx * 3;
                int b = px[0], g = px[1], r = px[2];
                int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                yRow[x] = ClampByte(yVal);
                if (doUV && ((x & 1) == 0)) {
                    int uVal = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    int vVal = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                    uvRow[x] = ClampByte(uVal);
                    uvRow[x + 1] = ClampByte(vVal);
                }
            }
        }
    }

public:
    AlapMediaStream(AlapMediaSource* src, IMFStreamDescriptor* sd, UINT32 w, UINT32 h)
        : m_source(src), m_sd(sd), m_width(w), m_height(h) {
        g_dllRef.fetch_add(1);
        InitializeCriticalSection(&m_cs);
        if (m_sd) m_sd->AddRef();
        MFCreateEventQueue(&m_events);
        QueryPerformanceFrequency(&m_qpcFreq);
        QueryPerformanceCounter(&m_qpcStart);
    }

    virtual ~AlapMediaStream() {
        if (m_pView) UnmapViewOfFile(m_pView);
        if (m_hMap) CloseHandle(m_hMap);
        delete[] m_tempBgr;
        if (m_events) m_events->Release();
        if (m_sd) m_sd->Release();
        DeleteCriticalSection(&m_cs);
        g_dllRef.fetch_sub(1);
    }

    void SetResolution(UINT32 w, UINT32 h) {
        EnterCriticalSection(&m_cs);
        m_width = w;
        m_height = h;
        LeaveCriticalSection(&m_cs);
    }

    HRESULT StartStream(const PROPVARIANT* varStart) {
        EnterCriticalSection(&m_cs);
        if (m_shutdown) { LeaveCriticalSection(&m_cs); return MF_E_SHUTDOWN; }
        QueryPerformanceCounter(&m_qpcStart);
        m_startTime = 0;
        HRESULT hr = S_OK;
        if (m_started) {
            hr = m_events->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, varStart);
        } else {
            m_started = true;
            hr = m_events->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, varStart);
        }
        LeaveCriticalSection(&m_cs);
        return hr;
    }

    HRESULT StopStream() {
        EnterCriticalSection(&m_cs);
        if (m_shutdown) { LeaveCriticalSection(&m_cs); return MF_E_SHUTDOWN; }
        m_started = false;
        HRESULT hr = m_events->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
        LeaveCriticalSection(&m_cs);
        return hr;
    }

    HRESULT ShutdownStream() {
        EnterCriticalSection(&m_cs);
        m_shutdown = true;
        if (m_events) m_events->Shutdown();
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IMFMediaEventGenerator || riid == IID_IMFMediaStream) {
            *ppv = static_cast<IMFMediaStream*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return (ULONG)m_ref.fetch_add(1) + 1; }
    STDMETHODIMP_(ULONG) Release() override {
        long c = m_ref.fetch_sub(1) - 1;
        if (c == 0) delete this;
        return (ULONG)c;
    }

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) override {
        return m_events->GetEvent(dwFlags, ppEvent);
    }
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState) override {
        return m_events->BeginGetEvent(pCallback, punkState);
    }
    STDMETHODIMP EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent) override {
        return m_events->EndGetEvent(pResult, ppEvent);
    }
    STDMETHODIMP QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue) override {
        return m_events->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue);
    }

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource** ppMediaSource) override;
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor) override {
        if (!ppStreamDescriptor) return E_POINTER;
        EnterCriticalSection(&m_cs);
        if (m_shutdown) { LeaveCriticalSection(&m_cs); return MF_E_SHUTDOWN; }
        *ppStreamDescriptor = m_sd;
        m_sd->AddRef();
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    STDMETHODIMP RequestSample(IUnknown* pToken) override {
        EnterCriticalSection(&m_cs);
        if (m_shutdown) { LeaveCriticalSection(&m_cs); return MF_E_SHUTDOWN; }
        if (!m_started) { LeaveCriticalSection(&m_cs); return MF_E_INVALIDREQUEST; }

        UINT32 bufSize = m_width * m_height * 3 / 2;
        IMFMediaBuffer* pBuf = nullptr;
        HRESULT hr = MFCreateMemoryBuffer(bufSize, &pBuf);
        if (SUCCEEDED(hr)) {
            BYTE* pData = nullptr;
            hr = pBuf->Lock(&pData, nullptr, nullptr);
            if (SUCCEEDED(hr)) {
                FillSampleNV12(pData);
                pBuf->Unlock();
                pBuf->SetCurrentLength(bufSize);
            }
        }
        IMFSample* pSample = nullptr;
        if (SUCCEEDED(hr)) {
            hr = MFCreateSample(&pSample);
            if (SUCCEEDED(hr)) {
                pSample->AddBuffer(pBuf);
                LARGE_INTEGER now;
                QueryPerformanceCounter(&now);
                LONGLONG ts = (now.QuadPart - m_qpcStart.QuadPart) * 10000000LL / m_qpcFreq.QuadPart;
                pSample->SetSampleTime(ts);
                pSample->SetSampleDuration(333333LL);
                pSample->SetUINT32(MFSampleExtension_CleanPoint, TRUE);
                if (pToken) pSample->SetUnknown(MFSampleExtension_Token, pToken);
                hr = m_events->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, pSample);
                pSample->Release();
            }
        }
        if (pBuf) pBuf->Release();
        LeaveCriticalSection(&m_cs);
        return hr;
    }
};

class AlapMediaSource : public IMFMediaSourceEx, public IMFGetService, public IKsControl {
    std::atomic<long> m_ref{1};
    CRITICAL_SECTION m_cs;
    IMFMediaEventQueue* m_events = nullptr;
    IMFAttributes* m_srcAttrs = nullptr;
    IMFAttributes* m_streamAttrs = nullptr;
    IMFPresentationDescriptor* m_pd = nullptr;
    AlapMediaStream* m_stream = nullptr;
    bool m_shutdown = false;

public:
    AlapMediaSource() {
        g_dllRef.fetch_add(1);
        InitializeCriticalSection(&m_cs);
        MFCreateEventQueue(&m_events);
        MFCreateAttributes(&m_srcAttrs, 4);
        MFCreateAttributes(&m_streamAttrs, 4);
        m_streamAttrs->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, ALAP_PINNAME_VIDEO_CAPTURE);
        m_streamAttrs->SetUINT32(MF_DEVICESTREAM_STREAM_ID, 0);
        m_streamAttrs->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);

        IMFMediaType* types[3] = { nullptr, nullptr, nullptr };
        CreateVideoMediaType(1280, 720, &types[0]);
        CreateVideoMediaType(1920, 1080, &types[1]);
        CreateVideoMediaType(640, 480, &types[2]);

        IMFStreamDescriptor* sd = nullptr;
        MFCreateStreamDescriptor(0, 3, types, &sd);
        IMFMediaTypeHandler* handler = nullptr;
        if (sd && SUCCEEDED(sd->GetMediaTypeHandler(&handler))) {
            handler->SetCurrentMediaType(types[0]);
            handler->Release();
        }
        for (int i = 0; i < 3; ++i) if (types[i]) types[i]->Release();

        MFCreatePresentationDescriptor(1, &sd, &m_pd);
        m_pd->SelectStream(0);
        m_stream = new AlapMediaStream(this, sd, 1280, 720);
        if (sd) sd->Release();
    }

    virtual ~AlapMediaSource() {
        if (m_stream) m_stream->Release();
        if (m_pd) m_pd->Release();
        if (m_streamAttrs) m_streamAttrs->Release();
        if (m_srcAttrs) m_srcAttrs->Release();
        if (m_events) m_events->Release();
        DeleteCriticalSection(&m_cs);
        g_dllRef.fetch_sub(1);
    }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IMFMediaEventGenerator ||
            riid == IID_IMFMediaSource || riid == IID_IMFMediaSourceEx) {
            *ppv = static_cast<IMFMediaSourceEx*>(this);
            AddRef();
            return S_OK;
        }
        if (riid == IID_IMFGetService) {
            *ppv = static_cast<IMFGetService*>(this);
            AddRef();
            return S_OK;
        }
        if (riid == ALAP_IID_IKsControl) {
            *ppv = static_cast<IKsControl*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return (ULONG)m_ref.fetch_add(1) + 1; }
    STDMETHODIMP_(ULONG) Release() override {
        long c = m_ref.fetch_sub(1) - 1;
        if (c == 0) delete this;
        return (ULONG)c;
    }

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) override { return m_events->GetEvent(dwFlags, ppEvent); }
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState) override { return m_events->BeginGetEvent(pCallback, punkState); }
    STDMETHODIMP EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent) override { return m_events->EndGetEvent(pResult, ppEvent); }
    STDMETHODIMP QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue) override {
        return m_events->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue);
    }

    // IMFMediaSource
    STDMETHODIMP GetCharacteristics(DWORD* pdwCharacteristics) override {
        if (!pdwCharacteristics) return E_POINTER;
        *pdwCharacteristics = MFMEDIASOURCE_IS_LIVE;
        return S_OK;
    }
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** ppPresentationDescriptor) override {
        if (!ppPresentationDescriptor) return E_POINTER;
        EnterCriticalSection(&m_cs);
        HRESULT hr = m_shutdown ? MF_E_SHUTDOWN : m_pd->Clone(ppPresentationDescriptor);
        LeaveCriticalSection(&m_cs);
        return hr;
    }
    STDMETHODIMP Start(IMFPresentationDescriptor* pPD, const GUID* pguidTimeFormat, const PROPVARIANT* pvarStartPosition) override {
        if (!pPD || !pvarStartPosition) return E_POINTER;
        if (pguidTimeFormat && *pguidTimeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;
        EnterCriticalSection(&m_cs);
        if (m_shutdown) { LeaveCriticalSection(&m_cs); return MF_E_SHUTDOWN; }

        IMFStreamDescriptor* sd = nullptr;
        BOOL selected = FALSE;
        if (SUCCEEDED(pPD->GetStreamDescriptorByIndex(0, &selected, &sd)) && sd) {
            IMFMediaTypeHandler* handler = nullptr;
            IMFMediaType* curType = nullptr;
            if (SUCCEEDED(sd->GetMediaTypeHandler(&handler)) && handler) {
                if (SUCCEEDED(handler->GetCurrentMediaType(&curType)) && curType) {
                    UINT32 w = 1280, h = 720;
                    if (SUCCEEDED(MFGetAttributeSize(curType, MF_MT_FRAME_SIZE, &w, &h))) {
                        m_stream->SetResolution(w, h);
                    }
                    curType->Release();
                }
                handler->Release();
            }
            sd->Release();
        }

        m_events->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, pvarStartPosition);
        if (selected) {
            m_events->QueueEventParamUnk(MENewStream, GUID_NULL, S_OK, m_stream);
            m_stream->StartStream(pvarStartPosition);
        }
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    STDMETHODIMP Stop() override {
        EnterCriticalSection(&m_cs);
        if (m_shutdown) { LeaveCriticalSection(&m_cs); return MF_E_SHUTDOWN; }
        m_stream->StopStream();
        HRESULT hr = m_events->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
        LeaveCriticalSection(&m_cs);
        return hr;
    }
    STDMETHODIMP Pause() override { return MF_E_INVALID_STATE_TRANSITION; }
    STDMETHODIMP Shutdown() override {
        EnterCriticalSection(&m_cs);
        m_shutdown = true;
        if (m_stream) m_stream->ShutdownStream();
        if (m_events) m_events->Shutdown();
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }

    // IMFMediaSourceEx
    STDMETHODIMP GetSourceAttributes(IMFAttributes** ppAttributes) override {
        if (!ppAttributes) return E_POINTER;
        *ppAttributes = m_srcAttrs;
        m_srcAttrs->AddRef();
        return S_OK;
    }
    STDMETHODIMP GetStreamAttributes(DWORD dwStreamIdentifier, IMFAttributes** ppAttributes) override {
        if (!ppAttributes) return E_POINTER;
        if (dwStreamIdentifier != 0) return MF_E_INVALIDSTREAMNUMBER;
        *ppAttributes = m_streamAttrs;
        m_streamAttrs->AddRef();
        return S_OK;
    }
    STDMETHODIMP SetD3DManager(IUnknown* pManager) override { return S_OK; }

    // IMFGetService
    STDMETHODIMP GetService(REFGUID guidService, REFIID riid, LPVOID* ppvObject) override {
        return MF_E_UNSUPPORTED_SERVICE;
    }

    // IKsControl
    STDMETHODIMP KsProperty(PKSPROPERTY Property, ULONG PropertyLength, LPVOID PropertyData, ULONG DataLength, ULONG* BytesReturned) override {
        if (BytesReturned) *BytesReturned = 0;
        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }
    STDMETHODIMP KsMethod(PKSMETHOD Method, ULONG MethodLength, LPVOID MethodData, ULONG DataLength, ULONG* BytesReturned) override {
        if (BytesReturned) *BytesReturned = 0;
        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }
    STDMETHODIMP KsEvent(PKSEVENT Event, ULONG EventLength, LPVOID EventData, ULONG DataLength, ULONG* BytesReturned) override {
        if (BytesReturned) *BytesReturned = 0;
        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }
};

STDMETHODIMP AlapMediaStream::GetMediaSource(IMFMediaSource** ppMediaSource) {
    if (!ppMediaSource) return E_POINTER;
    EnterCriticalSection(&m_cs);
    if (m_shutdown) { LeaveCriticalSection(&m_cs); return MF_E_SHUTDOWN; }
    *ppMediaSource = static_cast<IMFMediaSource*>(m_source);
    m_source->AddRef();
    LeaveCriticalSection(&m_cs);
    return S_OK;
}

class AlapActivate : public IMFActivate {
    std::atomic<long> m_ref{1};
    IMFAttributes* m_attrs = nullptr;
public:
    AlapActivate() {
        g_dllRef.fetch_add(1);
        MFCreateAttributes(&m_attrs, 4);
    }
    virtual ~AlapActivate() {
        if (m_attrs) m_attrs->Release();
        g_dllRef.fetch_sub(1);
    }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IMFAttributes || riid == IID_IMFActivate) {
            *ppv = static_cast<IMFActivate*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return (ULONG)m_ref.fetch_add(1) + 1; }
    STDMETHODIMP_(ULONG) Release() override {
        long c = m_ref.fetch_sub(1) - 1;
        if (c == 0) delete this;
        return (ULONG)c;
    }

    // IMFAttributes delegation
    STDMETHODIMP GetItem(REFGUID k, PROPVARIANT* v) override { return m_attrs->GetItem(k, v); }
    STDMETHODIMP GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t) override { return m_attrs->GetItemType(k, t); }
    STDMETHODIMP CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r) override { return m_attrs->CompareItem(k, v, r); }
    STDMETHODIMP Compare(IMFAttributes* t, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r) override { return m_attrs->Compare(t, m, r); }
    STDMETHODIMP GetUINT32(REFGUID k, UINT32* v) override { return m_attrs->GetUINT32(k, v); }
    STDMETHODIMP GetUINT64(REFGUID k, UINT64* v) override { return m_attrs->GetUINT64(k, v); }
    STDMETHODIMP GetDouble(REFGUID k, double* v) override { return m_attrs->GetDouble(k, v); }
    STDMETHODIMP GetGUID(REFGUID k, GUID* v) override { return m_attrs->GetGUID(k, v); }
    STDMETHODIMP GetStringLength(REFGUID k, UINT32* l) override { return m_attrs->GetStringLength(k, l); }
    STDMETHODIMP GetString(REFGUID k, LPWSTR v, UINT32 c, UINT32* l) override { return m_attrs->GetString(k, v, c, l); }
    STDMETHODIMP GetAllocatedString(REFGUID k, LPWSTR* v, UINT32* l) override { return m_attrs->GetAllocatedString(k, v, l); }
    STDMETHODIMP GetBlobSize(REFGUID k, UINT32* s) override { return m_attrs->GetBlobSize(k, s); }
    STDMETHODIMP GetBlob(REFGUID k, UINT8* b, UINT32 s, UINT32* r) override { return m_attrs->GetBlob(k, b, s, r); }
    STDMETHODIMP GetAllocatedBlob(REFGUID k, UINT8** b, UINT32* s) override { return m_attrs->GetAllocatedBlob(k, b, s); }
    STDMETHODIMP GetUnknown(REFGUID k, REFIID i, LPVOID* p) override { return m_attrs->GetUnknown(k, i, p); }
    STDMETHODIMP SetItem(REFGUID k, REFPROPVARIANT v) override { return m_attrs->SetItem(k, v); }
    STDMETHODIMP DeleteItem(REFGUID k) override { return m_attrs->DeleteItem(k); }
    STDMETHODIMP DeleteAllItems() override { return m_attrs->DeleteAllItems(); }
    STDMETHODIMP SetUINT32(REFGUID k, UINT32 v) override { return m_attrs->SetUINT32(k, v); }
    STDMETHODIMP SetUINT64(REFGUID k, UINT64 v) override { return m_attrs->SetUINT64(k, v); }
    STDMETHODIMP SetDouble(REFGUID k, double v) override { return m_attrs->SetDouble(k, v); }
    STDMETHODIMP SetGUID(REFGUID k, REFGUID v) override { return m_attrs->SetGUID(k, v); }
    STDMETHODIMP SetString(REFGUID k, LPCWSTR v) override { return m_attrs->SetString(k, v); }
    STDMETHODIMP SetBlob(REFGUID k, const UINT8* b, UINT32 s) override { return m_attrs->SetBlob(k, b, s); }
    STDMETHODIMP SetUnknown(REFGUID k, IUnknown* p) override { return m_attrs->SetUnknown(k, p); }
    STDMETHODIMP LockStore() override { return m_attrs->LockStore(); }
    STDMETHODIMP UnlockStore() override { return m_attrs->UnlockStore(); }
    STDMETHODIMP GetCount(UINT32* c) override { return m_attrs->GetCount(c); }
    STDMETHODIMP GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v) override { return m_attrs->GetItemByIndex(i, k, v); }
    STDMETHODIMP CopyAllItems(IMFAttributes* d) override { return m_attrs->CopyAllItems(d); }

    // IMFActivate
    STDMETHODIMP ActivateObject(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        AlapMediaSource* src = new AlapMediaSource();
        HRESULT hr = src->QueryInterface(riid, ppv);
        src->Release();
        return hr;
    }
    STDMETHODIMP ShutdownObject() override { return S_OK; }
    STDMETHODIMP DetachObject() override { return S_OK; }
};

class AlapClassFactory : public IClassFactory {
    std::atomic<long> m_ref{1};
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return (ULONG)m_ref.fetch_add(1) + 1; }
    STDMETHODIMP_(ULONG) Release() override {
        long c = m_ref.fetch_sub(1) - 1;
        if (c == 0) delete this;
        return (ULONG)c;
    }
    STDMETHODIMP CreateInstance(IUnknown* pOuter, REFIID riid, void** ppv) override {
        if (pOuter) return CLASS_E_NOAGGREGATION;
        AlapActivate* act = new AlapActivate();
        HRESULT hr = act->QueryInterface(riid, ppv);
        act->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL fLock) override {
        if (fLock) g_dllRef.fetch_add(1);
        else g_dllRef.fetch_sub(1);
        return S_OK;
    }
};

extern "C" {

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID) {
    if (fdwReason == DLL_PROCESS_ATTACH) {
        g_hModule = hinstDLL;
        DisableThreadLibraryCalls(hinstDLL);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) {
    if (!ppv) return E_POINTER;
    if (rclsid != CLSID_AlapMfCam) { *ppv = nullptr; return CLASS_E_CLASSNOTAVAILABLE; }
    AlapClassFactory* cf = new AlapClassFactory();
    HRESULT hr = cf->QueryInterface(riid, ppv);
    cf->Release();
    return hr;
}

STDAPI DllCanUnloadNow() {
    return g_dllRef.load() == 0 ? S_OK : S_FALSE;
}

STDAPI DllRegisterServer() {
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(g_hModule, path, MAX_PATH);
    const wchar_t* clsidKey = L"SOFTWARE\\Classes\\CLSID\\{A1A9C0DE-0001-4000-8000-414C41504341}";
    HKEY hk = nullptr;
    if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, clsidKey, 0, nullptr, 0, KEY_WRITE, nullptr, &hk, nullptr) != ERROR_SUCCESS)
        return E_ACCESSDENIED;
    const wchar_t* desc = L"AlapCamera MediaFoundation Source";
    RegSetValueExW(hk, nullptr, 0, REG_SZ, (const BYTE*)desc, (DWORD)((wcslen(desc) + 1) * sizeof(wchar_t)));
    HKEY hkInproc = nullptr;
    if (RegCreateKeyExW(hk, L"InprocServer32", 0, nullptr, 0, KEY_WRITE, nullptr, &hkInproc, nullptr) == ERROR_SUCCESS) {
        RegSetValueExW(hkInproc, nullptr, 0, REG_SZ, (const BYTE*)path, (DWORD)((wcslen(path) + 1) * sizeof(wchar_t)));
        const wchar_t* tm = L"Both";
        RegSetValueExW(hkInproc, L"ThreadingModel", 0, REG_SZ, (const BYTE*)tm, (DWORD)((wcslen(tm) + 1) * sizeof(wchar_t)));
        RegCloseKey(hkInproc);
    }
    RegCloseKey(hk);
    return S_OK;
}

STDAPI DllUnregisterServer() {
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Classes\\CLSID\\{A1A9C0DE-0001-4000-8000-414C41504341}");
    return S_OK;
}

}
