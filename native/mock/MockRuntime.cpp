// LanaMockXR: a scriptable virtual headset, implemented as a minimal OpenXR runtime.
// Selected per process through XR_RUNTIME_JSON, so it never touches the system's active runtime.
//
// Control files live in %USERPROFILE%\AppData\LocalLow\LanaVR\lab:
//   mock_head.txt  "x y z yawDegrees [ipd] [pitchDegrees]" read every frame: the scripted head pose in LOCAL space
//   mock_dump.txt  created by a test: on the next frame with layers, both swapchain images are written
//                  to mock_left.bmp and mock_right.bmp, and layer data goes to mock_layers.txt, then
//                  this file is deleted
//   mock_exit.txt  created by a test: asks the application to end the session
//   mock_log.txt   lifecycle log, including API misuse that a real runtime could reject
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <timeapi.h>
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <openxr/openxr_loader_negotiation.h>
#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {

std::string LabDir() {
    char p[MAX_PATH];
    GetEnvironmentVariableA("USERPROFILE", p, MAX_PATH);
    return std::string(p) + "\\AppData\\LocalLow\\LanaVR\\lab\\";
}
std::mutex g_logLock;
void Log(const char* fmt, ...) {
    std::lock_guard<std::mutex> l(g_logLock);
    FILE* f = nullptr;
    fopen_s(&f, (LabDir() + "mock_log.txt").c_str(), "a");
    if (!f) return;
    va_list a; va_start(a, fmt); vfprintf(f, fmt, a); va_end(a);
    fputc('\n', f);
    fclose(f);
}

const XrInstance kInstance = (XrInstance)0x1001;
const XrSystemId kSystem = 42;
const XrSession kSession = (XrSession)0x2001;
const int kW = 2064, kH = 2272;
const XrDuration kPeriod = 11111111;  // 90 Hz

struct Space { XrReferenceSpaceType type; XrPosef offset; };
struct Chain {
    std::vector<ID3D11Texture2D*> images;
    uint32_t w = 0, h = 0;
    int64_t format = 0;
    uint32_t next = 0;
    int acquired = -1, lastReleased = -1;
    bool waited = false;
};

ID3D11Device* g_dev = nullptr;
std::deque<XrSessionState> g_events;
XrSessionState g_state = XR_SESSION_STATE_UNKNOWN;
bool g_exitInstanceLoss = false;
bool g_frameBegun = false;
int g_waitsWithoutBegin = 0;
long long g_frames = 0, g_badCalls = 0;
auto g_start = std::chrono::steady_clock::now();
auto g_nextFrame = g_start;

XrTime Now() {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - g_start).count() + 1000000000;
}
void Bad(const char* what) { g_badCalls++; Log("BAD CALL: %s", what); }

struct Head { float x = 0, y = 0, z = 0, yaw = 0, ipd = 0.063f, pitch = 0; };
Head ReadHead() {
    Head h;
    FILE* f = nullptr;
    if (fopen_s(&f, (LabDir() + "mock_head.txt").c_str(), "r") == 0 && f) {
        fscanf_s(f, "%f %f %f %f %f %f", &h.x, &h.y, &h.z, &h.yaw, &h.ipd, &h.pitch);
        fclose(f);
    }
    return h;
}

// Yaw about +Y (positive turns left), then pitch about the head's +X (positive looks up).
XrQuaternionf HeadQuat(const Head& h) {
    float y = h.yaw * 3.14159265f / 360.f, p = h.pitch * 3.14159265f / 360.f;
    float sy = std::sin(y), cy = std::cos(y), sp = std::sin(p), cp = std::cos(p);
    return {cy * sp, sy * cp, -sy * sp, cy * cp};
}

bool Exists(const std::string& p) { return GetFileAttributesA(p.c_str()) != INVALID_FILE_ATTRIBUTES; }

