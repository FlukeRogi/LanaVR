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
        // The scenery sits at the world's origin, because Unity's coordinates are too coarse far from it: at 20 km
        // they only move in 2 mm steps, which shows as a fast jitter on anything near the player. It stays switched
        // off except while its own camera draws, so the game's cameras never see it.

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
            public float X, Y, Z, Yaw, Scale, Tilt;
            public bool ByTop, AlongZ;
            public Anchor On;
            // A tilted prop is turned nose-down along its length by Tilt degrees, and placed by the centre of its bounds.
            public Prop(string mesh, string material, float x, float y, float z, float yaw = 0, bool byTop = false, Anchor on = Anchor.Floor, float scale = 1, bool alongZ = false, float tilt = 0)
            { Mesh = mesh; Material = material; X = x; Y = y; Z = z; Yaw = yaw; ByTop = byTop; On = on; Scale = scale; AlongZ = alongZ; Tilt = tilt; }
        }

        const float DeckRaise = 0.085f;
        const string BoatMesh = "Tailo_prop_boat_02";
        const float BoatX = 5.2f, BoatZ = 3.2f, BoatYaw = 15f, BoatTilt = 40f, BoatCentre = -0.3f;
        const string Planks = "House_BG_006_01Top_Tex", Yard = "House_BG_003_Tex", Walkway = "House_BG_005_Tex";

        // Metres, with +x to the right of the screen's direction, +z towards it, and the player's feet at 0. A prop
        // is placed by the bottom of its bounds, or by the top for anything standing in the water.
        static readonly Prop[] Layout =
        {
            // The platform underfoot, and a walkway behind the player leading to a second platform.
            // The deck's highest point is a raised beam. Its planks are about 8.5 cm lower, and are what the player stands on.
            new Prop("Std_Pg_Floor1x1_010", Planks, 0, DeckRaise, 0, byTop: true),
            new Prop("Std_Pg_Bridge2__1_", Walkway, -0.4f, DeckRaise - 0.04f, -5.1f, byTop: true),
            new Prop("Std_Pg_Floor1x1_011_1", Planks, -0.4f, DeckRaise - 0.08f, -10.4f, 30, byTop: true),

            // Posts at the platform's corners. Each runs down into the water.
            new Prop("Std_Pg_PoleSmall_106_1", Planks, -1.62f, 1.05f, 1.78f, byTop: true),
            new Prop("Std_Pg_PoleSmall_108_1", Planks, 1.62f, 0.8f, 1.78f, byTop: true),
            new Prop("Std_Pg_PoleSmall_110_1", Planks, -1.62f, 1.3f, -1.78f, byTop: true),
            new Prop("Std_Pg_PoleSmall_113_1", Planks, 1.62f, 1.1f, -1.78f, byTop: true),

            // On the platform, behind the player's line of sight. The planks reach about 1.55 m to each side and
            // 1.7 m to the front and back, so these sit within that. The deck's bounds are larger, because of its beams.
            // The planks aren't level, so each height here is the measured height of the planks under that prop.
            new Prop("Std_Pg_Box_003_lowpoly", "Std_Pg_Boxes_tex_01_Opt", 0.85f, 0f, -1, 25, scale: 0.45f),
            new Prop("Std_Pg_Box_004_lowpoly", "Std_Pg_Boxes_tex_03_Opt", -0.1f, -0.015f, -1.4f, -15, scale: 0.8f),
            new Prop("Tailo_prop_barrel_001_lowpoly", "Tailo_prop_barrel_001_Opt_Dark", -0.75f, -0.008f, -1.35f, scale: 0.7f),
            // Along the deck's left side, resting on the planks. This mesh is modelled at 35 degrees within its own
            // 2.0 by 1.4 m box, so lining up the box leaves that much still to turn.
            new Prop("Std_Pg_PropFence_003_1", "House_BG_002_01_Tex", -1.3f, 0.03f, 0.25f, 35, alongZ: true),

            // A half-sunk boat nearby. The game's boat has two faces overlapping beside one seat, which flickers when
            // seen from above, as the game never does, so that end is tipped under the water.
            new Prop(BoatMesh, "Tailo_prop_Boats dark", BoatX, BoatCentre, BoatZ, BoatYaw, on: Anchor.Water, tilt: BoatTilt),
            new Prop("Std_Pg_PoleSmall_114_1", Planks, 4.1f, 1.4f, 5.6f, byTop: true, on: Anchor.Water),

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
            new Prop("Std_Pg_Box_001_lowpoly", "Std_Pg_Boxes_tex_03_Opt", -10.6f, 1.02f, 11, 40, on: Anchor.Water, scale: 0.7f),
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
                var mesh = source.GetComponent<MeshFilter>().sharedMesh;
                if (p.AlongZ) go.transform.rotation = Quaternion.Euler(0, YawAlongZ(mesh, go.transform) + p.Yaw, 0) * go.transform.rotation;
                if (p.Mesh == BoatMesh) { _boat = go; _boatRotation = go.transform.rotation; }
                Place(go, mesh, p);
                _built++;
            }
            BuildOccluder();
            _root.SetActive(false);
        }

        /// <summary>Puts a prop where the layout says, by its real extent, since each prop's pivot is somewhere
        /// different. This is worked out from the mesh, because a renderer's own bounds can lag behind a change
        /// made in the same frame.</summary>
        static void Place(GameObject go, Mesh mesh, Prop p)
        {
            if (p.Tilt != 0) go.transform.rotation = Quaternion.AngleAxis(p.Tilt, TiltAxis(mesh, go.transform)) * go.transform.rotation;
            var b = WorldBounds(mesh, go.transform);
            float y = p.Tilt != 0 ? b.center.y : p.ByTop ? b.max.y : b.min.y;
            go.transform.position += new Vector3(p.X, p.Y, p.Z) - new Vector3(b.center.x, y, b.center.z);
        }

        static GameObject _boat;
        static Quaternion _boatRotation;

        /// <summary>For tests: tips the boat by another angle, and sets the height of its centre above the water.</summary>
        public static string TiltBoat(float tilt, float centre, float yaw)
        {
            if (_boat == null) return "no boat";
            bool wasOn = _root.activeSelf;
            var scale = _root.transform.localScale;
            var water = _waterRoot.transform.localPosition;
            _root.transform.localScale = Vector3.one;
            _waterRoot.transform.localPosition = Vector3.zero;
            _boat.transform.localPosition = Vector3.zero;
            _boat.transform.rotation = Quaternion.Euler(0, yaw - BoatYaw, 0) * _boatRotation;
            Place(_boat, _boat.GetComponent<MeshFilter>().sharedMesh, new Prop(BoatMesh, "", BoatX, centre, BoatZ, yaw, tilt: tilt));
            _root.transform.localScale = scale;
            _waterRoot.transform.localPosition = water;
            _root.SetActive(wasOn);
            return $"boat tilt={tilt} centre={centre} yaw={yaw}";
        }

        /// <summary>The horizontal axis across a prop's length, to tip it nose-down along its length.</summary>
        static Vector3 TiltAxis(Mesh mesh, Transform t)
        {
            float yaw = YawAlongZ(mesh, t);
            var length = Quaternion.Euler(0, -yaw, 0) * Vector3.forward;
            return Vector3.Cross(Vector3.up, length).normalized;
        }

        /// <summary>For tests: the height of the deck's surface over a square of it, measured by looking straight
        /// down and lowering the far clip plane until each point appears. The meshes can't be read directly.</summary>
        public static string ProbeDeck(float x, float z, float halfWidth)
        {
            if (_state != State.Ready) return "not ready";
            MakeCamera();
            const int N = 24;
            var rt = RenderTexture.GetTemporary(N, N, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            _root.SetActive(true);
            _root.transform.localScale = Vector3.one;
            _waterRoot.gameObject.SetActive(false);
            var off = new List<GameObject>();
            for (int i = 0; i < _root.transform.childCount; i++)
            {
                var c = _root.transform.GetChild(i).gameObject;
                if (c.name != "Std_Pg_Floor1x1_010" && c.activeSelf) { c.SetActive(false); off.Add(c); }
            }
            float nearSaved = _cam.nearClipPlane, farSaved = _cam.farClipPlane;
            _cam.ResetProjectionMatrix();
            _cam.orthographic = true; _cam.orthographicSize = halfWidth; _cam.aspect = 1;
            _cam.transform.position = new Vector3(x, 5, z);
            _cam.transform.rotation = Quaternion.Euler(90, 0, 0);
            _cam.ResetWorldToCameraMatrix();
            _cam.targetTexture = rt;
            var heights = new float[N * N];
            for (int i = 0; i < heights.Length; i++) heights[i] = float.NaN;
            var copy = new Texture2D(N, N, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            for (float y = 0.03f; y > -0.25f; y -= 0.004f)
            {
                _cam.farClipPlane = 5f - y;
                _cam.Render();
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, N, N), 0, 0);
                copy.Apply();
                for (int j = 0; j < N; j++) for (int i = 0; i < N; i++)
                {
                    var c = copy.GetPixel(i, j);
                    if (float.IsNaN(heights[j * N + i]) && c.r + c.g + c.b + c.a > 0.01f) heights[j * N + i] = y;
                }
            }
            RenderTexture.active = previous;
            UnityEngine.Object.Destroy(copy);
            RenderTexture.ReleaseTemporary(rt);
            _cam.targetTexture = null; _cam.orthographic = false; _cam.nearClipPlane = nearSaved; _cam.farClipPlane = farSaved;
            foreach (var c in off) c.SetActive(true);
            _waterRoot.gameObject.SetActive(true);
            _root.SetActive(false);
            var found = new List<float>();
            foreach (var h in heights) if (!float.IsNaN(h)) found.Add(h);
            if (found.Count == 0) return "no deck here";
            found.Sort();
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "covered={0:F2} low={1:F3} median={2:F3} p80={3:F3} high={4:F3}", (float)found.Count / heights.Length, found[0], found[found.Count / 2], found[found.Count * 4 / 5], found[found.Count - 1]);
        }

        /// <summary>For tests: draws the scenery alone from any position, at full size and with the water level at
        /// 0. A positive size is a side-on view that many metres high, with no perspective, for measuring.</summary>
        public static string SaveView(Vector3 position, float yaw, float pitch, float orthoSize, string path, bool water = false)
        {
            if (_state != State.Ready) return "not ready";
            MakeCamera();
            const int W = 1600, H = 1000;
            var rt = RenderTexture.GetTemporary(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 4);
            _root.SetActive(true);
            _root.transform.localScale = Vector3.one;
            _waterRoot.transform.localPosition = Vector3.zero;
            if (_occluder != null) _occluder.SetActive(water);
            _cam.ResetProjectionMatrix();
            _cam.orthographic = orthoSize > 0;
            _cam.orthographicSize = orthoSize / 2;
            _cam.fieldOfView = 35;
            _cam.aspect = (float)W / H;
            _cam.transform.position = position;
            _cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0);
            _cam.ResetWorldToCameraMatrix();
            _cam.backgroundColor = new Color(0.15f, 0.2f, 0.3f, 1);
            _cam.targetTexture = rt;
            _cam.Render();
            _cam.targetTexture = null;
            _cam.backgroundColor = new Color(0, 0, 0, 0);
            _cam.orthographic = false;
            _root.SetActive(false);
            var previous = RenderTexture.active;
            var resolved = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Graphics.Blit(rt, resolved);
            RenderTexture.active = resolved;
            var copy = new Texture2D(W, H, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            RenderTexture.ReleaseTemporary(resolved);
            System.IO.File.WriteAllBytes(path, UnityEngine.ImageConversion.EncodeToPNG(copy));
            UnityEngine.Object.Destroy(copy);
            return "saved";
        }

        static Bounds WorldBounds(Mesh mesh, Transform t)
        {
            var local = mesh.bounds;
            var result = new Bounds(t.TransformPoint(local.center), Vector3.zero);
            for (int corner = 0; corner < 8; corner++)
            {
                var e = local.extents;
                var offset = new Vector3((corner & 1) == 0 ? -e.x : e.x, (corner & 2) == 0 ? -e.y : e.y, (corner & 4) == 0 ? -e.z : e.z);
                result.Encapsulate(t.TransformPoint(local.center + offset));
            }
            return result;
        }

        /// <summary>The turn that lines up the longest horizontal side of a mesh's box with the deck's edge,
        /// whatever angle the prop has in the village.</summary>
        static float YawAlongZ(Mesh mesh, Transform t)
        {
            var size = mesh.bounds.size;
            var sides = new[] { t.TransformVector(new Vector3(size.x, 0, 0)), t.TransformVector(new Vector3(0, size.y, 0)), t.TransformVector(new Vector3(0, 0, size.z)) };
            // The side nearest to vertical is the prop's height, so the longest of the other two is its length.
            int up = 0;
            for (int i = 1; i < 3; i++) if (Mathf.Abs(sides[i].normalized.y) > Mathf.Abs(sides[up].normalized.y)) up = i;
            var length = Vector3.zero;
            for (int i = 0; i < 3; i++) if (i != up && sides[i].magnitude > length.magnitude) length = sides[i];
            return -Mathf.Atan2(length.x, length.z) * Mathf.Rad2Deg;
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
                _mirrorDraw[i] = Target(w / 2, h / 2, 24, 1, "Scenery mirror draw " + i);
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
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 500f;
        }

        /// <summary>For tests: saves the left eye's scenery alone, without the sky or water behind it.</summary>
        public static string SaveImage(string path)
        {
            if (_image[0] == null) return "no image";
            var previous = RenderTexture.active;
            RenderTexture.active = _image[0];
            var copy = new Texture2D(_image[0].width, _image[0].height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, copy.width, copy.height), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            System.IO.File.WriteAllBytes(path, UnityEngine.ImageConversion.EncodeToPNG(copy));
            UnityEngine.Object.Destroy(copy);
            return "saved";
        }

        const float FadeInSeconds = 9f, LongestWait = 60f;
        static float _revealSince = -1, _sessionStart;
        public static float Fade { get; private set; }

        /// <summary>A new headset session starts with the whole sky layer black.</summary>
        public static void OnSessionCreated()
        {
            _revealSince = -1;
            _sessionStart = Time.unscaledTime;
            Fade = 0;
            Native.LVR_SetReveal(0);
        }

        /// <summary>Called every frame. The sky, water and scenery stay black until the scenery is ready, then
        /// fade in together, so nothing pops in on its own. If the scenery isn't coming, because it is off, failed,
        /// or the session began during play, when the village can't load, the sky fades in straight away.</summary>
        public static void UpdateReveal()
        {
            if (_revealSince < 0)
            {
                bool inPlay = PoL.Characters.Lana.LanaMachine.instance != null;
                if (_state == State.Ready || _state == State.Failed || !Cfg.Scenery.Value || inPlay
                    || Time.unscaledTime - _sessionStart > LongestWait)
                    _revealSince = Time.unscaledTime;
                else return;
            }
            float level = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.unscaledTime - _revealSince) / FadeInSeconds));
            if (level == Fade && level >= 1f) return;
            Fade = level;
            Native.LVR_SetReveal(level);
        }

        static float _position = float.NaN;
        const float Step = 0.1f;  // Metres per tap of Page Up or Page Down.

        /// <summary>Where the player stands, in metres towards the platform's front edge.</summary>
        public static float Position
        {
            get
            {
                if (float.IsNaN(_position)) _position = Mathf.Clamp(Cfg.SceneryPosition.Value, -Cfg.MaxSceneryPosition, Cfg.MaxSceneryPosition);
                return _position;
            }
        }

        /// <summary>Each tap of Page Up steps towards the screen, and Page Down away from it. The game only uses
        /// these keys in its developer build. The new position is saved straight away.</summary>
        public static void PollKeys(UnityEngine.InputSystem.Keyboard kb)
        {
            if (kb == null || _state != State.Ready || !Cfg.Scenery.Value) return;
            float dir = (kb.pageUpKey.wasPressedThisFrame ? 1f : 0f) - (kb.pageDownKey.wasPressedThisFrame ? 1f : 0f);
            if (dir == 0) return;
            _position = Mathf.Clamp(Position + dir * Step, -Cfg.MaxSceneryPosition, Cfg.MaxSceneryPosition);
            Cfg.SceneryPosition.Value = Mathf.Round(_position * 100f) / 100f;
        }

        /// <summary>For tests, and when the setting changes elsewhere: takes the position from the setting again.</summary>
        public static void ReloadPosition() => _position = float.NaN;

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
            bool show = _state == State.Ready && Cfg.Scenery.Value && f.viewsValid != 0;
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
            // Everything scales around the player's feet. The water's level is in real metres, so anything
            // standing in the water stays on the surface at any size.
            float size = Mathf.Clamp(Cfg.ScenerySize.Value, 0.5f, 1.5f) * Cfg.SceneryUnit;
            _root.SetActive(true);
            _root.transform.localScale = new Vector3(size, size, size);
            _waterRoot.transform.localPosition = new Vector3(0, waterAbove / size, 0);
            var yaw = new Quaternion(0, Mathf.Sin(centerYaw / 2), 0, Mathf.Cos(centerYaw / 2));
            var unyaw = Conj(yaw);
            var anchor = new Vector3(centerHead.x, floor, centerHead.z);
            // Where the player stands along the platform, towards the screen. It moves with the scenery's size,
            // so the same spot on the planks stays underfoot.
            float along = Position * size;

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
                _cam.transform.position = new Vector3(p.x, p.y, -p.z + along);
                _cam.transform.rotation = new Quaternion(-q.x, -q.y, q.z, q.w);
                _cam.ResetWorldToCameraMatrix();

                if (_occluder != null) _occluder.SetActive(true);
                _cam.targetTexture = _msaa[i];
                _cam.Render();
                Graphics.Blit(_msaa[i], _image[i], new Vector2(1, -1), new Vector2(0, 1));

                // The same view of the world mirrored in the water, for its reflection.
                if (_occluder != null) _occluder.SetActive(false);
                float h = waterAbove;
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
            _root.SetActive(false);
            Native.LVR_SetSceneryOn(1);
        }
    }
}
