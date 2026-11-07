#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "Skybox.h"
#include <d3dcompiler.h>
#include <wincodec.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <vector>

namespace sky {
namespace {

template <class T>
void Drop(T*& p) {
    if (p) { p->Release(); p = nullptr; }
}

// A horizontal cross is 4 faces wide and 3 faces tall:
//         [top]
//  [left] [front] [right] [back]
//         [bottom]
// Direct3D cube slices are +X, -X, +Y, -Y, +Z, -Z. Each entry is the {column, row} of that face. Faces
// from this layout can be used as they are, because Direct3D's face orientations unfold into this cross.
const int kFaceCell[6][2] = {{2, 1}, {0, 1}, {1, 0}, {1, 2}, {1, 1}, {3, 1}};

std::mutex g_lock;
std::string g_status = "none";
int g_generation = 0;
bool g_clearRequested = false;
int g_pendingFace = 0;
std::vector<uint8_t> g_pendingFaces[6];

ID3D11Texture2D* g_texture = nullptr;
ID3D11ShaderResourceView* g_view = nullptr;
ID3D11Texture2D* g_nextTexture = nullptr;
ID3D11ShaderResourceView* g_nextView = nullptr;
float g_pendingFade = 0, g_fadeSeconds = 0, g_mix = 0;
LARGE_INTEGER g_fadeStart{};
ID3D11VertexShader* g_vs = nullptr;
ID3D11PixelShader* g_ps = nullptr;
ID3D11Buffer* g_constants = nullptr;
ID3D11SamplerState* g_sampler = nullptr;
ID3D11RasterizerState* g_raster = nullptr;
ID3D11DepthStencilState* g_depth = nullptr;
ID3D11BlendState* g_blend = nullptr;
ID3DDeviceContextState* g_state = nullptr;
ID3DDeviceContextState* g_savedState = nullptr;
bool g_gpuFailed = false;
// Views of the two eye images. Eye textures are replaced together, so the older entry is the one to reuse.
ID3D11Texture2D* g_gameTextures[2] = {nullptr, nullptr};
ID3D11ShaderResourceView* g_gameViews[2] = {nullptr, nullptr};
int g_gameNext = 0;
// Views of the scenery images, which last until the session ends.
std::vector<std::pair<ID3D11Texture2D*, ID3D11ShaderResourceView*>> g_sceneryViews;

const char kShader[] = R"(
cbuffer Params : register(b0) {
    float4 tangents;      // left, right, up, down
    float4 viewRotation;  // quaternion
    float4 skyRotation;   // quaternion from world to sky
    float4 tint;          // x brightness, y time, z ripple strength, w debug mode
    float4 eye;           // xyz this eye's position, w water level
    float4 screenPos;     // xyz centre, w is 1 when a game image is bound
    float4 screenRotInv;  // quaternion from world to screen space
    float4 screenSize;    // xy width and height, z how far the crossfade to the next sky has got
    float4 skyTint;       // rgb linear multiplier, following the game's colour grading
    float4 sceneryInfo;   // xy this layer's size in pixels, z is 1 when scenery is bound
};
TextureCube sky : register(t0);
Texture2D gameImage : register(t1);
TextureCube nextSky : register(t2);
Texture2D sceneryImage : register(t3);
Texture2D sceneryReflection : register(t4);
SamplerState skySampler : register(s0);

float3 Rotate(float4 q, float3 v) {
    float3 u = cross(q.xyz, v) + q.w * v;
    return v + 2.0 * cross(q.xyz, u);
}

// OpenXR looks down -Z with +X to the right. A Direct3D cube looks down +Z with +X to the right.
float3 SkyLookup(float3 world) {
    float3 d = Rotate(skyRotation, world);
    return float3(d.x, d.y, -d.z);
}

float3 SampleSky(float3 cube) {
    return screenSize.z > 0.0 ? lerp(sky.Sample(skySampler, cube).rgb, nextSky.Sample(skySampler, cube).rgb, screenSize.z) : sky.Sample(skySampler, cube).rgb;
}

float3 SampleSkyLevel(float3 cube, float lod) {
    return screenSize.z > 0.0 ? lerp(sky.SampleLevel(skySampler, cube, lod).rgb, nextSky.SampleLevel(skySampler, cube, lod).rgb, screenSize.z) : sky.SampleLevel(skySampler, cube, lod).rgb;
}

float Hash(float2 p) {
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

// Value noise with its analytic slope: x and y of the result are d/dx and d/dy.
float2 NoiseSlope(float2 p) {
    float2 i = floor(p), f = p - i;
    float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float2 du = 30.0 * f * f * (f * (f - 2.0) + 1.0);
    float a = Hash(i), b = Hash(i + float2(1, 0)), c = Hash(i + float2(0, 1)), d = Hash(i + float2(1, 1));
    float k1 = b - a, k2 = c - a, k4 = a - b - c + d;
    return du * float2(k1 + k4 * u.y, k2 + k4 * u.x);
}

// Slope of the water surface at a point. Each layer fades out once its waves are smaller than a pixel,
// which is what stops distant water from sparkling.
float2 RippleSlope(float2 p, float time, float footprint) {
    static const float wavelength[4] = {2.3, 1.05, 0.47, 0.21};
    static const float slope[4] = {0.55, 0.45, 0.32, 0.22};
    static const float2 c[4] = {float2(0.80, 0.60), float2(-0.47, 0.88), float2(0.96, -0.28), float2(-0.71, -0.70)};
    static const float speed[4] = {0.10, 0.17, 0.26, 0.38};
    float2 g = 0;
    for (int i = 0; i < 4; i++) {
        float w = 1.0 - smoothstep(0.2 * wavelength[i], 0.9 * wavelength[i], footprint);
        if (w <= 0.0) continue;
        float2 q = float2(c[i].x * p.x + c[i].y * p.y, -c[i].y * p.x + c[i].x * p.y) / wavelength[i];
        q.x += time * speed[i];
        float2 n = NoiseSlope(q);
        g += w * slope[i] * float2(c[i].x * n.x - c[i].y * n.y, c[i].y * n.x + c[i].x * n.y);
    }
    return g;
}

struct V2P { float4 position : SV_Position; float3 ray : RAY; };

V2P VSMain(uint id : SV_VertexID) {
    float2 ndc = float2(id == 1 ? 3.0 : -1.0, id == 2 ? 3.0 : -1.0);
    V2P o;
    o.position = float4(ndc, 0.0, 1.0);
    float2 t = ndc * 0.5 + 0.5;
    o.ray = float3(lerp(tangents.x, tangents.y, t.x), lerp(tangents.w, tangents.z, t.y), -1.0);
    return o;
}

// The scenery's art already has its lighting painted in, so it is only dimmed and coloured to suit the current
// sky. Its overall brightness and hue come from the sky's average, so a night sky gives dark, cool scenery.
float3 SceneryLight() {
    float3 average = SampleSkyLevel(float3(0, 1, 0), 20.0);
    float level = dot(average, float3(0.30, 0.59, 0.11));
    float3 hue = lerp(float3(1, 1, 1), average / max(level, 0.001), 0.2);
    return hue * max(pow(saturate(level / 0.38), 1.4), 0.08) * tint.x * skyTint.rgb;
}

float3 Background(V2P i) {
    float brightness = tint.x, time = tint.y, ripple = tint.z;
    int debug = (int)tint.w;
    float3 d = normalize(Rotate(viewRotation, i.ray));
    float3 skyColor = SampleSky(SkyLookup(d)) * brightness * skyTint.rgb;
    if (d.y >= -0.0005 || debug == 3 || eye.y <= eye.w) return skyColor;

    float t = (eye.w - eye.y) / d.y;
    float3 p = eye.xyz + d * t;
    if (debug == 1) return saturate(t / 20.0).xxx;

    float footprint = max(length(ddx(p.xz)), length(ddy(p.xz)));
    float2 g = RippleSlope(p.xz, time, footprint) * 0.11 * ripple;
    float3 n = normalize(float3(-g.x, 1.0, -g.y));

    float3 r = reflect(d, n);
    r.y = abs(r.y) + 0.001;
    float fresnel = 0.02 + 0.98 * pow(saturate(1.0 - dot(-d, n)), 5.0);
    float softness = 1.5 + saturate(log2(max(footprint, 0.001)) * 0.25 + 0.5) * 2.0;
    float3 reflection = SampleSkyLevel(SkyLookup(r), softness);
    float3 average = SampleSkyLevel(float3(0, 1, 0), 20.0);
    float3 deep = average * float3(0.10, 0.24, 0.30);
    float3 color = lerp(deep, reflection, fresnel) * brightness * skyTint.rgb;

    // Follows the reflected ray to the game screen. Screen space is +z towards the viewer.
    float3 o = Rotate(screenRotInv, p - screenPos.xyz);
    float3 v = Rotate(screenRotInv, r);
    float3 hitInfo = 0;
    if (o.z > 0.0 && v.z < -0.0001) {
        float s = -o.z / v.z;
        float3 h = o + v * s;
        float2 uv = float2(h.x / screenSize.x + 0.5, 0.5 - h.y / screenSize.y);
        float2 edge = min(uv, 1.0 - uv);
        float feather = 0.015 + 0.05 * saturate(s * 0.25);
        float mask = smoothstep(0.0, feather, edge.x) * smoothstep(0.0, feather, edge.y);
        hitInfo = float3(saturate(uv), mask);
        if (screenPos.w > 0.5 && mask > 0.0) {
            float lod = 2.0 + saturate(s * 0.35) * 2.5;
            float3 game = gameImage.SampleLevel(skySampler, saturate(uv), lod).rgb;
            // Stronger than physics at steep angles, so the screen visibly sits on the water.
            color = lerp(color, game, mask * saturate(fresnel + 0.38));
        }
    }
    if (debug == 2) return hitInfo;

    // The scenery's mirror image, which is already in this view's projection, shifted by the ripples.
    if (sceneryInfo.z > 0.5) {
        float2 uv = i.position.xy / sceneryInfo.xy + g * 0.28;
        float4 mirrored = sceneryReflection.SampleLevel(skySampler, saturate(uv), 0);
        float strength = saturate(fresnel + 0.45) * mirrored.a;
        color = color * (1.0 - strength) + (mirrored.rgb / max(mirrored.a, 0.004)) * SceneryLight() * strength;
    }

    // Blends into the sky at the very edge of the water, which softens the horizon.
    return lerp(skyColor, color, smoothstep(0.0, 0.012, -d.y));
}

float4 PSMain(V2P i) : SV_Target {
    float3 color = Background(i);
    if (sceneryInfo.z > 0.5 && tint.w == 0.0) {
        // Clear pixels hold no colour, so these values are already multiplied by their coverage.
        float4 s = sceneryImage.SampleLevel(skySampler, i.position.xy / sceneryInfo.xy, 0);
        if (s.a > 0.0) color = color * (1.0 - s.a) + s.rgb * SceneryLight();
    }
    return float4(color, 1.0);
}
)";

struct Constants {
    float tangents[4];
    float viewRotation[4];
    float skyRotation[4];
    float tint[4];
    float eye[4];
    float screenPos[4];
    float screenRotInv[4];
    float screenSize[4];
    float skyTint[4];
    float sceneryInfo[4];
};

// Unity's render textures are typeless, and hold gamma encoded values like the eye images.
ID3D11ShaderResourceView* SceneryView(ID3D11DeviceContext* context, ID3D11Texture2D* texture) {
    if (!texture) return nullptr;
    for (auto& entry : g_sceneryViews) if (entry.first == texture) return entry.second;
    ID3D11Device* device = nullptr;
    context->GetDevice(&device);
    D3D11_SHADER_RESOURCE_VIEW_DESC v{};
    v.Format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
    v.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    v.Texture2D.MipLevels = 1;
    ID3D11ShaderResourceView* view = nullptr;
    HRESULT hr = device->CreateShaderResourceView(texture, &v, &view);
    device->Release();
    if (FAILED(hr)) { LvrLog("Scenery unavailable: could not view its image (0x%08X)", hr); return nullptr; }
    g_sceneryViews.emplace_back(texture, view);
    return view;
}

void SetStatus(const std::string& s) {
    std::lock_guard<std::mutex> l(g_lock);
    g_status = s;
}

// Reports only if this load is still the current one.
bool IsCurrent(int generation) {
    std::lock_guard<std::mutex> l(g_lock);
    return generation == g_generation;
}

void Fail(int generation, const char* why) {
    LvrLog("Skybox not loaded: %s", why);
    std::lock_guard<std::mutex> l(g_lock);
    if (generation == g_generation) g_status = std::string("failed: ") + why;
}

// Fills the six cube faces from a 2:1 equirectangular panorama. The middle of the image is forward, and
// the horizon is its middle row. Each face runs on its own thread, as large panoramas are many millions of
// lookups. The slice order and face directions match the cross layout in kFaceCell.
void PanoramaToFaces(const uint8_t* image, int width, int height, int face, std::vector<uint8_t> (&faces)[6]) {
    const double pi = 3.14159265358979323846;
    auto fill = [=, &faces](int slice) {
        faces[slice].resize((size_t)face * face * 4);
        uint8_t* out = faces[slice].data();
        for (int j = 0; j < face; j++) {
            double v = 2.0 * (j + 0.5) / face - 1.0;
            for (int i = 0; i < face; i++, out += 4) {
                double u = 2.0 * (i + 0.5) / face - 1.0, x, y, z;
                switch (slice) {
                    case 0: x = 1; y = -v; z = -u; break;
                    case 1: x = -1; y = -v; z = u; break;
                    case 2: x = u; y = 1; z = v; break;
                    case 3: x = u; y = -1; z = -v; break;
                    case 4: x = u; y = -v; z = 1; break;
                    default: x = -u; y = -v; z = -1; break;
                }
                double px = (std::atan2(x, z) / (2.0 * pi) + 0.5) * width - 0.5;
                double py = (0.5 - std::atan2(y, std::sqrt(x * x + z * z)) / pi) * height - 0.5;
                int x0 = (int)std::floor(px), y0 = (int)std::floor(py);
                double fx = px - x0, fy = py - y0;
                // Wraps around at the sides, and stops at the top and bottom rows.
                int x1 = (x0 + 1 + width) % width;
                x0 = (x0 + width) % width;
                int y1 = std::min(y0 + 1, height - 1);
                y0 = std::max(y0, 0);
                const uint8_t* a = image + ((size_t)y0 * width + x0) * 4;
                const uint8_t* b = image + ((size_t)y0 * width + x1) * 4;
                const uint8_t* c = image + ((size_t)y1 * width + x0) * 4;
                const uint8_t* d = image + ((size_t)y1 * width + x1) * 4;
                for (int k = 0; k < 4; k++) {
                    double top = a[k] + (b[k] - a[k]) * fx, bottom = c[k] + (d[k] - c[k]) * fx;
                    out[k] = (uint8_t)(top + (bottom - top) * fy + 0.5);
                }
            }
        }
    };
    std::thread workers[6];
    for (int slice = 0; slice < 6; slice++) workers[slice] = std::thread(fill, slice);
    for (auto& worker : workers) worker.join();
}

void Decode(std::wstring path, int maxFace, int generation) {
    HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    IWICImagingFactory* factory = nullptr;
    IWICBitmapDecoder* decoder = nullptr;
    IWICBitmapFrameDecode* frame = nullptr;
    IWICBitmapScaler* scaler = nullptr;
    IWICFormatConverter* converter = nullptr;
    do {
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)))) { Fail(generation, "no image decoder available"); break; }
        if (FAILED(factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder))) { Fail(generation, "the file is missing or is not a readable image"); break; }
        if (FAILED(decoder->GetFrame(0, &frame))) { Fail(generation, "the image has no readable frame"); break; }
        UINT w = 0, h = 0;
        frame->GetSize(&w, &h);
        // The shape says which format this is: 2:1 is a panorama, and 4:3 is a horizontal cross.
        const bool panorama = w >= 4 && h >= 2 && std::abs((int)w - 2 * (int)h) <= 2;
        int faceW = (int)w / 4, faceH = (int)h / 3;
        if (!panorama && (w == 0 || h == 0 || std::abs(faceW - faceH) > 1 || faceW < 1)) {
            char why[160];
            sprintf_s(why, "%ux%u is not a 2:1 panorama or a 4:3 horizontal cross", w, h);
            Fail(generation, why);
            break;
        }
        const auto started = std::chrono::steady_clock::now();
        // A panorama's width covers 360 degrees, and a cube face covers 90, so a quarter of it keeps its detail.
        int sourceFace = panorama ? faceW : std::min(faceW, faceH);
        int face = std::min(sourceFace, maxFace);
        IWICBitmapSource* source = frame;
        if (face != sourceFace) {
            LvrLog("Skybox faces are %d pixels, reducing to the %d pixel limit", sourceFace, face);
            if (FAILED(factory->CreateBitmapScaler(&scaler)) || FAILED(scaler->Initialize(frame, (UINT)std::lround((double)w * face / sourceFace), (UINT)std::lround((double)h * face / sourceFace), WICBitmapInterpolationModeFant))) { Fail(generation, "could not scale the image"); break; }
            source = scaler;
        }
        if (FAILED(factory->CreateFormatConverter(&converter)) || FAILED(converter->Initialize(source, GUID_WICPixelFormat32bppRGBA, WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom))) { Fail(generation, "could not convert the image"); break; }

        std::vector<uint8_t> faces[6];
        bool ok = true;
        try {
            UINT outW = 0, outH = 0;
            converter->GetSize(&outW, &outH);
            const UINT stride = outW * 4;
            std::vector<uint8_t> image((size_t)stride * outH);
            if (FAILED(converter->CopyPixels(nullptr, stride, (UINT)image.size(), image.data()))) { Fail(generation, "could not read the image's pixels"); ok = false; }
            if (ok && panorama) PanoramaToFaces(image.data(), (int)outW, (int)outH, face, faces);
            for (int i = 0; ok && !panorama && i < 6; i++) {
                faces[i].resize((size_t)face * face * 4);
                const uint8_t* src = image.data() + (size_t)kFaceCell[i][1] * face * stride + (size_t)kFaceCell[i][0] * face * 4;
                for (int y = 0; y < face; y++) memcpy(faces[i].data() + (size_t)y * face * 4, src + (size_t)y * stride, (size_t)face * 4);
            }
        } catch (const std::bad_alloc&) {
            Fail(generation, "not enough memory for an image of this size");
            ok = false;
        }
        if (!ok) break;

        std::lock_guard<std::mutex> l(g_lock);
        if (generation != g_generation) break;
        for (int i = 0; i < 6; i++) g_pendingFaces[i] = std::move(faces[i]);
        g_pendingFace = face;
        LvrLog("Skybox decoded: %ux%u %s, %d pixels per face, in %lld ms", w, h, panorama ? "panorama" : "cross", face,
               (long long)std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - started).count());
    } while (false);
    Drop(converter);
    Drop(scaler);
    Drop(frame);
    Drop(decoder);
    Drop(factory);
    if (SUCCEEDED(co)) CoUninitialize();
}

