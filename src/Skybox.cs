using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;

namespace LanaVR
{
    /// <summary>
    /// Chooses the backdrop image to match the game's current background. skybox\skyboxes.txt maps the
    /// game's backdrop scene names to image files. Each can be a 2:1 panorama or a 4:3 horizontal-cross
    /// cubemap of any size, and the native loader tells them apart by shape.
    /// </summary>
    internal static class Skybox
    {
        /// <summary>The largest face kept in video memory. Bigger images are scaled down to this, which is
        /// already sharper than current headsets can show and costs about 512 MB with its mipmaps.</summary>
        // Size of each cube face at Low, Medium and High. The packed skies are big enough for High, and are
        // reduced as they load for the others.
        static readonly int[] QualityFaces = { 512, 1024, 2048 };
        public static int QualityFace => QualityFaces[Mathf.Clamp(Mathf.RoundToInt(Cfg.SkyQuality.Value), 0, QualityFaces.Length - 1)];
        static int _loadedFace;
        const float FadeSeconds = 3f;
        const string MenuName = "Main Menu";

        static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static bool _mapLoaded;
        static string _shownFile;

        // Backdrop name, then the game's post-process volume, then that volume's colour effect at full weight.
        static readonly Dictionary<string, Dictionary<string, Color>> Tints = new Dictionary<string, Dictionary<string, Color>>(StringComparer.OrdinalIgnoreCase);
        static readonly List<(PostProcessVolume volume, Color color)> ActiveTints = new List<(PostProcessVolume, Color)>();
        static readonly Dictionary<string, List<(float x, Color color)>> Shades = new Dictionary<string, List<(float x, Color color)>>(StringComparer.OrdinalIgnoreCase);
        static Color _lastShade = Color.white;
        public static Color TestShade = Color.white;  // For tests.
        static Color _tint = Color.white;
        public static Color CurrentTint => _tint;
        public static int TintVolumes => ActiveTints.Count;

        public static string CurrentBackdrop { get; private set; } = "";
        public static string CurrentFile => _shownFile ?? "";
        public static int MappedCount => Map.Count;

        public static string Folder => Path.Combine(Path.GetDirectoryName(typeof(Skybox).Assembly.Location), "skybox");

        static void LoadMap()
        {
            _mapLoaded = true;
            Map.Clear();
            Tints.Clear();
            Shades.Clear();
            var path = Path.Combine(Folder, "skyboxes.txt");
            if (!File.Exists(path))
            {
                Plugin.L.LogWarning($"{path} is missing, so the backdrop stays black");
                return;
            }
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("Tint:", StringComparison.OrdinalIgnoreCase))
                {
                    ParseTint(line);
                    continue;
                }
                if (line.StartsWith("Shade:", StringComparison.OrdinalIgnoreCase))
                {
                    ParseShade(line);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || eq == line.Length - 1)
                {
                    Plugin.L.LogWarning($"Ignoring this line in skyboxes.txt: {line}");
                    continue;
                }
                Map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            Plugin.L.LogInfo($"skyboxes.txt lists {Map.Count} backgrounds");
        }

