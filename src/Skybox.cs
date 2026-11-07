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
        public const int MaxFaceSize = 4096;
        const float FadeSeconds = 3f;
        const string MenuName = "Main Menu";

        static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static bool _mapLoaded;
        static string _shownFile;

        // Backdrop name, then the game's post-process volume, then that volume's colour effect at full weight.
        static readonly Dictionary<string, Dictionary<string, Color>> Tints = new Dictionary<string, Dictionary<string, Color>>(StringComparer.OrdinalIgnoreCase);
        static readonly List<(PostProcessVolume volume, Color color)> ActiveTints = new List<(PostProcessVolume, Color)>();
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

        /// <summary>Finds the game's volumes that tint this backdrop, lowest priority first, as the game blends them.</summary>
        static void RefreshTintVolumes()
        {
            ActiveTints.Clear();
            if (!Tints.TryGetValue(CurrentBackdrop, out var wanted)) return;
            foreach (var v in Resources.FindObjectsOfTypeAll<PostProcessVolume>())
                if (v.gameObject.scene.IsValid() && wanted.TryGetValue(v.name, out var color)) ActiveTints.Add((v, color));
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
            _tint = Color.Lerp(_tint, target, 1f - Mathf.Exp(-deltaTime / 0.5f));
            Native.LVR_SetSkyTint(_tint.r, _tint.g, _tint.b);
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

        public static void Poll()
        {
            var backdrop = DetectBackdrop();
            if (backdrop.Length == 0 && !Map.ContainsKey(MenuName)) return;
            if (backdrop.Length == 0) backdrop = MenuName;  // The first frames of a session, before any scene.
            if (backdrop != CurrentBackdrop)
            {
                CurrentBackdrop = backdrop;
                Apply(backdrop);
            }
            RefreshTintVolumes();  // Scenes bring their own volumes, so this can change without a new backdrop.
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
            Native.LVR_LoadSkybox(path, MaxFaceSize, _shownFile == null ? 0f : FadeSeconds);
            _shownFile = file;
        }
    }
}