bool CreatePipeline(ID3D11Device* device) {
    if (g_state) return true;
    ID3D11Device1* device1 = nullptr;
    if (FAILED(device->QueryInterface(__uuidof(ID3D11Device1), (void**)&device1))) { LvrLog("Skybox needs Direct3D 11.1"); return false; }
    D3D_FEATURE_LEVEL level = device->GetFeatureLevel(), chosen;
    HRESULT state = device1->CreateDeviceContextState(0, &level, 1, D3D11_SDK_VERSION, __uuidof(ID3D11Device), &chosen, &g_state);
    device1->Release();
    if (FAILED(state)) { LvrLog("Skybox could not create a context state: 0x%08X", state); return false; }

    HMODULE compiler = LoadLibraryW(L"d3dcompiler_47.dll");
    auto compile = compiler ? (pD3DCompile)GetProcAddress(compiler, "D3DCompile") : nullptr;
    if (!compile) { LvrLog("Skybox could not load d3dcompiler_47.dll"); return false; }
    ID3DBlob* vs = nullptr; ID3DBlob* ps = nullptr; ID3DBlob* errors = nullptr;
    bool ok = SUCCEEDED(compile(kShader, sizeof kShader - 1, "skybox", nullptr, nullptr, "VSMain", "vs_5_0", 0, 0, &vs, &errors));
    if (ok) ok = SUCCEEDED(compile(kShader, sizeof kShader - 1, "skybox", nullptr, nullptr, "PSMain", "ps_5_0", 0, 0, &ps, &errors));
    if (!ok) LvrLog("Skybox shader failed: %s", errors ? (const char*)errors->GetBufferPointer() : "unknown");
    if (ok) ok = SUCCEEDED(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &g_vs)) && SUCCEEDED(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &g_ps));
    Drop(vs); Drop(ps); Drop(errors);
    if (!ok) return false;

    D3D11_BUFFER_DESC b{};
    b.ByteWidth = sizeof(Constants);
    b.Usage = D3D11_USAGE_DYNAMIC;
    b.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    b.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    D3D11_SAMPLER_DESC s{};
    s.Filter = D3D11_FILTER_ANISOTROPIC;
    s.AddressU = s.AddressV = s.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    s.MaxAnisotropy = 8;
    s.MaxLOD = D3D11_FLOAT32_MAX;
    D3D11_RASTERIZER_DESC r{};
    r.FillMode = D3D11_FILL_SOLID;
    r.CullMode = D3D11_CULL_NONE;
    D3D11_DEPTH_STENCIL_DESC d{};
    D3D11_BLEND_DESC bl{};
    bl.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    return SUCCEEDED(device->CreateBuffer(&b, nullptr, &g_constants)) && SUCCEEDED(device->CreateSamplerState(&s, &g_sampler)) &&
           SUCCEEDED(device->CreateRasterizerState(&r, &g_raster)) && SUCCEEDED(device->CreateDepthStencilState(&d, &g_depth)) &&
           SUCCEEDED(device->CreateBlendState(&bl, &g_blend));
}

