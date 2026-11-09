# LanaVR: developer notes

Plays Planet of Lana on a floating virtual screen in VR, with a full render per eye and window-style head tracking. Works through OpenXR on SteamVR and Virtual Desktop VDXR. Player documentation is in [README.md](README.md) and [install/README.txt](install/README.txt).

## How it works

The game is 64-bit IL2CPP on Unity 2020.3.41 (D3D11, built-in pipeline, gamma colour space), so the mod runs on BepInEx 6.

- **[src/XrRig.cs](src/XrRig.cs)** renders each eye. The virtual screen is a rectangle in tracking space, mapped to the plane in the game world at Lana's depth, so she sits on the screen. Each eye gets a view offset from the nominal viewing position, and an off-axis frustum through that same rectangle. At the nominal position the framing matches the flat game.
- **[native/LanaVRNative.cpp](native/LanaVRNative.cpp)** owns the OpenXR session and submits one quad layer per eye. The quads are world-locked, so the compositor keeps the screen steady even if the game's frame rate dips.
- **[native/Skybox.cpp](native/Skybox.cpp)** draws the backdrop as a projection layer behind the screen. Images are 2:1 panoramas or 4:3 horizontal-cross cubemaps of any size in `skybox/`, told apart by shape; panoramas are converted to cube faces as they load. [src/Skybox.cs](src/Skybox.cs) watches the loaded `Chapter NN Backdrop...` scene and uses `skybox/skyboxes.txt` to pick an image, and the shader crossfades between two cubemaps. They decode on a background thread, and Unity's device state is set aside with `SwapDeviceContextState` while the sky draws.
- **Colour fades:** `Tint:` lines in `skybox/skyboxes.txt` tie one of the game's post-process volumes to a colour multiplier for a given backdrop. Each frame the mod blends them by volume weight in priority order, as the game does, and tints the sky and water, but not the reflected game image, which is already graded. Chapter 1 uses this for its Sunset and Gray fades. A `Tint: * / PP Cave` line applies to every backdrop, and dims the sky in the game's caves. `Shade:` lines give colours by distance along a backdrop, for places where the game's painted sky itself changes. `tests/scan_sky.ps1` finds these by measuring the backdrop alone along every level.
- **Water** is in the same shader. It intersects each eye's ray with a plane at the floor (from the runtime's STAGE space, or 1.5 m below the recentered head), shades it from layered noise slopes that fade out below pixel size, reflects the skybox, and follows the reflected ray to the screen rectangle to sample that eye's game image, using mipmaps on the submitted textures for softness.
- **[src/VrMenu.cs](src/VrMenu.cs)** clones the game's Settings Audio view and a Settings list button at runtime, so the page uses the game's real fonts, sprites, sliders and navigation.

Things that are easy to get wrong here:

- **UI camera target:** never clear `_uiCam.targetTexture`. That changes the camera's pixel size, and every `CanvasScaler` then rebuilds the whole UI. This cost 12 ms per frame.
- **Desktop resolution:** never call `Screen.SetResolution`. Unity saves it to the registry on exit, which breaks the flat game afterwards.
- **Input focus:** the game disables input devices when unfocused, so `backgroundBehavior` is set to `IgnoreFocus` during VR.
- **Abstract UI classes:** `UISelectable` and `SelectableSliderChoice` are abstract, which the interop stubs do not show. `AddComponent` on them returns null.
- **Back result:** `CustomView.Back` returning `true` tells the game to leave the view.
- **Hidden buttons:** the game keeps sending movement input to the selected UI object during play, and it reaches any button that is still interactable. Anything added to a hidden menu must be switched off explicitly while hidden, or walking selects it and jump presses it.
- **Drone lights:** each `VolumeLight` has its own camera using `SetTargetBuffers`, so `targetTexture` reads null even though it isn't a screen camera. Leave it to the engine.
- **Per-eye shader values:** the volumetric shader ray-marches from the camera position, and `ShaderVariables` publishes camera data once per frame. So the camera really moves to each eye, and those values are refreshed per eye.
- **Steam Cloud:** it uploads modified saves when the game closes, even after a direct launch. Lab mode blocks `Platform.Save` so tests can't change a player's progress.

## Skies

