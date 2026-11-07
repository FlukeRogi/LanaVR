using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using PoL.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LanaVR
{
    /// <summary>
    /// A wooden platform and other props around the player, built from the village level in the player's own
    /// copy of the game, so the mod ships none of this art.
    ///
    /// The level is loaded once at the main menu, and switched off in the frame it arrives, before its scripts
    /// have started. Only copies of the wanted props are kept. Each is then drawn for each eye into a transparent
    /// image, and a mirrored one for the water, and the native sky pass blends these into the layer behind the
    /// game screen. Being behind the screen, the scenery can never block the game.
    /// </summary>
    internal static class Scenery
    {
        const string SourceScene = "Chapter 01 Scene 10";
        const int Layer = 30;
        const float MaxImageWidth = 2560f;
        // Far from everything in the game's levels, so no game camera can see it.
        static readonly Vector3 Origin = new Vector3(20000, 20000, 0);

        enum State { Waiting, Loading, Ready, Failed }
        static State _state = State.Waiting;
        static float _loadStart, _loadSeconds, _menuSince = -1, _worstFrame;
        static int _built, _missing;
        static bool _cleanupPending;

        static GameObject _root, _waterRoot, _occluder;
        static Camera _cam;
        static readonly RenderTexture[] _msaa = new RenderTexture[2], _image = new RenderTexture[2], _mirrorDraw = new RenderTexture[2], _mirror = new RenderTexture[2];
        static int _imageW, _imageH;
        static bool _drawing;
        static readonly Dictionary<IntPtr, Material> _materials = new Dictionary<IntPtr, Material>();

        public static string Status => $"state={_state} props={_built} missing={_missing} loadSeconds={_loadSeconds:F1} worstFrameMs={_worstFrame * 1000f:F0} drawing={_drawing} image={_imageW}x{_imageH}";

        enum Anchor { Floor, Water }

        struct Prop
        {
            public string Mesh, Material;
            public float X, Y, Z, Yaw, Scale;
            public bool ByTop;
            public Anchor On;
            public Prop(string mesh, string material, float x, float y, float z, float yaw = 0, bool byTop = false, Anchor on = Anchor.Floor, float scale = 1)
            { Mesh = mesh; Material = material; X = x; Y = y; Z = z; Yaw = yaw; ByTop = byTop; On = on; Scale = scale; }
        }

        const string Planks = "House_BG_006_01Top_Tex", Yard = "House_BG_003_Tex", Walkway = "House_BG_005_Tex";

        // Metres, with +x to the right of the screen's direction, +z towards it, and the player's feet at 0. A prop
        // is placed by the bottom of its bounds, or by the top for anything standing in the water.
        static readonly Prop[] Layout =
        {
            // The platform underfoot, and a walkway behind the player leading to a second platform.
            new Prop("Std_Pg_Floor1x1_010", Planks, 0, 0, 0, byTop: true),
            new Prop("Std_Pg_Bridge2__1_", Walkway, -0.4f, -0.04f, -5.1f, byTop: true),
            new Prop("Std_Pg_Floor1x1_011_1", Planks, -0.4f, -0.08f, -10.4f, 30, byTop: true),

            // Posts at the platform's corners. Each runs down into the water.
            new Prop("Std_Pg_PoleSmall_106_1", Planks, -1.95f, 1.05f, 1.9f, byTop: true),
            new Prop("Std_Pg_PoleSmall_108_1", Planks, 1.95f, 0.8f, 1.9f, byTop: true),
            new Prop("Std_Pg_PoleSmall_110_1", Planks, -1.95f, 1.3f, -1.9f, byTop: true),
            new Prop("Std_Pg_PoleSmall_113_1", Planks, 1.95f, 1.1f, -1.9f, byTop: true),

            // On the platform, away from the middle and behind the player's line of sight.
            new Prop("Std_Pg_Box_003_lowpoly", "Std_Pg_Boxes_tex_01_Opt", 1.3f, 0, -1.25f, 25, scale: 0.7f),
            new Prop("Std_Pg_Box_004_lowpoly", "Std_Pg_Boxes_tex_03_Opt", 0.55f, 0, -1.55f, -15),
            new Prop("Tailo_prop_barrel_001_lowpoly", "Tailo_prop_barrel_001_Opt_Dark", -1.25f, 0, -1.3f, scale: 0.7f),
            new Prop("Std_Pg_PropFence_003_1", "House_BG_002_01_Tex", -1.85f, 0, 0.1f, 55),

            // A boat tied up nearby.
            new Prop("Tailo_prop_boat_02", "Tailo_prop_Boats dark", 4.2f, -0.35f, 3.2f, 15, on: Anchor.Water),
            new Prop("Std_Pg_PoleSmall_114_1", Planks, 3.1f, 1.4f, 5.6f, byTop: true, on: Anchor.Water),

            // Poles in the water, in small groups, to give the distance some scale.
            new Prop("Std_Pg_PoleSmall_001__14_", Yard, -5, 1.9f, 7, byTop: true, on: Anchor.Water),
            new Prop("Std_Pg_PoleSmall_001__15_", Yard, -6.1f, 1.2f, 8.4f, 40, byTop: true, on: Anchor.Water),
            new Prop("Std_Pg_PoleSmall_001__16_", Yard, -5.5f, 2.6f, 9.8f, 90, byTop: true, on: Anchor.Water),
            new Prop("Std_Pg_PoleSmall_107_1", Planks, 6.6f, 2.2f, 10, byTop: true, on: Anchor.Water),
            new Prop("Std_Pg_PoleSmall_109", Planks, 8.1f, 1, 8.2f, byTop: true, on: Anchor.Water),
            new Prop("Std_Pg_PoleSmall_106", Yard, -12, 3, 18, byTop: true, on: Anchor.Water),
            new Prop("Std_Pg_PoleSmall_108", Yard, -13.6f, 2, 17, byTop: true, on: Anchor.Water),
            new Prop("Std_Pg_PoleSmall_113", Yard, 13, 3.2f, 20, byTop: true, on: Anchor.Water),
            new Prop("Std_Pg_PoleSmall_110", Yard, 14.3f, 1.6f, 21.5f, byTop: true, on: Anchor.Water),

            // A small landing out in the water, standing on its own poles.
            new Prop("Std_Pg_Floor1x1_012", "House_BG_004_02_Tex", -11, 1.1f, 11, 25, byTop: true, on: Anchor.Water),
            new Prop("Std_Pg_PoleSmall_117", Planks, -12.7f, 2.2f, 10, byTop: true, on: Anchor.Water),
            new Prop("Std_Pg_PoleSmall_118", Planks, -9.3f, 1.9f, 10.2f, byTop: true, on: Anchor.Water),
            new Prop("Std_Pg_PoleSmall_115_1", Planks, -11.6f, 2.5f, 12.8f, byTop: true, on: Anchor.Water),
            new Prop("Std_Pg_Box_001_lowpoly", "Std_Pg_Boxes_tex_03_Opt", -10.6f, 1.1f, 11, 40, on: Anchor.Water, scale: 0.7f),
        };

        /// <summary>Called twice a second during a VR session. The level loads only from the main menu, where no
        /// other level is loading, and after a moment there, so as not to compete with the game's own start-up.</summary>
        public static void Poll()
        {
            if (_cleanupPending)
            {
                _cleanupPending = false;
                Resources.UnloadUnusedAssets();
            }
            if (_state != State.Waiting) return;
            bool atMenu = false;
            foreach (var v in Resources.FindObjectsOfTypeAll<MainMenuView>())
                if (v.gameObject.scene.IsValid() && v.canvas != null && v.canvas.enabled) atMenu = true;
            if (!atMenu) { _menuSince = -1; return; }
            if (_menuSince < 0) { _menuSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _menuSince < 1f) return;
            BeginLoad();
        }

        static void BeginLoad()
        {
            _state = State.Loading;
            _loadStart = Time.realtimeSinceStartup;
            try
            {
                if (!Application.CanStreamedLevelBeLoaded(SourceScene)) throw new InvalidOperationException("the level is not in this copy of the game");
                SceneManager.sceneLoaded = Il2CppSystem.Delegate.Combine(SceneManager.sceneLoaded, DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<Scene, LoadSceneMode>>(new Action<Scene, LoadSceneMode>(OnSceneLoaded))).Cast<UnityEngine.Events.UnityAction<Scene, LoadSceneMode>>();
                if (SceneManager.LoadSceneAsync(SourceScene, LoadSceneMode.Additive) == null) throw new InvalidOperationException("the level would not load");
                Plugin.L.LogInfo("Loading the village for the 3D scenery");
            }
            catch (Exception e)
            {
                _state = State.Failed;
                Plugin.L.LogWarning("No 3D scenery: " + e.Message);
            }
        }

        // The game's scripts in that level have only had Awake and OnEnable when this runs, as it comes before
        // the first Start and Update. Switching the level off here keeps it from doing anything else.
        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_state != State.Loading || scene.name != SourceScene) return;
            _state = State.Failed;
            try
            {
                var roots = scene.GetRootGameObjects();
                foreach (var root in roots) root.SetActive(false);
                Build(roots);
                _state = _built > 0 ? State.Ready : State.Failed;
                _loadSeconds = Time.realtimeSinceStartup - _loadStart;
                Plugin.L.LogInfo($"3D scenery ready: {_built} props, {_missing} not found, in {_loadSeconds:F1} s");
            }
            catch (Exception e) { Plugin.L.LogError("Could not build the 3D scenery: " + e); }
            SceneManager.UnloadSceneAsync(scene);
            _cleanupPending = true;
        }

        static void Build(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameObject> roots)
        {
            var sources = new Dictionary<string, MeshRenderer>();
            foreach (var root in roots)
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r.isPartOfStaticBatch || r.sharedMaterial == null) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var key = mf.sharedMesh.name + "|" + r.sharedMaterial.name;
                    if (!sources.ContainsKey(key)) sources[key] = r;
                }

            _root = new GameObject("LanaVR Scenery");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.transform.position = Origin;
            _waterRoot = new GameObject("Water level");
            _waterRoot.transform.SetParent(_root.transform, false);

            var unlit = Shader.Find("Unlit/Texture");
            foreach (var p in Layout)
            {
                if (!sources.TryGetValue(p.Mesh + "|" + p.Material, out var source))
                {
                    _missing++;
                    Plugin.L.LogWarning($"Scenery prop not found: {p.Mesh} with {p.Material}");
                    continue;
                }
                var go = new GameObject(p.Mesh) { layer = Layer };
                go.transform.SetParent(p.On == Anchor.Water ? _waterRoot.transform : _root.transform, false);
                go.transform.rotation = Quaternion.Euler(0, p.Yaw, 0) * source.transform.rotation;
                go.transform.localScale = source.transform.lossyScale * p.Scale;
                go.AddComponent<MeshFilter>().sharedMesh = source.GetComponent<MeshFilter>().sharedMesh;
                var nr = go.AddComponent<MeshRenderer>();
                nr.sharedMaterials = Materials(source.sharedMaterials, unlit);
                nr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                nr.receiveShadows = false;
                // Placed by its real bounds, since each prop's pivot is somewhere different.
                var b = nr.bounds;
                var parentY = p.On == Anchor.Water ? _waterRoot.transform.position.y : _root.transform.position.y;
                var want = new Vector3(Origin.x + p.X, parentY + p.Y, Origin.z + p.Z);
                go.transform.position += want - new Vector3(b.center.x, p.ByTop ? b.max.y : b.min.y, b.center.z);
                _built++;
            }
            BuildOccluder();
        }

        /// <summary>The art has its lighting painted in, for the game's side-on view, so an unlit shader shows it
        /// as it was painted, and doesn't depend on the lights of whichever level is running.</summary>
        static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material> Materials(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material> originals, Shader unlit)
        {
            var result = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material>(originals.Length);
            for (int i = 0; i < originals.Length; i++)
            {
                var original = originals[i];
                if (unlit == null || original == null) { result[i] = original; continue; }
                if (!_materials.TryGetValue(original.Pointer, out var own))
                {
                    own = new Material(unlit) { mainTexture = original.mainTexture };
                    own.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    _materials[original.Pointer] = own;
                }
                result[i] = own;
            }
            return result;
        }

        /// <summary>A large plane at the water level that writes depth and leaves the image clear, so it hides
        /// everything below the surface. The sky layer's real water shows through the clear pixels.</summary>
        static void BuildOccluder()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) { Plugin.L.LogWarning("Scenery won't be cut off at the water: its shader is missing"); return; }
            var material = new Material(shader) { renderQueue = 1000, color = new Color(0, 0, 0, 0), hideFlags = HideFlags.DontUnloadUnusedAsset };
            material.SetInt("_SrcBlend", 1);
            material.SetInt("_DstBlend", 0);
            material.SetInt("_Cull", 0);
            material.SetInt("_ZWrite", 1);
            material.SetInt("_ZTest", 4);
            const float Size = 400f;
            var mesh = new Mesh { name = "Scenery water level", hideFlags = HideFlags.DontUnloadUnusedAsset };
            mesh.vertices = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(new[] { new Vector3(-Size, 0, -Size), new Vector3(-Size, 0, Size), new Vector3(Size, 0, Size), new Vector3(Size, 0, -Size) });
            mesh.triangles = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(new[] { 0, 1, 2, 0, 2, 3 });
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(Size * 2, 1, Size * 2));
            _occluder = new GameObject("Below the water") { layer = Layer };
            _occluder.transform.SetParent(_waterRoot.transform, false);
            _occluder.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = _occluder.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        static void MakeImages(int recommendedW, int recommendedH)
        {
            float scale = Mathf.Min(1f, MaxImageWidth / Mathf.Max(1, recommendedW));
            int w = Mathf.RoundToInt(recommendedW * scale) & ~1, h = Mathf.RoundToInt(recommendedH * scale) & ~1;
            if (w == _imageW && h == _imageH && _image[0] != null) return;
            _imageW = w; _imageH = h;
            for (int i = 0; i < 2; i++)
            {
                foreach (var old in new[] { _msaa[i], _image[i], _mirrorDraw[i], _mirror[i] }) if (old != null) { old.Release(); UnityEngine.Object.Destroy(old); }
                _msaa[i] = Target(w, h, 24, 4, "Scenery draw " + i);
                _image[i] = Target(w, h, 0, 1, "Scenery " + i);
                _mirrorDraw[i] = Target(w / 2, h / 2, 16, 1, "Scenery mirror draw " + i);
                _mirror[i] = Target(w / 2, h / 2, 0, 1, "Scenery mirror " + i);
            }
            Native.LVR_SetScenery(_image[0].GetNativeTexturePtr(), _image[1].GetNativeTexturePtr(), _mirror[0].GetNativeTexturePtr(), _mirror[1].GetNativeTexturePtr());
        }

        static RenderTexture Target(int w, int h, int depth, int samples, string name)
        {
            var rt = new RenderTexture(w, h, depth, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "LanaVR " + name, antiAliasing = samples };
            rt.hideFlags = HideFlags.HideAndDontSave;
            rt.Create();
            return rt;
        }

        static void MakeCamera()
        {
            if (_cam != null) return;
            var go = new GameObject("LanaVR Scenery Camera") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.cullingMask = 1 << Layer;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0, 0, 0, 0);
            _cam.allowHDR = false;
            _cam.useOcclusionCulling = false;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 500f;
        }

        static Quaternion Conj(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);
        static Vector3 Rot(Quaternion q, Vector3 v)
        {
            var u = new Vector3(q.x, q.y, q.z);
            return v + 2f * Vector3.Cross(u, Vector3.Cross(u, v) + q.w * v);
        }

        /// <summary>Draws both eyes. Called after the eye pass, inside the mod's render step.</summary>
        public static void Render(XrFrame f, Vector3 centerHead, float centerYaw, int recommendedW, int recommendedH)
        {
            if (_state == State.Loading || (_state == State.Ready && Time.realtimeSinceStartup - _loadStart - _loadSeconds < 3f))
                _worstFrame = Mathf.Max(_worstFrame, Time.unscaledDeltaTime);
            bool show = _state == State.Ready && Cfg.Scenery.Value && f.viewsValid != 0 && Cfg.SkyBrightness.Value > 0f;
            if (show != _drawing)
            {
                _drawing = show;
                Native.LVR_SetSceneryOn(0);
            }
            if (!show) return;
            MakeImages(recommendedW, recommendedH);
            MakeCamera();

            float floor = Native.LVR_GetFloor();
            Native.LVR_GetWater(out float water, out _);
            float waterAbove = water - floor;
            _waterRoot.transform.localPosition = new Vector3(0, waterAbove, 0);
            var yaw = new Quaternion(0, Mathf.Sin(centerYaw / 2), 0, Mathf.Cos(centerYaw / 2));
            var unyaw = Conj(yaw);
            var anchor = new Vector3(centerHead.x, floor, centerHead.z);

            bool fog = RenderSettings.fog;
            RenderSettings.fog = false;
            for (int i = 0; i < 2; i++)
            {
                var eye = i == 0 ? f.left : f.right;
                float n = _cam.nearClipPlane;
                var projection = i == 0
                    ? XrRig.Frustum(n * f.leftL, n * f.leftR, n * f.leftD, n * f.leftU, n, _cam.farClipPlane)
                    : XrRig.Frustum(n * f.rightL, n * f.rightR, n * f.rightD, n * f.rightU, n, _cam.farClipPlane);
                _cam.projectionMatrix = projection;

                // Tracking space is right handed, and looks down -z. Unity looks down +z.
                var p = Rot(unyaw, new Vector3(eye.px, eye.py, eye.pz) - anchor);
                var q = unyaw * new Quaternion(eye.qx, eye.qy, eye.qz, eye.qw);
                _cam.transform.position = Origin + new Vector3(p.x, p.y, -p.z);
                _cam.transform.rotation = new Quaternion(-q.x, -q.y, q.z, q.w);
                _cam.ResetWorldToCameraMatrix();

                if (_occluder != null) _occluder.SetActive(true);
                _cam.targetTexture = _msaa[i];
                _cam.Render();
                Graphics.Blit(_msaa[i], _image[i], new Vector2(1, -1), new Vector2(0, 1));

                // The same view of the world mirrored in the water, for its reflection.
                if (_occluder != null) _occluder.SetActive(false);
                float h = Origin.y + waterAbove;
                var mirror = Matrix4x4.identity;
                mirror.m11 = -1f;
                mirror.m13 = 2f * h;
                var view = _cam.worldToCameraMatrix;
                _cam.worldToCameraMatrix = view * mirror;
                // Anything below the water would mirror to above it, so a slanted near plane cuts it off.
                var planePoint = view.MultiplyPoint3x4(new Vector3(0, h, 0));
                var planeNormal = view.MultiplyVector(Vector3.down).normalized;
                _cam.projectionMatrix = _cam.CalculateObliqueMatrix(new Vector4(planeNormal.x, planeNormal.y, planeNormal.z, -Vector3.Dot(planeNormal, planePoint)));
                _cam.targetTexture = _mirrorDraw[i];
                GL.invertCulling = true;
                _cam.Render();
                GL.invertCulling = false;
                Graphics.Blit(_mirrorDraw[i], _mirror[i], new Vector2(1, -1), new Vector2(0, 1));
                _cam.ResetWorldToCameraMatrix();
            }
            _cam.targetTexture = null;
            RenderSettings.fog = fog;
            Native.LVR_SetSceneryOn(1);
        }
    }
}