// Runs inside a saved context state, because GenerateMips changes the pipeline.
void Upload(ID3D11Device* device, ID3D11DeviceContext* context, int face, std::vector<uint8_t> (&faces)[6], float fadeSeconds) {
    ID3D11Texture2D* texture = nullptr;
    ID3D11ShaderResourceView* view = nullptr;
    D3D11_TEXTURE2D_DESC t{};
    t.Width = t.Height = (UINT)face;
    t.MipLevels = 0;  // A full chain, which stops distant sky detail shimmering.
    t.ArraySize = 6;
    t.Format = DXGI_FORMAT_R8G8B8A8_TYPELESS;
    t.SampleDesc.Count = 1;
    t.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    t.MiscFlags = D3D11_RESOURCE_MISC_TEXTURECUBE | D3D11_RESOURCE_MISC_GENERATE_MIPS;
    HRESULT hr = device->CreateTexture2D(&t, nullptr, &texture);
    D3D11_SHADER_RESOURCE_VIEW_DESC v{};
    if (SUCCEEDED(hr)) {
        // sRGB, so the image is treated as gamma encoded when filtered and when written to the headset.
        v.Format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
        v.ViewDimension = D3D11_SRV_DIMENSION_TEXTURECUBE;
        v.TextureCube.MipLevels = (UINT)-1;
        hr = device->CreateShaderResourceView(texture, &v, &view);
    }
    if (FAILED(hr)) {
        char why[96];
        sprintf_s(why, "the GPU could not create a %d pixel cubemap (0x%08X)", face, (unsigned)hr);
        Drop(view);
        Drop(texture);
        Fail(g_generation, why);
        return;
    }
    texture->GetDesc(&t);
    for (UINT i = 0; i < 6; i++) context->UpdateSubresource(texture, D3D11CalcSubresource(0, i, t.MipLevels), nullptr, faces[i].data(), (UINT)face * 4, 0);
    context->GenerateMips(view);
    // A fade already running jumps to its end, so only two skies are ever held.
    if (g_nextView) {
        Drop(g_view);
        Drop(g_texture);
        g_view = g_nextView;
        g_texture = g_nextTexture;
        g_nextView = nullptr;
        g_nextTexture = nullptr;
    }
    if (g_view && fadeSeconds > 0) {
        g_nextView = view;
        g_nextTexture = texture;
        g_fadeSeconds = fadeSeconds;
        QueryPerformanceCounter(&g_fadeStart);
        g_mix = 0;
    } else {
        Drop(g_view);
        Drop(g_texture);
        g_view = view;
        g_texture = texture;
    }
    char ready[48];
    sprintf_s(ready, "ready %d", face);
    SetStatus(ready);
    LvrLog("Skybox on the GPU: %d pixels per face, %u mip levels", face, t.MipLevels);
}

}  // namespace