`skybox/skyboxes.txt` maps each of the game's backdrops to a sky from the purchased Fantasy Skybox pack, which stays out of the repository. `tools\pack_skies.ps1` packs the listed images into `skybox\*.lvsky`, encrypted with AES-256-CBC. The key is in `native\SkyKey.h`, which the script creates the first time and which stays out of the repository. A build without it still works with plain PNG and JPG skies, but can't read the included ones. The native loader decrypts them in memory on its background thread with Windows' hardware AES.

The pack's 4096-wide images are doubled with Real-ESRGAN (`tools/upscale_skies.ps1`). The Sky Quality setting limits each cube face to 512 (Low), 1024 (Medium, the default) or 2048 (High), about 8, 33 or 134 MB of video memory per sky. On the test PC, fading in a High sky took a worst frame of 45 to 50 ms, against 22 to 24 ms for the others.

## 3D scenery

[src/Scenery.cs](src/Scenery.cs) puts the player on a wooden platform with a half-sunk boat and poles around it, using the game's own village meshes and textures, read from the player's install at run time. The mod ships none of this art.

- **Loading:** `Chapter 01 Scene 10` is loaded additively once, from the main menu. Its roots are switched off in `sceneLoaded`, before any `Start`, then the wanted meshes are copied and the level is unloaded. Later scene unloads can't remove the copies, as they stay referenced.
- **Choosing props:** statically batched renderers can't be copied usefully, so `Layout` names only unbatched mesh and material pairs. The Lab commands `propload` and `propgallery` photograph what's available. The textures have lighting painted in for the game's side view, so some have dark bands.
- **Look:** an unlit shader shows the art as painted, independent of the running level's lights. The native sky shader then scales it by the sky's average colour and the game-matching tint, so no per-sky setup is needed.
- **Position:** the scenery sits at the world's origin, and is only active while its own camera draws, so the game's cameras never see it. It must stay near the origin: placed 20 km away to avoid those cameras, it snapped to a 2 mm grid, which showed in a headset as a fast jitter.
- **Drawing:** a separate camera draws each eye into a transparent image at the sky layer's projection. A depth-writing plane with alpha 0 hides everything below the water. A second pass with a mirrored view and an oblique near plane gives the water's reflection. The native shader blends both into the sky layer, which sits behind the screen quads, so scenery can never block the game. There's no extra compositor layer.

## Build

Needs Visual Studio C++ build tools, CMake and the .NET 8 SDK, with the game installed and BepInEx run once to generate `BepInEx\interop`. The OpenXR loader NuGet package is unpacked in `tools\openxr`.

```bash
cmake -S native -B native/build -G "Visual Studio 17 2022" -A x64
```

```bash
cmake --build native/build --config Release
```

```bash
dotnet build LanaVR.csproj -c Release
```

The C# build deploys to the game folder, including every image in `skybox/`. `tools\make_package.ps1` creates the release zip and only ships images that `skybox/skyboxes.txt` uses, so samples and licensed originals can sit in that folder without being distributed.

## Test

```bash
powershell -ExecutionPolicy Bypass -File tests/run_tests.ps1 -Slot 2
```

This drives the real game with no headset, through two test tools:

- **Mock HMD** ([native/mock/MockRuntime.cpp](native/mock/MockRuntime.cpp)): a small OpenXR runtime with scripted head poses that saves what each eye receives. It also reports OpenXR API misuse.
- **Virtual gamepad** (in [src/Lab.cs](src/Lab.cs)): raw Input System events, so menus are exercised through the game's real input path.

The test command channel only exists when `Debug.LabCommands = true`, which also blocks all save writes. The suite uses the given save slot, starting a new game there if it is empty. It runs extra drone-light checks when that slot's scene has a drone. Lab command `waterdebug` makes the sky shader output test data: 1 is water distance, 2 is where reflected rays hit the screen, 3 hides the water. To compare modes, freeze time with the `ts 0` lab command and capture the same frame in VR and, after creating `mock_exit.txt`, in flat.

`tests\steamvr_null.ps1 -Enable` switches SteamVR to its null HMD driver, and `-Disable` restores your settings. Steam may wait on a dialog when launching with SteamVR already running, so start the game exe directly with `SteamAppId=1608230` set.

`tests\capture_backdrops.ps1` jumps to each backdrop and saves a screenshot to `backdrop_previews`. Jumping straight to some levels fails, even in the flat game (Chapter 01 Scene 60 is one).

`native\build\Release\prof.exe` is a sampling profiler for finding main-thread costs. Unity's symbols come from `symbolserver.unity3d.com`.