        // Tint: Chapter 01 Backdrop / PP Sunset = 1.00 0.56 0.52
        static void ParseTint(string line)
        {
            var body = line.Substring(5);
            int eq = body.IndexOf('='), slash = body.IndexOf('/');
            var v = eq > 0 ? body.Substring(eq + 1).Split((char[])null, StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
            if (slash <= 0 || eq < slash || v.Length != 3
                || !float.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
                || !float.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var g)
                || !float.TryParse(v[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            {
                Plugin.L.LogWarning($"Ignoring this line in skyboxes.txt: {line}");
                return;
            }
            var backdrop = body.Substring(0, slash).Trim();
            if (!Tints.TryGetValue(backdrop, out var volumes)) Tints[backdrop] = volumes = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            volumes[body.Substring(slash + 1, eq - slash - 1).Trim()] = new Color(r, g, b);
        }

        // Shade: Chapter 04 Backdrop = 520 1 1 1 | 573 0.85 0.85 0.9 | 700 1 1 1
        // Places where the game's own painted sky gets darker or changes colour along a level, found by scanning
        // the game. Each stop is a distance along the level and a colour, with smooth changes between stops.
        static void ParseShade(string line)
        {
            var body = line.Substring(6);
            int eq = body.IndexOf('=');
            if (eq <= 0) { Plugin.L.LogWarning($"Ignoring this line in skyboxes.txt: {line}"); return; }
            var stops = new List<(float x, Color color)>();
            foreach (var stop in body.Substring(eq + 1).Split('|'))
            {
                var v = stop.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (v.Length != 4
                    || !float.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    || !float.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
                    || !float.TryParse(v[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var g)
                    || !float.TryParse(v[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                {
                    Plugin.L.LogWarning($"Ignoring this line in skyboxes.txt: {line}");
                    return;
                }
                stops.Add((x, new Color(r, g, b)));
            }
            stops.Sort((p, q) => p.x.CompareTo(q.x));
            Shades[body.Substring(0, eq).Trim()] = stops;
        }

        static Color ShadeHere()
        {
            if (!Shades.TryGetValue(CurrentBackdrop, out var stops) || stops.Count == 0) return Color.white;
            var lana = PoL.Characters.Lana.LanaMachine.instance;
            if (lana == null) return _lastShade;
            float x = lana.transform.position.x;
            if (x <= stops[0].x) return _lastShade = stops[0].color;
            for (int i = 1; i < stops.Count; i++)
                if (x <= stops[i].x) return _lastShade = Color.Lerp(stops[i - 1].color, stops[i].color, Mathf.InverseLerp(stops[i - 1].x, stops[i].x, x));
            return _lastShade = stops[stops.Count - 1].color;
        }

        /// <summary>Finds the game's volumes that tint this backdrop, lowest priority first, as the game blends them.</summary>
        static void RefreshTintVolumes()
        {
            ActiveTints.Clear();
            Tints.TryGetValue(CurrentBackdrop, out var wanted);
            // "*" lines apply to every backdrop, such as the game's cave effect.
            Tints.TryGetValue("*", out var everywhere);
            if (wanted == null && everywhere == null) return;
            foreach (var v in Resources.FindObjectsOfTypeAll<PostProcessVolume>())
            {
                if (!v.gameObject.scene.IsValid()) continue;
                if (wanted != null && wanted.TryGetValue(v.name, out var color)) ActiveTints.Add((v, color));
                else if (everywhere != null && everywhere.TryGetValue(v.name, out color)) ActiveTints.Add((v, color));
            }
            ActiveTints.Sort((a, b) => a.volume.priority.CompareTo(b.volume.priority));
        }

        /// <summary>Follows the game's colour grading every frame, so the sky darkens along with the game. The
        /// game's fades take a minute or more, and the easing here only softens sudden changes, such as a new
        /// backdrop or a checkpoint reload.</summary>
        public static void UpdateTint(float deltaTime)
        {
            var target = Color.white;
            foreach (var (volume, color) in ActiveTints)
                if (volume != null && volume.isActiveAndEnabled) target = Color.Lerp(target, color, Mathf.Clamp01(volume.weight));
            target *= ShadeHere() * TestShade;
            if (!(_blending && BlendTint(target))) _tint = Color.Lerp(_tint, target, 1f - Mathf.Exp(-deltaTime / 0.5f));
            Native.LVR_SetSkyTint(_tint.r, _tint.g, _tint.b);
        }

        // When the backdrop changes to a new sky, the old sky stays on screen while the new one loads, then the two
        // crossfade. The old backdrop's tint is kept until that crossfade starts, then blended to the new one in
        // step with it, so a dimmed sky never brightens before changing, as Chapter 1's did on its way to night.
        static bool _blending;
        static float _blendStart, _blendAsked;
        static Color _blendFrom;

        static void StartBlend()
        {
            _blending = true;
            _blendStart = -1;
            _blendAsked = Time.unscaledTime;
            _blendFrom = _tint;
        }

        /// <summary>Sets the tint while a sky change is under way. False once the change is over.</summary>
        static bool BlendTint(Color target)
        {
            float now = Time.unscaledTime;
            if (_blendStart < 0)
            {
                var status = Native.SkyboxStatus;
                if (status.StartsWith("ready", StringComparison.Ordinal)) _blendStart = now;
                else if (status.StartsWith("decoding", StringComparison.Ordinal) && now - _blendAsked < 15f) return true;  // Holds the old tint.
                else { _blending = false; return false; }
            }
            float t = (now - _blendStart) / FadeSeconds;
            if (t >= 1f) { _blending = false; return false; }
            // The sky's crossfade eases by s. The new tint follows s squared, so the old sky, (1 - s) of the picture,
            // only ever gets darker on its way out, whichever way the tint is heading.
            float s = t * t * (3f - 2f * t);
            _tint = Color.Lerp(_blendFrom, target, s * s);
            return true;
        }

        /// <summary>A new OpenXR session starts with no sky, so the current one must be loaded again.</summary>
        public static void OnSessionCreated()
        {
            if (!_mapLoaded) LoadMap();
            _shownFile = null;
            CurrentBackdrop = "";
            _tint = Color.white;
            Poll();
        }

        /// <summary>The game loads one backdrop scene at a time. Between levels there may be none, and then
        /// the current sky stays until the next one appears.</summary>
        static string DetectBackdrop()
        {
            bool menu = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                var name = scene.name;
                if (name.StartsWith("Chapter ", StringComparison.Ordinal) && name.Contains(" Backdrop") && name != CurrentBackdrop) return name;
                if (name == CurrentBackdrop) return name;
                if (name == "UI Landing") menu = true;
            }
            return menu ? MenuName : CurrentBackdrop;
        }

        /// <summary>A full poll also looks for the game's colour volumes, which means searching every object, so
        /// it is only done when levels load or unload, and every few seconds in case one appears otherwise.</summary>
        public static void Poll(bool full = true)
        {
            var backdrop = DetectBackdrop();
            if (backdrop.Length == 0 && !Map.ContainsKey(MenuName)) return;
            if (backdrop.Length == 0) backdrop = MenuName;  // The first frames of a session, before any scene.
            bool changed = backdrop != CurrentBackdrop;
            if (changed)
            {
                CurrentBackdrop = backdrop;
                Apply(backdrop);
            }
            if (full || changed) RefreshTintVolumes();  // Scenes bring their own volumes, so this can change without a new backdrop.
            if (_shownFile != null && _loadedFace != QualityFace)
            {
                _loadedFace = QualityFace;
                Plugin.L.LogInfo($"Sky quality is now {_loadedFace} per face");
                Native.LVR_LoadSkybox(Path.Combine(Folder, _shownFile), _loadedFace, FadeSeconds);
            }
        }

        static void Apply(string backdrop)
        {
            if (!Map.TryGetValue(backdrop, out var file))
            {
                Plugin.L.LogInfo($"Backdrop is now '{backdrop}', not listed in skyboxes.txt, keeping {(_shownFile ?? "no skybox")}");
                return;
            }
            if (string.Equals(file, _shownFile, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.L.LogInfo($"Backdrop is now '{backdrop}', already showing {file}");
                return;
            }
            var path = Path.Combine(Folder, file);
            if (!File.Exists(path))
            {
                Plugin.L.LogWarning($"Backdrop is now '{backdrop}', but {file} isn't in the skybox folder, keeping {(_shownFile ?? "no skybox")}");
                return;
            }
            Plugin.L.LogInfo($"Backdrop is now '{backdrop}', showing {file}");
            _loadedFace = QualityFace;
            if (_shownFile != null) StartBlend();
            Native.LVR_LoadSkybox(path, _loadedFace, _shownFile == null ? 0f : FadeSeconds);
            _shownFile = file;
        }
    }
}