void Load(const wchar_t* path, int maxFaceSize, float fadeSeconds) {
    int generation;
    {
        std::lock_guard<std::mutex> l(g_lock);
        generation = ++g_generation;
        g_status = "decoding";
        g_clearRequested = fadeSeconds <= 0;
        g_pendingFade = fadeSeconds;
        g_pendingFace = 0;
        for (auto& f : g_pendingFaces) { f.clear(); f.shrink_to_fit(); }
    }
    std::thread(Decode, std::wstring(path), maxFaceSize, generation).detach();
}

bool Prepare(ID3D11Device* device, ID3D11DeviceContext* context) {
    if (g_gpuFailed) return false;
    if (!CreatePipeline(device)) {
        g_gpuFailed = true;
        SetStatus("failed: the skybox renderer could not start");
        return false;
    }
    int face = 0;
    float fade = 0;
    std::vector<uint8_t> faces[6];
    {
        std::lock_guard<std::mutex> l(g_lock);
        if (g_clearRequested) {
            Drop(g_view);
            Drop(g_texture);
            Drop(g_nextView);
            Drop(g_nextTexture);
            g_mix = 0;
            g_clearRequested = false;
        }
        if (g_pendingFace > 0) {
            fade = g_pendingFade;
            face = g_pendingFace;
            g_pendingFace = 0;
            for (int i = 0; i < 6; i++) faces[i] = std::move(g_pendingFaces[i]);
        }
    }
    if (face > 0) {
        ID3D11DeviceContext1* context1 = nullptr;
        if (SUCCEEDED(context->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&context1))) {
            ID3DDeviceContextState* saved = nullptr;
            context1->SwapDeviceContextState(g_state, &saved);
            Upload(device, context, face, faces, fade);
            context1->SwapDeviceContextState(saved, nullptr);
            Drop(saved);
            context1->Release();
        }
    }
    if (g_nextView) {
        LARGE_INTEGER now, freq;
        QueryPerformanceCounter(&now);
        QueryPerformanceFrequency(&freq);
        float t = (float)((double)(now.QuadPart - g_fadeStart.QuadPart) / (double)freq.QuadPart) / g_fadeSeconds;
        if (t >= 1.0f) {
            Drop(g_view);
            Drop(g_texture);
            g_view = g_nextView;
            g_texture = g_nextTexture;
            g_nextView = nullptr;
            g_nextTexture = nullptr;
            g_mix = 0;
        } else {
            g_mix = t * t * (3.0f - 2.0f * t);  // Eases in and out.
        }
    }
    return g_view != nullptr;
}

