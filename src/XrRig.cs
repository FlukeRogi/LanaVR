using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime;
using PoL.Rendering;
using UnityEngine;

namespace LanaVR
{
    /// <summary>
    /// Renders the game once per eye and presents it as a window fixed in the room.
    ///
    /// The virtual screen is a real rectangle in tracking space. Each eye's camera is moved by how far
    /// that eye is from the nominal viewing position, and its off-axis frustum always passes through the
    /// same rectangle at the game's focal plane. At the nominal position the framing matches the flat
    /// game exactly. Moving your head changes the view the way a real window does.
    /// </summary>
    public class XrRig : MonoBehaviour
    {
        public XrRig(IntPtr p) : base(p) { }

        public static XrRig I;

        const int EvCreateSession = 1, EvSubmit = 2, EvResize = 3, EvShutdown = 4;
        const float Aspect = 16f / 9f;

        bool _loaded, _created, _active, _hasCenter, _camsHidden, _inRender, _textureDirty;
        bool _sessionRequested, _hadSession, _steamVrSession;
        UnityEngine.InputSystem.InputSettings.BackgroundBehavior _savedBackground;
        readonly Dictionary<IntPtr, (PoL.Rendering.ReflectionCamera refl, int original)> _shrunk = new Dictionary<IntPtr, (PoL.Rendering.ReflectionCamera, int)>();
        float _nextCreate, _retryDelay = 2.5f;
        IntPtr _ev;
        RenderTexture[] _eye = new RenderTexture[2], _submit = new RenderTexture[2];
        int _rtW, _rtH, _recW, _recH;
        Camera _uiCam;
        readonly List<Camera> _screenCams = new List<Camera>();
        readonly List<Camera> _reflCams = new List<Camera>();
        readonly List<VolumeLight> _volumeLights = new List<VolumeLight>();
        readonly List<Canvas> _routed = new List<Canvas>();
        float _nextSweep, _nextFullSweep;
        int _sceneSignature;

        static int SceneSignature()
        {
            int sig = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                sig = sig * 31 + s.handle * (s.isLoaded ? 2 : 1);
            }
            return sig;
        }
        float _focal = 30f;

        // Screen pose in OpenXR LOCAL space.
        Vector3 _centerHead;
        float _centerYaw;
        Vector3 _screenPos;
        Quaternion _screenRot = Quaternion.identity;
        bool _recenterRequested;

        // Diagnostics.
        public float FrameMs;
        public bool Active => _active;
        public int EyeWidth => _rtW;
        public int EyeHeight => _rtH;
        public float Focal => _focal;
        public string HeadInfo = "";

        readonly HashSet<string> _reportedCams = new HashSet<string>();
        public bool VolumeCameraInEyePass
        {
            get
            {
                foreach (var c in _screenCams) if (c != null && c.GetComponent<VolumeLight>() != null) return true;
                return false;
            }
        }
        public int VolumeLightCount => _volumeLights.Count;


        void Start()
        {
            I = this;
            if (Cfg.VrDisabled)
            {
                Plugin.L.LogInfo("VR is off (OpenXR.Runtime = off), playing flat");
                return;
            }
            var dir = Path.GetDirectoryName(typeof(XrRig).Assembly.Location);
            _loaded = Native.Load(dir);
            if (!_loaded) return;
            _ev = Native.LVR_GetRenderEvent();
            var hook = DelegateSupport.ConvertDelegate<Canvas.WillRenderCanvases>(new Action(OnWillRender));
            Canvas.willRenderCanvases = Il2CppSystem.Delegate.Combine(Canvas.willRenderCanvases, hook).Cast<Canvas.WillRenderCanvases>();
            StartCoroutine(Loop().WrapToIl2Cpp());
        }