void WriteBmp(ID3D11Texture2D* tex, uint32_t w, uint32_t h, const std::string& path) {
    ID3D11DeviceContext* ctx = nullptr;
    g_dev->GetImmediateContext(&ctx);
    D3D11_TEXTURE2D_DESC d{};
    tex->GetDesc(&d);
    d.Usage = D3D11_USAGE_STAGING;
    d.BindFlags = 0;
    d.MiscFlags = 0;
    d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ID3D11Texture2D* st = nullptr;
    if (FAILED(g_dev->CreateTexture2D(&d, nullptr, &st))) { Log("staging texture failed"); ctx->Release(); return; }
    ctx->CopyResource(st, tex);
    D3D11_MAPPED_SUBRESOURCE m{};
    if (SUCCEEDED(ctx->Map(st, 0, D3D11_MAP_READ, 0, &m))) {
        FILE* f = nullptr;
        fopen_s(&f, path.c_str(), "wb");
        if (f) {
            BITMAPFILEHEADER fh{};
            BITMAPINFOHEADER ih{};
            fh.bfType = 0x4D42;
            fh.bfOffBits = sizeof fh + sizeof ih;
            fh.bfSize = fh.bfOffBits + w * h * 4;
            ih.biSize = sizeof ih;
            ih.biWidth = (LONG)w;
            ih.biHeight = -(LONG)h;  // Top-down: row 0 of the swapchain image is the top of the headset view.
            ih.biPlanes = 1;
            ih.biBitCount = 32;
            fwrite(&fh, sizeof fh, 1, f);
            fwrite(&ih, sizeof ih, 1, f);
            std::vector<uint8_t> row(w * 4);
            for (uint32_t y = 0; y < h; y++) {
                const uint8_t* s = (const uint8_t*)m.pData + y * m.RowPitch;
                for (uint32_t x = 0; x < w; x++) {
                    row[x * 4] = s[x * 4 + 2];
                    row[x * 4 + 1] = s[x * 4 + 1];
                    row[x * 4 + 2] = s[x * 4];
                    row[x * 4 + 3] = 255;
                }
                fwrite(row.data(), 1, row.size(), f);
            }
            fclose(f);
        }
        ctx->Unmap(st, 0);
    }
    st->Release();
    ctx->Release();
}

#define MOCK(ret, name, ...) XRAPI_ATTR ret XRAPI_CALL name(__VA_ARGS__)

MOCK(XrResult, GetInstanceProcAddr, XrInstance, const char*, PFN_xrVoidFunction*);