void BeginPass(ID3D11DeviceContext* context) {
    // Unity caches device state, so its whole pipeline state is set aside and restored afterwards.
    ID3D11DeviceContext1* context1 = nullptr;
    if (FAILED(context->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&context1))) return;
    context1->SwapDeviceContextState(g_state, &g_savedState);
    context1->Release();
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context->IASetInputLayout(nullptr);
    context->VSSetShader(g_vs, nullptr, 0);
    context->PSSetShader(g_ps, nullptr, 0);
    context->VSSetConstantBuffers(0, 1, &g_constants);
    context->PSSetConstantBuffers(0, 1, &g_constants);
    context->PSSetShaderResources(0, 1, &g_view);
    context->PSSetShaderResources(2, 1, &g_nextView);
    context->PSSetSamplers(0, 1, &g_sampler);
    context->RSSetState(g_raster);
    context->OMSetDepthStencilState(g_depth, 0);
    context->OMSetBlendState(g_blend, nullptr, 0xFFFFFFFF);
}

void DrawEye(ID3D11DeviceContext* context, ID3D11RenderTargetView* target, int width, int height,
             const float tangents[4], const float viewRotation[4], float skyYaw, float brightness, const Water& water) {
    // Views of the game's eye textures are remade only when a texture changes, as with a resolution change.
    ID3D11ShaderResourceView* game = nullptr;
    if (water.gameImage) {
        auto& textures = g_gameTextures;
        auto& views = g_gameViews;
        int slot = textures[0] == water.gameImage ? 0 : textures[1] == water.gameImage ? 1 : g_gameNext;
        if (textures[slot] != water.gameImage) {
            g_gameNext = (slot + 1) % 2;
            Drop(views[slot]);
            textures[slot] = water.gameImage;
            ID3D11Device* device = nullptr;
            context->GetDevice(&device);
            D3D11_SHADER_RESOURCE_VIEW_DESC v{};
            v.Format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
            v.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
            v.Texture2D.MipLevels = (UINT)-1;
            HRESULT hr = device->CreateShaderResourceView(water.gameImage, &v, &views[slot]);
            if (FAILED(hr)) LvrLog("Water reflection unavailable: could not view the game image (0x%08X)", hr);
            device->Release();
        }
        game = views[slot];
    }
    context->PSSetShaderResources(1, 1, &game);
    ID3D11ShaderResourceView* scenery[2] = {SceneryView(context, water.scenery), SceneryView(context, water.sceneryReflection)};
    const bool hasScenery = scenery[0] && scenery[1];
    context->PSSetShaderResources(3, 2, scenery);
    D3D11_MAPPED_SUBRESOURCE m{};
    if (FAILED(context->Map(g_constants, 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) return;
    auto* c = (Constants*)m.pData;
    memcpy(c->tangents, tangents, sizeof c->tangents);
    memcpy(c->viewRotation, viewRotation, sizeof c->viewRotation);
    // The inverse of the screen's turn, so the sky's forward stays in front of a recentered player.
    c->skyRotation[0] = 0; c->skyRotation[1] = -std::sin(skyYaw / 2); c->skyRotation[2] = 0; c->skyRotation[3] = std::cos(skyYaw / 2);
    c->tint[0] = brightness; c->tint[1] = water.time; c->tint[2] = water.ripple; c->tint[3] = (float)water.debug;
    c->eye[0] = water.eye[0]; c->eye[1] = water.eye[1]; c->eye[2] = water.eye[2]; c->eye[3] = water.height;
    c->screenPos[0] = water.screenPos[0]; c->screenPos[1] = water.screenPos[1]; c->screenPos[2] = water.screenPos[2]; c->screenPos[3] = game ? 1.0f : 0.0f;
    c->screenRotInv[0] = -water.screenRot[0]; c->screenRotInv[1] = -water.screenRot[1]; c->screenRotInv[2] = -water.screenRot[2]; c->screenRotInv[3] = water.screenRot[3];
    c->screenSize[0] = water.screenSize[0]; c->screenSize[1] = water.screenSize[1]; c->screenSize[2] = g_nextView ? g_mix : 0.0f; c->screenSize[3] = 0;
    c->skyTint[0] = water.tint[0]; c->skyTint[1] = water.tint[1]; c->skyTint[2] = water.tint[2]; c->skyTint[3] = 1;
    c->sceneryInfo[0] = (float)width; c->sceneryInfo[1] = (float)height; c->sceneryInfo[2] = hasScenery ? 1.0f : 0.0f; c->sceneryInfo[3] = 0;
    context->Unmap(g_constants, 0);
    D3D11_VIEWPORT v{0, 0, (float)width, (float)height, 0, 1};
    context->RSSetViewports(1, &v);
    context->OMSetRenderTargets(1, &target, nullptr);
    context->Draw(3, 0);
}

void EndPass(ID3D11DeviceContext* context) {
    ID3D11RenderTargetView* none = nullptr;
    context->OMSetRenderTargets(1, &none, nullptr);
    ID3D11ShaderResourceView* noView = nullptr;
    context->PSSetShaderResources(1, 1, &noView);
    ID3D11ShaderResourceView* noViews[2] = {nullptr, nullptr};
    context->PSSetShaderResources(3, 2, noViews);
    ID3D11DeviceContext1* context1 = nullptr;
    if (FAILED(context->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&context1))) return;
    context1->SwapDeviceContextState(g_savedState, nullptr);
    Drop(g_savedState);
    context1->Release();
}

void ReleaseGpu() {
    Drop(g_nextView); Drop(g_nextTexture); g_mix = 0;
    Drop(g_view); Drop(g_texture); Drop(g_vs); Drop(g_ps); Drop(g_constants);
    Drop(g_sampler); Drop(g_raster); Drop(g_depth); Drop(g_blend); Drop(g_state);
    for (int i = 0; i < 2; i++) { Drop(g_gameViews[i]); g_gameTextures[i] = nullptr; }
    for (auto& entry : g_sceneryViews) entry.second->Release();
    g_sceneryViews.clear();
    g_gpuFailed = false;
    std::lock_guard<std::mutex> l(g_lock);
    g_generation++;  // Abandons any decode still running; the next session starts its own.
    g_status = "none";
    g_pendingFace = 0;
    g_clearRequested = false;
    for (auto& f : g_pendingFaces) { f.clear(); f.shrink_to_fit(); }
}

void Status(char* buffer, int size) {
    std::lock_guard<std::mutex> l(g_lock);
    strncpy_s(buffer, size, g_status.c_str(), _TRUNCATE);
}

}  // namespace sky
