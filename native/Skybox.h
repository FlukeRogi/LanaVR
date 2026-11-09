// Skybox backdrop: decodes a 2:1 panorama or a 4:3 horizontal-cross cubemap and draws it for each eye.
#pragma once
#include <d3d11_1.h>

void LvrLog(const char* fmt, ...);

namespace sky {

// The water plane and everything the shader needs to reflect the game screen in it.
struct Water {
    float eye[3];          // This eye's position in tracking space
    float height;          // Water level in tracking space
    float time;            // Seconds, for the ripples
    float ripple;          // 1 is the normal strength, 0 is still water
    float tint[3];         // Linear colour multiplier for the sky and water, matching the game's grading
    float reveal;
    float reflectionQuality;  // 0 low, 1 medium, 2 high sharpness for the screen's reflection, negative for none          // Linear multiplier for the whole layer, for fading it in from black
    int debug;             // Test output: 1 water distance, 2 screen hit, 3 no water
    float screenPos[3];
    float screenRot[4];
    float screenSize[2];
    ID3D11Texture2D* gameImage;  // This eye's game image, for the reflection. May be null.
    // The 3D scenery as drawn for this eye, with alpha, in the same projection as this layer. May be null.
    ID3D11Texture2D* scenery;
    ID3D11Texture2D* sceneryReflection;
};

// Starts decoding on a background thread. Safe to call from any thread. With a fade time the current sky
// stays until the new one is ready, then crossfades, and stays if the new one can't load. With no fade time
// the current sky goes immediately.
void Load(const wchar_t* path, int maxFaceSize, float fadeSeconds);

// All of these run on Unity's render thread.
bool Prepare(ID3D11Device* device, ID3D11DeviceContext* context);  // True once a sky can be drawn.
void BeginPass(ID3D11DeviceContext* context);
void DrawEye(ID3D11DeviceContext* context, ID3D11RenderTargetView* target, int width, int height,
             const float tangents[4], const float viewRotation[4], float skyYaw, float brightness, const Water& water);
void EndPass(ID3D11DeviceContext* context);
void ForgetSceneryViews();
void ReleaseGpu();

// For logs and tests: "none", "decoding", "failed: <why>", or "ready <face size>".
void Status(char* buffer, int size);

}  // namespace sky