MOCK(XrResult, EnumerateInstanceExtensionProperties, const char*, uint32_t cap, uint32_t* n, XrExtensionProperties* p) {
    *n = 1;
    if (cap == 0) return XR_SUCCESS;
    if (cap < 1) return XR_ERROR_SIZE_INSUFFICIENT;
    strcpy_s(p[0].extensionName, XR_KHR_D3D11_ENABLE_EXTENSION_NAME);
    p[0].extensionVersion = 1;
    return XR_SUCCESS;
}
MOCK(XrResult, CreateInstance, const XrInstanceCreateInfo* ci, XrInstance* out) {
    bool d3d = false;
    for (uint32_t i = 0; i < ci->enabledExtensionCount; i++)
        if (!strcmp(ci->enabledExtensionNames[i], XR_KHR_D3D11_ENABLE_EXTENSION_NAME)) d3d = true;
    Log("xrCreateInstance app=%s d3d11=%d", ci->applicationInfo.applicationName, d3d);
    timeBeginPeriod(1);
    *out = kInstance;
    return XR_SUCCESS;
}
MOCK(XrResult, DestroyInstance, XrInstance) { Log("xrDestroyInstance frames=%lld bad=%lld", g_frames, g_badCalls); return XR_SUCCESS; }
MOCK(XrResult, GetInstanceProperties, XrInstance, XrInstanceProperties* p) {
    p->runtimeVersion = XR_MAKE_VERSION(1, 0, 0);
    strcpy_s(p->runtimeName, "LanaMockXR");
    return XR_SUCCESS;
}
MOCK(XrResult, ResultToString, XrInstance, XrResult r, char s[XR_MAX_RESULT_STRING_SIZE]) { sprintf_s(s, XR_MAX_RESULT_STRING_SIZE, "XR_RESULT_%d", (int)r); return XR_SUCCESS; }
MOCK(XrResult, StructureTypeToString, XrInstance, XrStructureType t, char s[XR_MAX_STRUCTURE_NAME_SIZE]) { sprintf_s(s, XR_MAX_STRUCTURE_NAME_SIZE, "XR_TYPE_%d", (int)t); return XR_SUCCESS; }
MOCK(XrResult, GetSystem, XrInstance, const XrSystemGetInfo* i, XrSystemId* s) {
    if (i->formFactor != XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY) return XR_ERROR_FORM_FACTOR_UNSUPPORTED;
    *s = kSystem;
    return XR_SUCCESS;
}
MOCK(XrResult, GetSystemProperties, XrInstance, XrSystemId, XrSystemProperties* p) {
    p->systemId = kSystem;
    strcpy_s(p->systemName, "LanaMock HMD");
    p->graphicsProperties = {kW, kH, 16};
    p->trackingProperties = {XR_TRUE, XR_TRUE};
    return XR_SUCCESS;
}
MOCK(XrResult, EnumerateViewConfigurationViews, XrInstance, XrSystemId, XrViewConfigurationType, uint32_t cap, uint32_t* n, XrViewConfigurationView* v) {
    *n = 2;
    if (cap == 0) return XR_SUCCESS;
    if (cap < 2) return XR_ERROR_SIZE_INSUFFICIENT;
    for (int i = 0; i < 2; i++) {
        v[i].recommendedImageRectWidth = kW;
        v[i].recommendedImageRectHeight = kH;
        v[i].maxImageRectWidth = 4096;
        v[i].maxImageRectHeight = 4096;
        v[i].recommendedSwapchainSampleCount = 1;
        v[i].maxSwapchainSampleCount = 1;
    }
    return XR_SUCCESS;
}
MOCK(XrResult, GetD3D11Requirements, XrInstance, XrSystemId, XrGraphicsRequirementsD3D11KHR* r) {
    // Asks for the adapter with the most video memory, which is where a game on this PC runs.
    r->minFeatureLevel = D3D_FEATURE_LEVEL_11_0;
    r->adapterLuid = LUID{0, 0};
    IDXGIFactory1* factory = nullptr;
    if (SUCCEEDED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&factory))) {
        IDXGIAdapter1* a = nullptr;
        SIZE_T best = 0;
        for (UINT i = 0; factory->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; i++) {
            DXGI_ADAPTER_DESC1 d{};
            a->GetDesc1(&d);
            if (d.DedicatedVideoMemory > best) { best = d.DedicatedVideoMemory; r->adapterLuid = d.AdapterLuid; }
            a->Release();
        }
        factory->Release();
    }
    return XR_SUCCESS;
}
MOCK(XrResult, CreateSession, XrInstance, const XrSessionCreateInfo* ci, XrSession* s) {
    auto* b = (const XrGraphicsBindingD3D11KHR*)ci->next;
    if (!b || b->type != XR_TYPE_GRAPHICS_BINDING_D3D11_KHR || !b->device) { Bad("session without D3D11 binding"); return XR_ERROR_GRAPHICS_DEVICE_INVALID; }
    g_dev = b->device;
    *s = kSession;
    g_events.push_back(XR_SESSION_STATE_IDLE);
    g_events.push_back(XR_SESSION_STATE_READY);
    Log("xrCreateSession thread=%lu", GetCurrentThreadId());
    return XR_SUCCESS;
}
MOCK(XrResult, DestroySession, XrSession) { Log("xrDestroySession"); return XR_SUCCESS; }
MOCK(XrResult, BeginSession, XrSession, const XrSessionBeginInfo* b) {
    if (g_state != XR_SESSION_STATE_READY) Bad("xrBeginSession when not READY");
    Log("xrBeginSession cfg=%d", (int)b->primaryViewConfigurationType);
    g_events.push_back(XR_SESSION_STATE_SYNCHRONIZED);
    g_events.push_back(XR_SESSION_STATE_VISIBLE);
    g_events.push_back(XR_SESSION_STATE_FOCUSED);
    return XR_SUCCESS;
}
MOCK(XrResult, EndSession, XrSession) {
    Log("xrEndSession");
    g_events.push_back(XR_SESSION_STATE_IDLE);
    g_events.push_back(XR_SESSION_STATE_EXITING);
    return XR_SUCCESS;
}
MOCK(XrResult, RequestExitSession, XrSession) { g_events.push_back(XR_SESSION_STATE_STOPPING); return XR_SUCCESS; }
MOCK(XrResult, PollEvent, XrInstance, XrEventDataBuffer* e) {
    if (Exists(LabDir() + "mock_exit.txt")) {
        DeleteFileA((LabDir() + "mock_exit.txt").c_str());
        g_events.push_back(XR_SESSION_STATE_VISIBLE);
        g_events.push_back(XR_SESSION_STATE_SYNCHRONIZED);
        g_events.push_back(XR_SESSION_STATE_STOPPING);
    }
    if (g_events.empty()) return XR_EVENT_UNAVAILABLE;
    auto* s = (XrEventDataSessionStateChanged*)e;
    g_state = g_events.front();
    g_events.pop_front();
    s->type = XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED;
    s->next = nullptr;
    s->session = kSession;
    s->state = g_state;
    s->time = Now();
    Log("state -> %d", (int)g_state);
    return XR_SUCCESS;
}
MOCK(XrResult, EnumerateSwapchainFormats, XrSession, uint32_t cap, uint32_t* n, int64_t* f) {
    static const int64_t k[] = {DXGI_FORMAT_B8G8R8A8_UNORM_SRGB, DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_R8G8B8A8_UNORM};
    *n = 3;
    if (cap == 0) return XR_SUCCESS;
    if (cap < 3) return XR_ERROR_SIZE_INSUFFICIENT;
    memcpy(f, k, sizeof k);
    return XR_SUCCESS;
}
MOCK(XrResult, CreateSwapchain, XrSession, const XrSwapchainCreateInfo* ci, XrSwapchain* out) {
    auto* c = new Chain;
    c->w = ci->width;
    c->h = ci->height;
    c->format = ci->format;
    for (int i = 0; i < 3; i++) {
        D3D11_TEXTURE2D_DESC d{};
        d.Width = ci->width;
        d.Height = ci->height;
        d.MipLevels = 1;
        d.ArraySize = 1;
        d.Format = DXGI_FORMAT_R8G8B8A8_TYPELESS;
        d.SampleDesc.Count = 1;
        d.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        ID3D11Texture2D* t = nullptr;
        HRESULT hr = g_dev->CreateTexture2D(&d, nullptr, &t);
        if (FAILED(hr)) { Log("CreateTexture2D failed 0x%08X", hr); return XR_ERROR_RUNTIME_FAILURE; }
        c->images.push_back(t);
    }
    Log("xrCreateSwapchain %ux%u format=%lld", ci->width, ci->height, ci->format);
    *out = (XrSwapchain)c;
    return XR_SUCCESS;
}
MOCK(XrResult, DestroySwapchain, XrSwapchain s) {
    auto* c = (Chain*)s;
    for (auto* t : c->images) t->Release();
    delete c;
    return XR_SUCCESS;
}
MOCK(XrResult, EnumerateSwapchainImages, XrSwapchain s, uint32_t cap, uint32_t* n, XrSwapchainImageBaseHeader* imgs) {
    auto* c = (Chain*)s;
    *n = (uint32_t)c->images.size();
    if (cap == 0) return XR_SUCCESS;
    if (cap < *n) return XR_ERROR_SIZE_INSUFFICIENT;
    auto* o = (XrSwapchainImageD3D11KHR*)imgs;
    for (uint32_t i = 0; i < *n; i++) o[i].texture = c->images[i];
    return XR_SUCCESS;
}
MOCK(XrResult, AcquireSwapchainImage, XrSwapchain s, const XrSwapchainImageAcquireInfo*, uint32_t* idx) {
    auto* c = (Chain*)s;
    if (c->acquired >= 0) Bad("acquire twice without release");
    c->acquired = (int)c->next;
    *idx = c->next;
    c->next = (c->next + 1) % c->images.size();
    return XR_SUCCESS;
}
MOCK(XrResult, WaitSwapchainImage, XrSwapchain s, const XrSwapchainImageWaitInfo*) {
    auto* c = (Chain*)s;
    if (c->acquired < 0) Bad("wait without acquire");
    c->waited = true;
    return XR_SUCCESS;
}
MOCK(XrResult, ReleaseSwapchainImage, XrSwapchain s, const XrSwapchainImageReleaseInfo*) {
    auto* c = (Chain*)s;
    if (c->acquired < 0 || !c->waited) Bad("release without acquire+wait");
    c->lastReleased = c->acquired;
    c->acquired = -1;
    c->waited = false;
    return XR_SUCCESS;
}
MOCK(XrResult, EnumerateReferenceSpaces, XrSession, uint32_t cap, uint32_t* n, XrReferenceSpaceType* t) {
    // A floor 1.6 m below the LOCAL origin, unless a test asks for a headset that doesn't report one.
    bool stage = !Exists(LabDir() + "mock_nostage.txt");
    *n = stage ? 3 : 2;
    if (cap == 0) return XR_SUCCESS;
    if (cap < *n) return XR_ERROR_SIZE_INSUFFICIENT;
    t[0] = XR_REFERENCE_SPACE_TYPE_VIEW;
    t[1] = XR_REFERENCE_SPACE_TYPE_LOCAL;
    if (stage) t[2] = XR_REFERENCE_SPACE_TYPE_STAGE;
    return XR_SUCCESS;
}
MOCK(XrResult, CreateReferenceSpace, XrSession, const XrReferenceSpaceCreateInfo* ci, XrSpace* s) {
    *s = (XrSpace) new Space{ci->referenceSpaceType, ci->poseInReferenceSpace};
    return XR_SUCCESS;
}
MOCK(XrResult, DestroySpace, XrSpace s) { delete (Space*)s; return XR_SUCCESS; }
MOCK(XrResult, LocateSpace, XrSpace s, XrSpace, XrTime, XrSpaceLocation* l) {
    Head h = ReadHead();
    l->locationFlags = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT | XR_SPACE_LOCATION_POSITION_TRACKED_BIT | XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT;
    auto spaceType = ((Space*)s)->type;
    l->pose.position = spaceType == XR_REFERENCE_SPACE_TYPE_VIEW ? XrVector3f{h.x, h.y, h.z} : spaceType == XR_REFERENCE_SPACE_TYPE_STAGE ? XrVector3f{0, -1.6f, 0} : XrVector3f{0, 0, 0};
    l->pose.orientation = ((Space*)s)->type == XR_REFERENCE_SPACE_TYPE_VIEW ? HeadQuat(h) : XrQuaternionf{0, 0, 0, 1};
    return XR_SUCCESS;
}
MOCK(XrResult, LocateViews, XrSession, const XrViewLocateInfo*, XrViewState* vs, uint32_t cap, uint32_t* n, XrView* v) {
    *n = 2;
    if (cap == 0) return XR_SUCCESS;
    if (cap < 2) return XR_ERROR_SIZE_INSUFFICIENT;
    vs->viewStateFlags = XR_VIEW_STATE_POSITION_VALID_BIT | XR_VIEW_STATE_ORIENTATION_VALID_BIT | XR_VIEW_STATE_POSITION_TRACKED_BIT | XR_VIEW_STATE_ORIENTATION_TRACKED_BIT;
    Head h = ReadHead();
    float yr = h.yaw * 3.14159265f / 180.f, half = h.ipd / 2;
    for (int i = 0; i < 2; i++) {
        float s = i == 0 ? -half : half;
        v[i].pose.position = {h.x + std::cos(yr) * s, h.y, h.z - std::sin(yr) * s};
        v[i].pose.orientation = HeadQuat(h);
        v[i].fov = {-0.87f, 0.87f, 0.87f, -0.87f};
    }
    return XR_SUCCESS;
}
MOCK(XrResult, WaitFrame, XrSession, const XrFrameWaitInfo*, XrFrameState* fs) {
    if (g_waitsWithoutBegin > 1) Bad("xrWaitFrame called again with two frames not begun");
    g_waitsWithoutBegin++;
    auto now = std::chrono::steady_clock::now();
    // Sleep for most of the wait, then spin. Windows' default timer is too coarse for a 90 Hz deadline.
    if (g_nextFrame > now + std::chrono::milliseconds(2)) std::this_thread::sleep_until(g_nextFrame - std::chrono::milliseconds(2));
    while (std::chrono::steady_clock::now() < g_nextFrame) std::this_thread::yield();
    g_nextFrame = std::max(g_nextFrame, now) + std::chrono::nanoseconds(kPeriod);
    fs->predictedDisplayTime = Now() + kPeriod;
    fs->predictedDisplayPeriod = kPeriod;
    fs->shouldRender = XR_TRUE;
    return XR_SUCCESS;
}
MOCK(XrResult, BeginFrame, XrSession, const XrFrameBeginInfo*) {
    if (g_waitsWithoutBegin <= 0) Bad("xrBeginFrame without xrWaitFrame");
    g_waitsWithoutBegin--;
    if (g_frameBegun) { Bad("xrBeginFrame discarded an unfinished frame"); return XR_FRAME_DISCARDED; }
    g_frameBegun = true;
    return XR_SUCCESS;
}
MOCK(XrResult, EndFrame, XrSession, const XrFrameEndInfo* e) {
    if (!g_frameBegun) Bad("xrEndFrame without xrBeginFrame");
    g_frameBegun = false;
    g_frames++;
    if (e->layerCount > 0 && Exists(LabDir() + "mock_dump.txt")) {
        FILE* f = nullptr;
        fopen_s(&f, (LabDir() + "mock_layers.txt").c_str(), "w");
        if (f) fprintf(f, "frames=%lld bad=%lld layers=%u blend=%d\n", g_frames, g_badCalls, e->layerCount, (int)e->environmentBlendMode);
        // Quads are listed first, so layer lines keep their meaning with or without a sky behind them.
        int quadIndex = 0;
        for (uint32_t i = 0; i < e->layerCount; i++) {
            auto* q = (const XrCompositionLayerQuad*)e->layers[i];
            if (q->type == XR_TYPE_COMPOSITION_LAYER_PROJECTION) continue;
            if (q->type != XR_TYPE_COMPOSITION_LAYER_QUAD) { Bad("unexpected layer type"); continue; }
            auto* c = (Chain*)q->subImage.swapchain;
            if (f) fprintf(f, "layer%d eye=%d flags=0x%llx pos=%.4f,%.4f,%.4f rot=%.4f,%.4f,%.4f,%.4f size=%.4fx%.4f rect=%dx%d chain=%ux%u format=%lld\n", quadIndex++, (int)q->eyeVisibility,
                (unsigned long long)q->layerFlags, q->pose.position.x, q->pose.position.y, q->pose.position.z, q->pose.orientation.x, q->pose.orientation.y, q->pose.orientation.z, q->pose.orientation.w,
                q->size.width, q->size.height, q->subImage.imageRect.extent.width, q->subImage.imageRect.extent.height, c->w, c->h, c->format);
            if (c->lastReleased >= 0)
                WriteBmp(c->images[c->lastReleased], c->w, c->h, LabDir() + (q->eyeVisibility == XR_EYE_VISIBILITY_LEFT ? "mock_left.bmp" : "mock_right.bmp"));
        }
        for (uint32_t i = 0; i < e->layerCount; i++) {
            auto* p = (const XrCompositionLayerProjection*)e->layers[i];
            if (p->type != XR_TYPE_COMPOSITION_LAYER_PROJECTION) continue;
            if (i != 0) Bad("the projection layer is not the rearmost layer");
            if (p->viewCount != 2) { Bad("projection layer without two views"); continue; }
            for (int v = 0; v < 2; v++) {
                auto* c = (Chain*)p->views[v].subImage.swapchain;
                if (f) fprintf(f, "projection view=%d chain=%ux%u rect=%dx%d format=%lld fovL=%.3f\n", v, c->w, c->h, p->views[v].subImage.imageRect.extent.width, p->views[v].subImage.imageRect.extent.height, c->format, p->views[v].fov.angleLeft);
                if (c->lastReleased >= 0) WriteBmp(c->images[c->lastReleased], c->w, c->h, LabDir() + (v == 0 ? "mock_sky_left.bmp" : "mock_sky_right.bmp"));
            }
        }
        if (f) fclose(f);
        DeleteFileA((LabDir() + "mock_dump.txt").c_str());
    }
    return XR_SUCCESS;
}
MOCK(XrResult, StringToPath, XrInstance, const char*, XrPath* p) { *p = 1; return XR_SUCCESS; }
MOCK(XrResult, Unsupported) { Bad("call to an unimplemented function"); return XR_ERROR_FUNCTION_UNSUPPORTED; }

