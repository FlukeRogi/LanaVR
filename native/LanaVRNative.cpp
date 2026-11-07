// LanaVR native bridge: owns the OpenXR instance/session and submits two per-eye quad layers.
//
// Threading: LVR_Create and LVR_BeginFrame (xrWaitFrame, xrLocateViews, event polling) run on Unity's
// main thread. Session creation, swapchain work, xrBeginFrame and xrEndFrame run on Unity's render
// thread through GL.IssuePluginEvent, because they touch the D3D11 immediate context.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include "Skybox.h"
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <string>
#include <vector>

#define API extern "C" __declspec(dllexport)

namespace {

struct Eye { float px, py, pz, qx, qy, qz, qw; };
struct Frame {
    int running;       // session is running, this frame must be submitted
    int shouldRender;  // the runtime wants images this frame
    int viewsValid;    // eye poses below are tracked
    int pad;
    Eye eye[2];
    float tangents[2][4];  // per eye: tangent of the left, right, up and down edges of its view
    double periodSeconds;
};
struct Screen { Eye pose; float width, height; };
struct Pending { XrTime displayTime; XrDuration period; bool shouldRender; bool viewsValid; Screen screen; XrView views[2]; float skyBrightness; float skyYaw; float waterHeight; float ripple; double time; float skyTint[3]; };

std::mutex g_logLock;
std::vector<std::string> g_logs;
void Log(const char* fmt, ...) {
    char b[1024];
    va_list a; va_start(a, fmt); vsnprintf(b, sizeof b, fmt, a); va_end(a);
    std::lock_guard<std::mutex> l(g_logLock);
    if (g_logs.size() < 512) g_logs.emplace_back(b);
}

XrInstance g_instance = XR_NULL_HANDLE;
XrSystemId g_system = XR_NULL_SYSTEM_ID;
XrSession g_session = XR_NULL_HANDLE;
XrSpace g_local = XR_NULL_HANDLE, g_view = XR_NULL_HANDLE;
std::atomic<int> g_state{0};
std::atomic<bool> g_running{false};
std::atomic<bool> g_exitRequested{false};
XrViewConfigurationView g_viewCfg[2]{{XR_TYPE_VIEW_CONFIGURATION_VIEW}, {XR_TYPE_VIEW_CONFIGURATION_VIEW}};
char g_runtimeName[XR_MAX_RUNTIME_NAME_SIZE] = "";

ID3D11Device* g_device = nullptr;
ID3D11DeviceContext* g_ctx = nullptr;
ID3D11Texture2D* g_src[2] = {nullptr, nullptr};
int g_srcW = 0, g_srcH = 0;

struct Chain { XrSwapchain handle = XR_NULL_HANDLE; std::vector<XrSwapchainImageD3D11KHR> images; int w = 0, h = 0; };
Chain g_chain[2];
int64_t g_format = 0;

// The skybox has its own swapchains at the headset's full per-eye size.
Chain g_skyChain[2];
std::vector<ID3D11RenderTargetView*> g_skyTargets[2];
float g_skyBrightness = 1.0f, g_skyYaw = 0.0f;
float g_skyTint[3] = {1.0f, 1.0f, 1.0f};

// Water. Its base level is the room's floor when the runtime knows it, otherwise a typical height below
// the player's head. The offset comes from the menu, and the water never rises above the head.
const float kAssumedHeadAboveFloor = 1.5f, kMinimumBelowHead = 0.2f, kMinFloorBelowHead = 0.6f, kMaxFloorBelowHead = 3.0f;
XrSpace g_stage = XR_NULL_HANDLE;
bool g_floorKnown = false;
float g_floorY = 0.0f, g_waterHeadY = 0.0f, g_waterOffset = 0.0f, g_ripple = 1.0f, g_waterY = -1.5f, g_floorBase = -1.5f;
std::atomic<int> g_waterDebug{0};

// The 3D scenery is drawn by Unity for each eye, then blended into the sky layer here.
ID3D11Texture2D* g_scenery[2] = {nullptr, nullptr};
ID3D11Texture2D* g_sceneryMirror[2] = {nullptr, nullptr};
std::atomic<bool> g_sceneryOn{false};

std::mutex g_screenLock;
Screen g_screen{{0, 0, -2.5f, 0, 0, 0, 1}, 3.0f, 1.6875f};

std::mutex g_queueLock;
std::vector<Pending> g_queue;

std::atomic<uint64_t> g_framesSubmitted{0};

bool Check(XrResult r, const char* what) {
    if (XR_SUCCEEDED(r)) return true;
    char s[XR_MAX_RESULT_STRING_SIZE] = "";
    if (g_instance) xrResultToString(g_instance, r, s);
    Log("%s failed: %d %s", what, (int)r, s);
    return false;
}

void DestroyChains() {
    for (auto& c : g_chain) {
        if (c.handle) xrDestroySwapchain(c.handle);
        c = Chain{};
    }
}

bool CreateChains() {
    DestroyChains();
    uint32_t n = 0;
    xrEnumerateSwapchainFormats(g_session, 0, &n, nullptr);
    std::vector<int64_t> formats(n);
    if (!Check(xrEnumerateSwapchainFormats(g_session, n, &n, formats.data()), "xrEnumerateSwapchainFormats")) return false;
    g_format = 0;
    // The game draws in gamma space, so values are already sRGB encoded. An sRGB swapchain makes the
    // compositor decode them correctly. A straight copy keeps the bytes unchanged.
    for (int64_t want : {(int64_t)DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, (int64_t)DXGI_FORMAT_R8G8B8A8_UNORM})
        for (auto f : formats)
            if (f == want && !g_format) g_format = f;
    if (!g_format) { Log("runtime offers no RGBA8 swapchain format"); return false; }
    for (int i = 0; i < 2; i++) {
        XrSwapchainCreateInfo ci{XR_TYPE_SWAPCHAIN_CREATE_INFO};
        ci.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT | XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
        ci.format = g_format;
        ci.sampleCount = 1;
        ci.width = g_srcW;
        ci.height = g_srcH;
        ci.faceCount = 1;
        ci.arraySize = 1;
        ci.mipCount = 1;
        if (!Check(xrCreateSwapchain(g_session, &ci, &g_chain[i].handle), "xrCreateSwapchain")) return false;
        uint32_t count = 0;
        xrEnumerateSwapchainImages(g_chain[i].handle, 0, &count, nullptr);
        g_chain[i].images.assign(count, {XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
        if (!Check(xrEnumerateSwapchainImages(g_chain[i].handle, count, &count, (XrSwapchainImageBaseHeader*)g_chain[i].images.data()), "xrEnumerateSwapchainImages")) return false;
        g_chain[i].w = g_srcW;
        g_chain[i].h = g_srcH;
    }
    Log("swapchains %dx%d format %d images %zu", g_srcW, g_srcH, (int)g_format, g_chain[0].images.size());
    return true;
}

void DestroySkyChains() {
    for (int i = 0; i < 2; i++) {
        for (auto* t : g_skyTargets[i]) t->Release();
        g_skyTargets[i].clear();
        if (g_skyChain[i].handle) xrDestroySwapchain(g_skyChain[i].handle);
        g_skyChain[i] = Chain{};
    }
}

bool CreateSkyChains() {
    if (g_skyChain[0].handle) return true;
    if (!g_format) return false;
    for (int i = 0; i < 2; i++) {
        XrSwapchainCreateInfo ci{XR_TYPE_SWAPCHAIN_CREATE_INFO};
        ci.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
        ci.format = g_format;
        ci.sampleCount = 1;
        ci.width = g_viewCfg[i].recommendedImageRectWidth;
        ci.height = g_viewCfg[i].recommendedImageRectHeight;
        ci.faceCount = 1;
        ci.arraySize = 1;
        ci.mipCount = 1;
        if (!Check(xrCreateSwapchain(g_session, &ci, &g_skyChain[i].handle), "xrCreateSwapchain (sky)")) return false;
        uint32_t count = 0;
        xrEnumerateSwapchainImages(g_skyChain[i].handle, 0, &count, nullptr);
        g_skyChain[i].images.assign(count, {XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
        if (!Check(xrEnumerateSwapchainImages(g_skyChain[i].handle, count, &count, (XrSwapchainImageBaseHeader*)g_skyChain[i].images.data()), "xrEnumerateSwapchainImages (sky)")) return false;
        g_skyChain[i].w = (int)ci.width;
        g_skyChain[i].h = (int)ci.height;
        for (auto& image : g_skyChain[i].images) {
            D3D11_RENDER_TARGET_VIEW_DESC rv{};
            rv.Format = (DXGI_FORMAT)g_format;
            rv.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
            ID3D11RenderTargetView* target = nullptr;
            HRESULT hr = g_device->CreateRenderTargetView(image.texture, &rv, &target);
            if (FAILED(hr)) { Log("sky render target failed: 0x%08X", hr); return false; }
            g_skyTargets[i].push_back(target);
        }
    }
    Log("sky swapchains %dx%d", g_skyChain[0].w, g_skyChain[0].h);
    return true;
}

// Draws both eyes of the backdrop. Returns false if the layer should be left out of this frame.
bool DrawSky(const Pending& p, XrCompositionLayerProjectionView views[2]) {
    if (p.skyBrightness <= 0.0f || !p.viewsValid || !g_device) return false;
    if (!sky::Prepare(g_device, g_ctx) || !CreateSkyChains()) return false;
    bool ok = true;
    sky::Water water{};
    water.height = p.waterHeight;
    water.time = (float)p.time;
    water.ripple = p.ripple;
    for (int c = 0; c < 3; c++) water.tint[c] = p.skyTint[c];
    water.debug = g_waterDebug.load();
    water.screenPos[0] = p.screen.pose.px; water.screenPos[1] = p.screen.pose.py; water.screenPos[2] = p.screen.pose.pz;
    water.screenRot[0] = p.screen.pose.qx; water.screenRot[1] = p.screen.pose.qy; water.screenRot[2] = p.screen.pose.qz; water.screenRot[3] = p.screen.pose.qw;
    water.screenSize[0] = p.screen.width; water.screenSize[1] = p.screen.height;
    sky::BeginPass(g_ctx);
    for (int i = 0; i < 2 && ok; i++) {
        uint32_t idx = 0;
        XrSwapchainImageAcquireInfo ai{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
        ok = Check(xrAcquireSwapchainImage(g_skyChain[i].handle, &ai, &idx), "acquire (sky)");
        if (!ok) break;
        XrSwapchainImageWaitInfo wi{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
        wi.timeout = 100000000;
        ok = Check(xrWaitSwapchainImage(g_skyChain[i].handle, &wi), "wait (sky)");
        const XrFovf& fov = p.views[i].fov;
        const XrQuaternionf& q = p.views[i].pose.orientation;
        const float tangents[4] = {std::tan(fov.angleLeft), std::tan(fov.angleRight), std::tan(fov.angleUp), std::tan(fov.angleDown)};
        const float rotation[4] = {q.x, q.y, q.z, q.w};
        water.eye[0] = p.views[i].pose.position.x; water.eye[1] = p.views[i].pose.position.y; water.eye[2] = p.views[i].pose.position.z;
        water.gameImage = g_src[i];
        water.scenery = g_sceneryOn.load() ? g_scenery[i] : nullptr;
        water.sceneryReflection = g_sceneryOn.load() ? g_sceneryMirror[i] : nullptr;
        if (ok) sky::DrawEye(g_ctx, g_skyTargets[i][idx], g_skyChain[i].w, g_skyChain[i].h, tangents, rotation, p.skyYaw, p.skyBrightness, water);
        XrSwapchainImageReleaseInfo ri{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
        Check(xrReleaseSwapchainImage(g_skyChain[i].handle, &ri), "release (sky)");
        views[i] = {XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW};
        views[i].pose = p.views[i].pose;
        views[i].fov = fov;
        views[i].subImage.swapchain = g_skyChain[i].handle;
        views[i].subImage.imageRect = {{0, 0}, {g_skyChain[i].w, g_skyChain[i].h}};
    }
    sky::EndPass(g_ctx);
    return ok;
}

void RT_CreateSession() {
    if (g_session || !g_instance || !g_src[0]) return;
    g_src[0]->GetDevice(&g_device);
    g_device->GetImmediateContext(&g_ctx);

    D3D11_TEXTURE2D_DESC td{};
    g_src[0]->GetDesc(&td);
    Log("eye texture %ux%u dxgi=%d samples=%u", td.Width, td.Height, (int)td.Format, td.SampleDesc.Count);

    PFN_xrGetD3D11GraphicsRequirementsKHR getReq = nullptr;
    xrGetInstanceProcAddr(g_instance, "xrGetD3D11GraphicsRequirementsKHR", (PFN_xrVoidFunction*)&getReq);
    XrGraphicsRequirementsD3D11KHR req{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
    if (!getReq || !Check(getReq(g_instance, g_system, &req), "xrGetD3D11GraphicsRequirementsKHR")) return;

    IDXGIDevice* dxgi = nullptr;
    if (SUCCEEDED(g_device->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgi))) {
        IDXGIAdapter* ad = nullptr;
        if (SUCCEEDED(dxgi->GetAdapter(&ad))) {
            DXGI_ADAPTER_DESC d{};
            ad->GetDesc(&d);
            if (memcmp(&d.AdapterLuid, &req.adapterLuid, sizeof(LUID)) != 0) {
                char game[128] = "", wanted[128] = "unknown";
                WideCharToMultiByte(CP_UTF8, 0, d.Description, -1, game, sizeof game, nullptr, nullptr);
                IDXGIFactory1* factory = nullptr;
                if (SUCCEEDED(ad->GetParent(__uuidof(IDXGIFactory1), (void**)&factory))) {
                    IDXGIAdapter1* other = nullptr;
                    for (UINT i = 0; factory->EnumAdapters1(i, &other) != DXGI_ERROR_NOT_FOUND; i++) {
                        DXGI_ADAPTER_DESC1 od{};
                        other->GetDesc1(&od);
                        if (memcmp(&od.AdapterLuid, &req.adapterLuid, sizeof(LUID)) == 0)
                            WideCharToMultiByte(CP_UTF8, 0, od.Description, -1, wanted, sizeof wanted, nullptr, nullptr);
                        other->Release();
                    }
                    factory->Release();
                }
                Log("WARNING: the game runs on '%s' but the headset runtime asked for '%s'", game, wanted);
            }
            ad->Release();
        }
        dxgi->Release();
    }

    XrGraphicsBindingD3D11KHR bind{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR};
    bind.device = g_device;
    XrSessionCreateInfo sci{XR_TYPE_SESSION_CREATE_INFO};
    sci.next = &bind;
    sci.systemId = g_system;
    if (!Check(xrCreateSession(g_instance, &sci, &g_session), "xrCreateSession")) return;

    XrReferenceSpaceCreateInfo rs{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    rs.poseInReferenceSpace.orientation.w = 1;
    rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    Check(xrCreateReferenceSpace(g_session, &rs, &g_local), "local space");
    rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW;
    Check(xrCreateReferenceSpace(g_session, &rs, &g_view), "view space");
    uint32_t spaceCount = 0;
    xrEnumerateReferenceSpaces(g_session, 0, &spaceCount, nullptr);
    std::vector<XrReferenceSpaceType> spaces(spaceCount);
    xrEnumerateReferenceSpaces(g_session, spaceCount, &spaceCount, spaces.data());
    for (auto type : spaces) {
        if (type != XR_REFERENCE_SPACE_TYPE_STAGE) continue;
        rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_STAGE;
        if (XR_FAILED(xrCreateReferenceSpace(g_session, &rs, &g_stage))) g_stage = XR_NULL_HANDLE;
    }
    Log("floor tracking %s", g_stage ? "available" : "not available, assuming a typical height");
    CreateChains();
    Log("session created");
}

void RT_Submit() {
    Pending p;
    {
        std::lock_guard<std::mutex> l(g_queueLock);
        if (g_queue.empty()) return;
        p = g_queue.front();
        g_queue.erase(g_queue.begin());
    }
    XrFrameBeginInfo bi{XR_TYPE_FRAME_BEGIN_INFO};
    XrResult br = xrBeginFrame(g_session, &bi);
    if (XR_FAILED(br)) { Check(br, "xrBeginFrame"); return; }

    XrCompositionLayerQuad quads[2];
    XrCompositionLayerProjection skyLayer{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
    XrCompositionLayerProjectionView skyViews[2];
    const XrCompositionLayerBaseHeader* layers[3];
    uint32_t layerCount = 0;

    // The first layer is the furthest back, so the sky goes behind the screen.
    if (p.shouldRender && DrawSky(p, skyViews)) {
        skyLayer.space = g_local;
        skyLayer.viewCount = 2;
        skyLayer.views = skyViews;
        layers[layerCount++] = (const XrCompositionLayerBaseHeader*)&skyLayer;
    }

    bool sized = g_chain[0].handle && g_chain[0].w == g_srcW && g_chain[0].h == g_srcH;
    if (p.shouldRender && sized && g_src[0] && g_src[1]) {
        bool ok = true;
        for (int i = 0; i < 2 && ok; i++) {
            uint32_t idx = 0;
            XrSwapchainImageAcquireInfo ai{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
            ok = Check(xrAcquireSwapchainImage(g_chain[i].handle, &ai, &idx), "acquire");
            if (!ok) break;
            XrSwapchainImageWaitInfo wi{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
            wi.timeout = 100000000;
            ok = Check(xrWaitSwapchainImage(g_chain[i].handle, &wi), "wait");
            if (ok) g_ctx->CopySubresourceRegion(g_chain[i].images[idx].texture, 0, 0, 0, 0, g_src[i], 0, nullptr);
            XrSwapchainImageReleaseInfo ri{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
            Check(xrReleaseSwapchainImage(g_chain[i].handle, &ri), "release");

            XrCompositionLayerQuad& q = quads[i];
            q = {XR_TYPE_COMPOSITION_LAYER_QUAD};
            q.layerFlags = 0;
            q.space = g_local;
            q.eyeVisibility = i == 0 ? XR_EYE_VISIBILITY_LEFT : XR_EYE_VISIBILITY_RIGHT;
            q.subImage.swapchain = g_chain[i].handle;
            q.subImage.imageRect = {{0, 0}, {g_chain[i].w, g_chain[i].h}};
            q.subImage.imageArrayIndex = 0;
            q.pose.position = {p.screen.pose.px, p.screen.pose.py, p.screen.pose.pz};
            q.pose.orientation = {p.screen.pose.qx, p.screen.pose.qy, p.screen.pose.qz, p.screen.pose.qw};
            q.size = {p.screen.width, p.screen.height};
        }
        if (ok) {
            layers[layerCount++] = (const XrCompositionLayerBaseHeader*)&quads[0];
            layers[layerCount++] = (const XrCompositionLayerBaseHeader*)&quads[1];
        }
    }

    XrFrameEndInfo ei{XR_TYPE_FRAME_END_INFO};
    ei.displayTime = p.displayTime;
    ei.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
    ei.layerCount = layerCount;
    ei.layers = layers;
    if (Check(xrEndFrame(g_session, &ei), "xrEndFrame")) g_framesSubmitted++;
}

void RT_Resize() {
    if (g_session) CreateChains();
}

void RT_Shutdown() {
    DestroyChains();
    DestroySkyChains();
    sky::ReleaseGpu();
    if (g_local) { xrDestroySpace(g_local); g_local = XR_NULL_HANDLE; }
    if (g_view) { xrDestroySpace(g_view); g_view = XR_NULL_HANDLE; }
    if (g_stage) { xrDestroySpace(g_stage); g_stage = XR_NULL_HANDLE; }
    g_floorKnown = false;
    if (g_session) { xrDestroySession(g_session); g_session = XR_NULL_HANDLE; }
    if (g_ctx) { g_ctx->Release(); g_ctx = nullptr; }
    if (g_device) { g_device->Release(); g_device = nullptr; }
    if (g_instance) { xrDestroyInstance(g_instance); g_instance = XR_NULL_HANDLE; }
    {
        std::lock_guard<std::mutex> l(g_queueLock);
        g_queue.clear();
    }
    g_system = XR_NULL_SYSTEM_ID;
    g_running = false;
    g_exitRequested = false;
    g_state = 0;
    Log("OpenXR shut down");
}

void __stdcall OnRenderEvent(int id) {
    __try {
        switch (id) {
            case 1: RT_CreateSession(); break;
            case 2: RT_Submit(); break;
            case 3: RT_Resize(); break;
            case 4: RT_Shutdown(); break;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Log("render event %d raised exception 0x%08X", id, GetExceptionCode());
    }
}

void PollEvents() {
    for (;;) {
        XrEventDataBuffer e{XR_TYPE_EVENT_DATA_BUFFER};
        if (xrPollEvent(g_instance, &e) != XR_SUCCESS) return;
        if (e.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
            auto* s = (XrEventDataSessionStateChanged*)&e;
            g_state = (int)s->state;
            Log("session state %d", (int)s->state);
            if (s->state == XR_SESSION_STATE_READY) {
                XrSessionBeginInfo b{XR_TYPE_SESSION_BEGIN_INFO};
                b.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                if (Check(xrBeginSession(g_session, &b), "xrBeginSession")) g_running = true;
            } else if (s->state == XR_SESSION_STATE_STOPPING) {
                g_running = false;
                Check(xrEndSession(g_session), "xrEndSession");
            } else if (s->state == XR_SESSION_STATE_EXITING || s->state == XR_SESSION_STATE_LOSS_PENDING) {
                g_running = false;
                g_exitRequested = true;
            }
        } else if (e.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) {
            g_running = false;
            g_exitRequested = true;
            Log("instance loss pending");
        }
    }
}

}  // namespace

void LvrLog(const char* fmt, ...) {
    char b[1024];
    va_list a;
    va_start(a, fmt);
    vsnprintf(b, sizeof b, fmt, a);
    va_end(a);
    Log("%s", b);
}

// Returns 0 on success, 1 when no headset is available yet (worth retrying), or 2 on a hard failure.
API int LVR_Create(const char* runtimeJson) {
    if (g_instance) return 0;
    if (runtimeJson && runtimeJson[0]) {
        SetEnvironmentVariableA("XR_RUNTIME_JSON", runtimeJson);
        Log("runtime override: %s", runtimeJson);
    }
    const char* exts[] = {XR_KHR_D3D11_ENABLE_EXTENSION_NAME};
    XrInstanceCreateInfo ci{XR_TYPE_INSTANCE_CREATE_INFO};
    strcpy_s(ci.applicationInfo.applicationName, "Planet of Lana VR");
    ci.applicationInfo.applicationVersion = 1;
    strcpy_s(ci.applicationInfo.engineName, "Unity 2020.3");
    ci.applicationInfo.engineVersion = 2020;
    ci.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
    ci.enabledExtensionCount = 1;
    ci.enabledExtensionNames = exts;
    XrResult r = xrCreateInstance(&ci, &g_instance);
    if (XR_FAILED(r)) { Log("xrCreateInstance failed: %d", (int)r); g_instance = XR_NULL_HANDLE; return 1; }

    XrInstanceProperties ip{XR_TYPE_INSTANCE_PROPERTIES};
    if (XR_SUCCEEDED(xrGetInstanceProperties(g_instance, &ip))) {
        strcpy_s(g_runtimeName, ip.runtimeName);
        Log("runtime: %s %d.%d.%d", ip.runtimeName, XR_VERSION_MAJOR(ip.runtimeVersion), XR_VERSION_MINOR(ip.runtimeVersion), XR_VERSION_PATCH(ip.runtimeVersion));
    }

    XrSystemGetInfo si{XR_TYPE_SYSTEM_GET_INFO};
    si.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    r = xrGetSystem(g_instance, &si, &g_system);
    if (XR_FAILED(r)) {
        Log("xrGetSystem failed: %d (is the headset connected?)", (int)r);
        xrDestroyInstance(g_instance);
        g_instance = XR_NULL_HANDLE;
        return r == XR_ERROR_FORM_FACTOR_UNAVAILABLE ? 1 : 2;
    }
    uint32_t n = 2;
    if (!Check(xrEnumerateViewConfigurationViews(g_instance, g_system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 2, &n, g_viewCfg), "view config")) return 2;
    Log("recommended eye size %ux%u", g_viewCfg[0].recommendedImageRectWidth, g_viewCfg[0].recommendedImageRectHeight);
    return 0;
}

API void LVR_LoadSkybox(const wchar_t* path, int maxFaceSize, float fadeSeconds) { sky::Load(path, maxFaceSize, fadeSeconds); }

API void LVR_SkyboxStatus(char* buffer, int size) { sky::Status(buffer, size); }

API void LVR_SetSkybox(float brightness, float yaw) {
    std::lock_guard<std::mutex> l(g_screenLock);
    g_skyBrightness = brightness;
    g_skyYaw = yaw;
}

// The tint arrives as gamma-space ratios, which is how the game's grading was measured. The shader multiplies
// in linear light, so converting here gives the same on-screen ratio.
API void LVR_SetSkyTint(float r, float g, float b) {
    std::lock_guard<std::mutex> l(g_screenLock);
    g_skyTint[0] = std::pow(std::max(r, 0.0f), 2.2f);
    g_skyTint[1] = std::pow(std::max(g, 0.0f), 2.2f);
    g_skyTint[2] = std::pow(std::max(b, 0.0f), 2.2f);
}

API void LVR_SetWater(float headY, float offset, float ripple) {
    std::lock_guard<std::mutex> l(g_screenLock);
    g_waterHeadY = headY;
    g_waterOffset = offset;
    g_ripple = ripple;
}

API void LVR_GetWater(float* y, int* floorKnown) {
    std::lock_guard<std::mutex> l(g_screenLock);
    *y = g_waterY;
    *floorKnown = g_floorKnown ? 1 : 0;
}

API void LVR_SetWaterDebug(int mode) { g_waterDebug = mode; }

// The floor the scenery stands on, in tracking space: the same base the water's level is measured from.
API float LVR_GetFloor() {
    std::lock_guard<std::mutex> l(g_screenLock);
    return g_floorBase;
}

API void LVR_SetScenery(void* left, void* right, void* mirrorLeft, void* mirrorRight) {
    g_scenery[0] = (ID3D11Texture2D*)left;
    g_scenery[1] = (ID3D11Texture2D*)right;
    g_sceneryMirror[0] = (ID3D11Texture2D*)mirrorLeft;
    g_sceneryMirror[1] = (ID3D11Texture2D*)mirrorRight;
}

API void LVR_SetSceneryOn(int on) { g_sceneryOn = on != 0; }

API void LVR_GetRecommended(int* w, int* h) {
    *w = (int)g_viewCfg[0].recommendedImageRectWidth;
    *h = (int)g_viewCfg[0].recommendedImageRectHeight;
}

// Unity creates its render textures with the game's D3D11 device. Called on the main thread while no
// submit is in flight for the previous textures, then followed by render event 1 or 3.
API void LVR_SetEyeTextures(void* left, void* right, int w, int h) {
    g_src[0] = (ID3D11Texture2D*)left;
    g_src[1] = (ID3D11Texture2D*)right;
    g_srcW = w;
    g_srcH = h;
}

API void LVR_SetScreen(float px, float py, float pz, float qx, float qy, float qz, float qw, float width, float height) {
    std::lock_guard<std::mutex> l(g_screenLock);
    g_screen = {{px, py, pz, qx, qy, qz, qw}, width, height};
}

// Paces the game to the headset and returns the eye poses predicted for this frame's display time.
API void LVR_BeginFrame(Frame* out) {
    *out = {};
    if (!g_instance || !g_session) return;
    PollEvents();
    if (!g_running) return;

    XrFrameWaitInfo wi{XR_TYPE_FRAME_WAIT_INFO};
    XrFrameState fs{XR_TYPE_FRAME_STATE};
    XrResult r = xrWaitFrame(g_session, &wi, &fs);
    if (XR_FAILED(r)) { Check(r, "xrWaitFrame"); return; }

    out->running = 1;
    out->shouldRender = fs.shouldRender ? 1 : 0;
    out->periodSeconds = fs.predictedDisplayPeriod / 1e9;

    XrViewLocateInfo li{XR_TYPE_VIEW_LOCATE_INFO};
    li.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    li.displayTime = fs.predictedDisplayTime;
    li.space = g_local;
    XrViewState vs{XR_TYPE_VIEW_STATE};
    XrView views[2]{{XR_TYPE_VIEW}, {XR_TYPE_VIEW}};
    uint32_t n = 0;
    if (XR_SUCCEEDED(xrLocateViews(g_session, &li, &vs, 2, &n, views)) && (vs.viewStateFlags & XR_VIEW_STATE_POSITION_VALID_BIT)) {
        out->viewsValid = 1;
        for (int i = 0; i < 2; i++) {
            auto& p = views[i].pose;
            out->eye[i] = {p.position.x, p.position.y, p.position.z, p.orientation.x, p.orientation.y, p.orientation.z, p.orientation.w};
            out->tangents[i][0] = std::tan(views[i].fov.angleLeft);
            out->tangents[i][1] = std::tan(views[i].fov.angleRight);
            out->tangents[i][2] = std::tan(views[i].fov.angleUp);
            out->tangents[i][3] = std::tan(views[i].fov.angleDown);
        }
    }
    Pending pending{fs.predictedDisplayTime, fs.predictedDisplayPeriod, fs.shouldRender == XR_TRUE, out->viewsValid != 0};
    pending.views[0] = views[0];
    pending.views[1] = views[1];
    XrSpaceLocation floorLocation{XR_TYPE_SPACE_LOCATION};
    bool floorLocated = g_stage && XR_SUCCEEDED(xrLocateSpace(g_stage, g_local, fs.predictedDisplayTime, &floorLocation)) && (floorLocation.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT);
    {
        std::lock_guard<std::mutex> l(g_screenLock);
        // A room that hasn't been set up can report a floor at head height, so one that isn't a believable
        // distance below a seated or standing player is treated as unknown.
        float below = g_waterHeadY - floorLocation.pose.position.y;
        g_floorKnown = floorLocated && below >= kMinFloorBelowHead && below <= kMaxFloorBelowHead;
        if (g_floorKnown) g_floorY = floorLocation.pose.position.y;
        pending.screen = g_screen;
        pending.skyBrightness = g_skyBrightness;
        pending.skyYaw = g_skyYaw;
        for (int c = 0; c < 3; c++) pending.skyTint[c] = g_skyTint[c];
        float base = g_floorKnown ? g_floorY : g_waterHeadY - kAssumedHeadAboveFloor;
        g_floorBase = base;
        g_waterY =std::min(base + g_waterOffset, g_waterHeadY - kMinimumBelowHead);
        pending.waterHeight = g_waterY;
        pending.ripple = g_ripple;
    }
    {
        // Wrapped every few minutes, so the shader's single-precision time never loses accuracy.
        LARGE_INTEGER now, freq;
        QueryPerformanceCounter(&now);
        QueryPerformanceFrequency(&freq);
        pending.time = fmod((double)now.QuadPart / (double)freq.QuadPart, 2048.0);
    }
    std::lock_guard<std::mutex> l(g_queueLock);
    g_queue.push_back(pending);
}

API void* LVR_GetRenderEvent() { return (void*)OnRenderEvent; }
API int LVR_SessionState() { return g_state.load(); }
API int LVR_IsRunning() { return g_running.load() ? 1 : 0; }
API int LVR_HasSession() { return g_session ? 1 : 0; }
API int LVR_ExitRequested() { return g_exitRequested.load() ? 1 : 0; }
API unsigned long long LVR_FramesSubmitted() { return g_framesSubmitted.load(); }
API const char* LVR_RuntimeName() { return g_runtimeName; }

API int LVR_PopLog(char* buffer, int size) {
    std::lock_guard<std::mutex> l(g_logLock);
    if (g_logs.empty()) return 0;
    strncpy_s(buffer, size, g_logs.front().c_str(), _TRUNCATE);
    g_logs.erase(g_logs.begin());
    return 1;
}