        IEnumerator Loop()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                try { Tick(); }
                catch (Exception e) { Plugin.L.LogError(e); }
            }
        }

        void Tick()
        {
            if (!_created)
            {
                if (Time.unscaledTime < _nextCreate) return;
                // Back off while no headset is connected, so a long flat session stays quiet.
                _retryDelay = Mathf.Min(_retryDelay * 2f, 30f);
                _nextCreate = Time.unscaledTime + _retryDelay;
                // Creating an instance launches SteamVR, so it must not come back after being quit.
                if (_steamVrSession && System.Diagnostics.Process.GetProcessesByName("vrserver").Length == 0) return;
                int r = Native.LVR_Create(Cfg.ResolveRuntime());
                Native.PumpLogs();
                if (r != 0)
                {
                    if (r == 2) _nextCreate = float.MaxValue;
                    return;
                }
                _created = true;
                _retryDelay = 2.5f;
                _steamVrSession = Native.RuntimeName.IndexOf("SteamVR", StringComparison.OrdinalIgnoreCase) >= 0;
                Scenery.OnSessionCreated();
                Skybox.OnSessionCreated();
                Native.LVR_GetRecommended(out _recW, out _recH);
                CreateTextures();
                Native.LVR_SetEyeTextures(_submit[0].GetNativeTexturePtr(), _submit[1].GetNativeTexturePtr(), _rtW, _rtH);
                GL.IssuePluginEvent(_ev, EvCreateSession);
                _sessionRequested = true;
                return;
            }

            float t0 = Time.realtimeSinceStartup;

            XrFrame f;
            Native.LVR_BeginFrame(out f);
            Native.PumpLogs();
            if (f.running == 0)
            {
                if (_active) Deactivate();
                if (Native.LVR_ExitRequested() != 0) HandleSessionEnded();
                return;
            }
            _hadSession = true;
            if (!_active) Activate();
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            if (_textureDirty) ApplyResolution();
            Skybox.UpdateTint(Time.unscaledDeltaTime);
            Scenery.UpdateReveal();
            if (f.viewsValid != 0 && _hasCenter) WatchForHeadsetOn(f);
            if (f.viewsValid != 0 && (!_hasCenter || _recenterRequested)) Recenter(f);
            else if (f.viewsValid == 0 && !_hasCenter) PlaceDefaultScreen();

            if (f.shouldRender != 0)
            {
                _inRender = true;
                try { RenderEyes(f); }
                finally { _inRender = false; }
                Graphics.Blit(_eye[0], (RenderTexture)null);
            }
            GL.IssuePluginEvent(_ev, EvSubmit);
            ShowCameras();
            FrameMs = Mathf.Lerp(FrameMs, (Time.realtimeSinceStartup - t0) * 1000f, 0.05f);
            PerfWatch.Tick();
            if (Time.unscaledTime >= _nextSweep)
            {
                _nextSweep = Time.unscaledTime + 0.5f;
                float s0 = Time.realtimeSinceStartup;
                // Searching all the game's objects takes a few milliseconds, so it waits for a level to load or
                // unload, with a check every few seconds for anything that appears otherwise.
                int scenes = SceneSignature();
                bool full = scenes != _sceneSignature || Time.unscaledTime >= _nextFullSweep;
                if (full)
                {
                    _sceneSignature = scenes;
                    _nextFullSweep = Time.unscaledTime + 5f;
                    Prune();
                    RouteCanvases();
                }
                Skybox.Poll(full);
                Scenery.Poll();
                WindowFocus.Poll();
                PerfWatch.Sweep((Time.realtimeSinceStartup - s0) * 1000f);
            }
        }

        /// <summary>Lets go of cameras and canvases from levels that have since unloaded.</summary>
        void Prune()
        {
            _routed.RemoveAll(new Predicate<Canvas>(c => c == null));
            List<IntPtr> dead = null;
            foreach (var kv in _shrunk) if (kv.Value.refl == null) (dead ??= new List<IntPtr>()).Add(kv.Key);
            if (dead != null) foreach (var k in dead) _shrunk.Remove(k);
        }

        /// <summary>The runtime ended the session: the headset was disconnected, the runtime was closed, or
        /// another VR app took over. The game stays playable flat, and VR resumes if the runtime returns.</summary>
        void HandleSessionEnded()
        {
            Plugin.L.LogInfo("OpenXR session ended, will retry");
            GL.IssuePluginEvent(_ev, EvShutdown);
            _created = false;
            _nextCreate = Time.unscaledTime + 5f;
        }

        void CreateTextures()
        {
            float scale = Mathf.Clamp(Cfg.ResolutionScale.Value, 0.25f, 2f);
            _rtW = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(_recW, 2560) * scale), 640, 4096) & ~1;
            _rtH = Mathf.RoundToInt(_rtW / Aspect) & ~1;
            for (int i = 0; i < 2; i++)
            {
                if (_eye[i] != null) { _eye[i].Release(); UnityEngine.Object.Destroy(_eye[i]); }
                if (_submit[i] != null) { _submit[i].Release(); UnityEngine.Object.Destroy(_submit[i]); }
                _eye[i] = new RenderTexture(_rtW, _rtH, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "LanaVR Eye " + i };
                _eye[i].hideFlags = HideFlags.HideAndDontSave;
                _eye[i].Create();
                // Mipmaps give the water's reflection of the screen a soft image to sample.
                _submit[i] = new RenderTexture(_rtW, _rtH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "LanaVR Submit " + i, useMipMap = true, autoGenerateMips = true };
                _submit[i].hideFlags = HideFlags.HideAndDontSave;
                _submit[i].Create();
            }
            Plugin.L.LogInfo($"Eye render size {_rtW}x{_rtH}, headset recommends {_recW}x{_recH}");
        }

        public void RequestResolutionChange() { _textureDirty = true; }

        void ApplyResolution()
        {
            _textureDirty = false;
            CreateTextures();
            Native.LVR_SetEyeTextures(_submit[0].GetNativeTexturePtr(), _submit[1].GetNativeTexturePtr(), _rtW, _rtH);
            GL.IssuePluginEvent(_ev, EvResize);
        }

        void Activate()
        {
            _active = true;
            _hasCenter = false;
            _activatedAt = Time.unscaledTime;
            _offCenterSince = -1;
            _recenterRequested = false;
            Plugin.L.LogInfo("VR session running");
            var go = new GameObject("LanaVR UI Camera");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _uiCam = go.AddComponent<Camera>();
            _uiCam.enabled = false;
            _uiCam.orthographic = true;
            _uiCam.orthographicSize = 1;
            _uiCam.nearClipPlane = 0.1f;
            _uiCam.farClipPlane = 10f;
            _uiCam.clearFlags = CameraClearFlags.Depth;
            _uiCam.cullingMask = 1 << 5;
            _uiCam.depth = 1000;
            _uiCam.allowHDR = false;
            _uiCam.allowMSAA = false;
            _uiCam.targetTexture = _eye[0];
            // The desktop resolution is left alone. Unity saves it to the registry on exit, so changing it
            // here would leave the flat game stuck at that size after quitting.
            RouteCanvases();
            // The game disables all input devices when its window loses focus. In a headset the desktop
            // window is rarely focused, so that would leave the player without a gamepad.
            var input = UnityEngine.InputSystem.InputSystem.settings;
            _savedBackground = input.backgroundBehavior;
            input.backgroundBehavior = UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
        }

        void Deactivate()
        {
            _active = false;
            Plugin.L.LogInfo("VR session stopped, returning to the flat game");
            ShowCameras();
            foreach (var c in _routed)
            {
                if (c == null) continue;
                c.renderMode = RenderMode.ScreenSpaceOverlay;
            }
            _routed.Clear();
            if (_uiCam != null) UnityEngine.Object.Destroy(_uiCam.gameObject);
            foreach (var kv in _shrunk)
            {
                if (kv.Value.refl == null) continue;
                kv.Value.refl.renderResolutionBase = kv.Value.original;
                kv.Value.refl.RebuildRenderTargetTexture();
            }
            _shrunk.Clear();
            QualitySettings.vSyncCount = 1;
            Wishfully.Core.FrameRateHandler.RefreshFrameRate();
            UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior = _savedBackground;
        }

        /// <summary>Overlay canvases draw straight to the backbuffer and never reach the eye textures, so
        /// they are moved onto a camera that draws into each eye after the 3D pass.</summary>
        void RouteCanvases()
        {
            if (_uiCam == null) return;
            foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (c.renderMode != RenderMode.ScreenSpaceOverlay || !c.isRootCanvas || !c.gameObject.scene.IsValid()) continue;
                c.renderMode = RenderMode.ScreenSpaceCamera;
                c.worldCamera = _uiCam;
                c.planeDistance = 1f;
                _routed.Add(c);
            }
        }

        /// <summary>The engine's own render pass runs after this. Cameras stay enabled during game logic
        /// because scripts read Camera.main, and are only hidden for the pass the mod replaces.</summary>
        void OnWillRender()
        {
            if (!_active || _inRender || _camsHidden) return;
            _camsHidden = true;
            _screenCams.Clear();
            _reflCams.Clear();
            _volumeLights.Clear();
            foreach (var c in Camera.allCameras)
            {
                if (c == _uiCam || !c.enabled) continue;
                // A volumetric light's camera renders its depth from the light's own point of view, into
                // buffers set with SetTargetBuffers, so targetTexture reads as null. It is not a screen
                // camera: the engine renders it as usual, and it needs no per-eye pass.
                var volume = c.GetComponent<VolumeLight>();
                if (volume != null)
                {
                    _volumeLights.Add(volume);
                    continue;
                }
                var refl = c.GetComponent<PoL.Rendering.ReflectionCamera>();
                if (refl != null)
                {
                    _reflCams.Add(c);
                    ShrinkReflection(refl);
                }
                else if (c.targetTexture == null) _screenCams.Add(c);
                else continue;
                c.enabled = false;
                // Any other camera that reaches the eye pass is worth seeing in the log.
                if (refl == null && c != Camera.main && _reportedCams.Add(c.name)) Plugin.L.LogInfo($"Extra screen camera in the eye pass: {c.name}");
            }
            _screenCams.Sort((a, b) => a.depth.CompareTo(b.depth));
        }

        /// <summary>The game sizes its water reflection for the desktop, up to 3640x2048. It is now rendered
        /// twice per frame into a much smaller eye image, so a smaller texture looks the same and costs less.</summary>
        void ShrinkReflection(PoL.Rendering.ReflectionCamera refl)
        {
            // Checked every frame by its real size, not by whether this camera was seen before: the game can
            // rebuild the texture at full size, and a new level's camera can reuse an old one's address. Either
            // would otherwise leave a full-size reflection rendering twice a frame until the game restarts.
            var target = refl.reflectionRenderTarget;
            int wanted = Mathf.Min(_rtH, 1024);
            if (target == null || target.height <= wanted) return;
            if (!_shrunk.TryGetValue(refl.Pointer, out var known) || known.refl == null || known.refl.Pointer != refl.Pointer)
                _shrunk[refl.Pointer] = (refl, refl.renderResolutionBase);
            refl.renderResolutionBase = wanted;
            refl.RebuildRenderTargetTexture();
            PerfWatch.Note($"water reflection was {target.width}x{target.height}, reduced");
        }

        void ShowCameras()
        {
            if (!_camsHidden) return;
            _camsHidden = false;
            foreach (var c in _screenCams) if (c != null) c.enabled = true;
            foreach (var c in _reflCams) if (c != null) c.enabled = true;
        }

        public void RequestRecenter() { _recenterRequested = true; }

        // The first pose of a session is often the headset resting on a desk or lap, which would leave the screen
        // far below the player's eyes once it is put on. So for the first two minutes, a head that settles more
        // than 25 cm above or below the screen's centre recenters by itself.
        const float SettleWindow = 120f, SettleDistance = 0.25f, SettleSeconds = 2f;
        float _activatedAt, _offCenterSince = -1;

        void WatchForHeadsetOn(XrFrame f)
        {
            float now = Time.unscaledTime;
            if (now - _activatedAt > SettleWindow) return;
            float y = (f.left.py + f.right.py) * 0.5f;
            if (Mathf.Abs(y - _centerHead.y) <= SettleDistance) { _offCenterSince = -1; return; }
            if (_offCenterSince < 0) { _offCenterSince = now; return; }
            if (now - _offCenterSince < SettleSeconds) return;
            _offCenterSince = -1;
            _recenterRequested = true;
            Plugin.L.LogInfo($"Head is {y - _centerHead.y:+0.00;-0.00} m from the screen's height, recentering");
        }

        static Vector3 V(EyePose e) => new Vector3(e.px, e.py, e.pz);

        // OpenXR is right handed. These helpers treat Vector3 and Quaternion as plain data, independent
        // of Unity's left-handed helpers.
        static Vector3 Rot(Quaternion q, Vector3 v)
        {
            var u = new Vector3(q.x, q.y, q.z);
            return v + 2f * Vector3.Cross(u, Vector3.Cross(u, v) + q.w * v);
        }
        static Quaternion YawQ(float yaw) => new Quaternion(0, Mathf.Sin(yaw / 2), 0, Mathf.Cos(yaw / 2));
        static Quaternion Conj(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        void Recenter(XrFrame f)
        {
            _recenterRequested = false;
            var a = V(f.left); var b = V(f.right);
            _centerHead = (a + b) * 0.5f;
            var q = new Quaternion(f.left.qx, f.left.qy, f.left.qz, f.left.qw);
            var fw = Rot(q, new Vector3(0, 0, -1));
            _centerYaw = Mathf.Atan2(-fw.x, -fw.z);
            _hasCenter = true;
            PlaceScreen();
            Plugin.L.LogInfo($"Recentered: head {_centerHead} yaw {_centerYaw * Mathf.Rad2Deg:F1}");
        }

        void PlaceDefaultScreen()
        {
            _centerHead = Vector3.zero;
            _centerYaw = 0;
            PlaceScreen();
        }

        void PlaceScreen()
        {
            _screenRot = YawQ(_centerYaw);
            _screenPos = _centerHead + Rot(_screenRot, new Vector3(0, 0, -Mathf.Max(0.3f, Cfg.ScreenDistance.Value)));
            float w = Mathf.Max(0.2f, Cfg.ScreenWidth.Value);
            Native.LVR_SetScreen(_screenPos.x, _screenPos.y, _screenPos.z, _screenRot.x, _screenRot.y, _screenRot.z, _screenRot.w, w, w / Aspect);
            Native.LVR_SetSkybox(1f, _centerYaw);
            Native.LVR_SetWater(_centerHead.y, Cfg.WaterHeight.Value, Mathf.Max(0f, Cfg.RippleStrength.Value));
            // A negative quality tells the native layer to leave the screen's reflection out.
            Native.LVR_SetReflection(Cfg.ScreenReflection.Value ? Mathf.Clamp(Mathf.Round(Cfg.ReflectionQuality.Value), 0f, 2f) : -1f);
        }

        float FocalDistance(Camera main)
        {
            float d = -1f;
            var tr = main.transform;
            var lana = PoL.Characters.Lana.LanaMachine.instance;
            if (lana != null) d = Vector3.Dot(lana.transform.position - tr.position, tr.forward);
            if (d <= 0)
            {
                // Without Lana, as in the main menu, use the z = 0 gameplay plane.
                float fz = tr.forward.z;
                d = Mathf.Abs(fz) > 0.01f ? -tr.position.z / fz : -1f;
            }
            return d > 0 ? d : _focal;
        }

        void RenderEyes(XrFrame f)
        {
            PlaceScreen();
            var conj = Conj(_screenRot);
            var local = new[] { Rot(conj, V(f.left) - _screenPos), Rot(conj, V(f.right) - _screenPos) };
            if (f.viewsValid == 0)
            {
                float z = Cfg.ScreenDistance.Value;
                local[0] = new Vector3(-0.0315f, 0, z);
                local[1] = new Vector3(0.0315f, 0, z);
            }
            var nominal = new Vector3(0, 0, Mathf.Max(0.3f, Cfg.ScreenDistance.Value));
            var mid = (local[0] + local[1]) * 0.5f;
            HeadInfo = $"mid={mid.x:F3},{mid.y:F3},{mid.z:F3} ipd={(local[1] - local[0]).magnitude:F4}";
            float strength = Cfg.Stereo.Value, gain = Cfg.HeadTracking.Value;
            var head = (mid - nominal) * gain;
            float screenW = Mathf.Max(0.2f, Cfg.ScreenWidth.Value);

            Camera main = null;
            foreach (var c in _screenCams) if (c != null && (main == null || c.GetComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>() != null)) main = c;
            if (main != null)
            {
                float target = Mathf.Clamp(FocalDistance(main), main.nearClipPlane * 1.2f, 300f);
                _focal = Mathf.Abs(target - _focal) > 40f ? target : Mathf.Lerp(_focal, target, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 2f));
            }

            for (int i = 0; i < 2; i++)
            {
                var delta = head + (local[i] - mid) * strength;
                var rt = _eye[i];
                if (_screenCams.Count == 0)
                {
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    GL.Clear(true, true, Color.black);
                    RenderTexture.active = prev;
                }

                foreach (var cam in _screenCams)
                {
                    if (cam == null) continue;
                    var tr = cam.transform;
                    var basePos = tr.position;
                    var baseRot = tr.rotation;
                    float halfH = _focal * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                    float halfW = halfH * Aspect;
                    float u = 2f * halfW / screenW;

                    // Screen space: +x right, +y up, +z towards the viewer. Camera space: +z forward.
                    float ex = delta.x * u, ey = delta.y * u;
                    float dz = Mathf.Max(_focal + delta.z * u, _focal * 0.25f);
                    float forward = _focal - dz;

                    // The camera really moves to the eye for this render, and is put back straight after.
                    // Shaders read its position, and the volumetric lights ray-march from it.
                    tr.position = basePos + baseRot * new Vector3(ex, ey, forward);

                    float n = cam.nearClipPlane, far = cam.farClipPlane, k = n / dz;
                    cam.projectionMatrix = Frustum((-halfW - ex) * k, (halfW - ex) * k, (-halfH - ey) * k, (halfH - ey) * k, n, far);

                    if (cam == main)
                    {
                        // The game publishes these from the main camera once per frame, so each eye needs
                        // its own values or anything reading them lands in the wrong place.
                        var variables = ShaderVariables.Instance;
                        if (variables != null) variables.LateUpdate();
                        foreach (var v in _volumeLights) if (v != null) v.UpdateRenderParams();

                        // The water reflection is a mirror camera, so each eye's offset is mirrored in
                        // the horizontal water plane and applied through its view matrix.
                        var worldOffset = baseRot * new Vector3(ex, ey, forward);
                        var mirrored = new Vector3(worldOffset.x, -worldOffset.y, worldOffset.z);
                        foreach (var rc in _reflCams)
                        {
                            if (rc == null) continue;
                            rc.ResetWorldToCameraMatrix();
                            var rv = rc.worldToCameraMatrix;
                            var shift = rv.MultiplyVector(mirrored);
                            rv.m03 -= shift.x;
                            rv.m13 -= shift.y;
                            rv.m23 -= shift.z;
                            rc.worldToCameraMatrix = rv;
                            rc.Render();
                            rc.ResetWorldToCameraMatrix();
                        }
                    }
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = null;
                    cam.ResetProjectionMatrix();
                    tr.position = basePos;
                }

                _uiCam.targetTexture = rt;
                _uiCam.Render();
                // The target stays set. Clearing it would briefly change the camera's pixel size, and every
                // CanvasScaler would then rebuild the whole UI.

                // Unity stores render textures bottom-up on D3D. OpenXR expects row 0 at the top.
                Graphics.Blit(rt, _submit[i], new Vector2(1, -1), new Vector2(0, 1));
            }
            Scenery.Render(f, _centerHead, _centerYaw, _recW, _recH);
            // Back to the true camera values, for anything that reads them before the next frame.
            if (main != null)
            {
                var restored = ShaderVariables.Instance;
                if (restored != null) restored.LateUpdate();
                foreach (var v in _volumeLights) if (v != null) v.UpdateRenderParams();
            }
        }

        internal static Matrix4x4 Frustum(float l, float r, float b, float t, float n, float f)
        {
            var m = new Matrix4x4();
            m.m00 = 2f * n / (r - l);
            m.m02 = (r + l) / (r - l);
            m.m11 = 2f * n / (t - b);
            m.m12 = (t + b) / (t - b);
            m.m22 = -(f + n) / (f - n);
            m.m23 = -2f * f * n / (f - n);
            m.m32 = -1f;
            return m;
        }
    }
}