MOCK(XrResult, GetInstanceProcAddr, XrInstance, const char* name, PFN_xrVoidFunction* fn) {
#define F(api, impl) if (!strcmp(name, #api)) { *fn = (PFN_xrVoidFunction)impl; return XR_SUCCESS; }
    F(xrGetInstanceProcAddr, GetInstanceProcAddr) F(xrEnumerateInstanceExtensionProperties, EnumerateInstanceExtensionProperties)
    F(xrCreateInstance, CreateInstance) F(xrDestroyInstance, DestroyInstance) F(xrGetInstanceProperties, GetInstanceProperties)
    F(xrResultToString, ResultToString) F(xrStructureTypeToString, StructureTypeToString) F(xrGetSystem, GetSystem)
    F(xrGetSystemProperties, GetSystemProperties) F(xrEnumerateViewConfigurationViews, EnumerateViewConfigurationViews)
    F(xrGetD3D11GraphicsRequirementsKHR, GetD3D11Requirements) F(xrCreateSession, CreateSession) F(xrDestroySession, DestroySession)
    F(xrBeginSession, BeginSession) F(xrEndSession, EndSession) F(xrRequestExitSession, RequestExitSession) F(xrPollEvent, PollEvent)
    F(xrEnumerateSwapchainFormats, EnumerateSwapchainFormats) F(xrCreateSwapchain, CreateSwapchain) F(xrDestroySwapchain, DestroySwapchain)
    F(xrEnumerateSwapchainImages, EnumerateSwapchainImages) F(xrAcquireSwapchainImage, AcquireSwapchainImage) F(xrWaitSwapchainImage, WaitSwapchainImage)
    F(xrReleaseSwapchainImage, ReleaseSwapchainImage) F(xrEnumerateReferenceSpaces, EnumerateReferenceSpaces) F(xrCreateReferenceSpace, CreateReferenceSpace)
    F(xrDestroySpace, DestroySpace) F(xrLocateSpace, LocateSpace) F(xrLocateViews, LocateViews) F(xrWaitFrame, WaitFrame) F(xrBeginFrame, BeginFrame)
    F(xrEndFrame, EndFrame) F(xrStringToPath, StringToPath)
#undef F
    *fn = (PFN_xrVoidFunction)Unsupported;
    return XR_SUCCESS;
}

}  // namespace

extern "C" __declspec(dllexport) XrResult XRAPI_CALL xrNegotiateLoaderRuntimeInterface(const XrNegotiateLoaderInfo* info, XrNegotiateRuntimeRequest* req) {
    if (!info || !req || info->structType != XR_LOADER_INTERFACE_STRUCT_LOADER_INFO || req->structType != XR_LOADER_INTERFACE_STRUCT_RUNTIME_REQUEST)
        return XR_ERROR_INITIALIZATION_FAILED;
    req->runtimeInterfaceVersion = XR_CURRENT_LOADER_RUNTIME_VERSION;
    req->runtimeApiVersion = XR_CURRENT_API_VERSION;
    req->getInstanceProcAddr = GetInstanceProcAddr;
    return XR_SUCCESS;
}
