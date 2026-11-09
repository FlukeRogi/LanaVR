using System;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Microsoft.Win32;

namespace LanaVR
{
    /// <summary>Persistent settings, stored in BepInEx\config\com.lanavr.planetoflana.cfg.</summary>
    internal static class Cfg
    {
        public static ConfigEntry<float> ScreenWidth, ScreenDistance, Stereo, HeadTracking, ResolutionScale, SkyQuality, WaterHeight, RippleStrength, ReflectionQuality, ScenerySize, SceneryPosition;
        public static ConfigEntry<string> Runtime;
        public static ConfigEntry<bool> LabCommands, CutsceneBorders, Scenery, ScreenReflection;
        public const bool DefaultCutsceneBorders = false, DefaultScenery = true, DefaultScreenReflection = true;

        public const float DefaultWidth = 3f, DefaultDistance = 2.5f, DefaultStereo = 1f, DefaultHeadTracking = 1f, DefaultResolution = 1f, DefaultSkyQuality = 1f, DefaultWaterHeight = -0.5f, DefaultRipple = 1f, DefaultReflectionQuality = 1f, DefaultScenerySize = 1f, DefaultSceneryPosition = 0.5f, MaxSceneryPosition = 1.2f;

        public static void Bind(ConfigFile f)
        {
            ScreenWidth = f.Bind("Screen", "Width", DefaultWidth, "Virtual screen width in metres.");
            ScreenDistance = f.Bind("Screen", "Distance", DefaultDistance, "Distance from your head to the virtual screen, in metres.");
            Stereo = f.Bind("Stereo", "Strength", DefaultStereo, "Stereoscopic strength. 1 uses your real eye separation, 0 is flat.");
            HeadTracking = f.Bind("Stereo", "HeadTracking", DefaultHeadTracking, "How strongly head movement shifts the view through the window. 1 is physically correct, 0 is off.");
            CutsceneBorders = f.Bind("Screen", "CutsceneBorders", DefaultCutsceneBorders, "Show the black borders the game adds above and below the picture during cutscenes.");
            SkyQuality = f.Bind("Skybox", "Quality", DefaultSkyQuality, "Sharpness of the sky: 0 low, 1 medium, 2 high. Higher uses more video memory and can cause a brief hitch when the sky changes.");
            Scenery = f.Bind("Scenery", "Enabled", DefaultScenery, "Show the wooden platform and the other 3D scenery around you, built from the game's own village.");
            ScenerySize = BindScenerySize(f);
            SceneryPosition = f.Bind("Scenery", "Position", DefaultSceneryPosition, $"Where you stand on the platform, in metres towards its front edge, from {-MaxSceneryPosition} to {MaxSceneryPosition}. Page Up and Page Down change it in the game.");
            // A new key, because 0 from the old Height key would put the water level with the platform's deck.
            WaterHeight = f.Bind("Water", "Level", DefaultWaterHeight, "Water level in metres, relative to the floor. If the headset doesn't report a floor, 0 is 1.5 m below your head at the last recenter.");
            RippleStrength = f.Bind("Water", "RippleStrength", DefaultRipple, "How strongly the water ripples. 0 is still water.");
            ScreenReflection = f.Bind("Water", "ScreenReflection", DefaultScreenReflection, "Show the game screen's reflection in the lake.");
            ReflectionQuality = f.Bind("Water", "ReflectionQuality", DefaultReflectionQuality, "Sharpness of the game screen's reflection in the water: 0 low, 1 medium, 2 high. Costs almost nothing either way.");
            ResolutionScale = f.Bind("Rendering", "ResolutionScale", DefaultResolution, "Scales the per-eye render resolution.");
            Runtime = f.Bind("OpenXR", "Runtime", "auto",
                "Which OpenXR runtime to use: auto (the system's active runtime), steamvr, vdxr, off (play flat), or a full path to a runtime JSON manifest.");
            LabCommands = f.Bind("Debug", "LabCommands", false, "Developer test automation, driven by a command file. Leave off.");
        }

        /// <summary>The scenery's size, where 1 is 75% of the scale the game's village is built at, which looks
        /// too large in a headset. Until 1.10 the setting was Size, with 1 at the village's own scale.</summary>
        static ConfigEntry<float> BindScenerySize(ConfigFile f)
        {
            float? old = null;
            bool hasScale = false;
            try
            {
                string section = "";
                foreach (var line in File.ReadAllLines(f.ConfigFilePath))
                {
                    var t = line.Trim();
                    if (t.StartsWith("[", StringComparison.Ordinal)) section = t;
                    if (section != "[Scenery]") continue;
                    if (t.StartsWith("Scale =", StringComparison.Ordinal)) hasScale = true;
                    if (t.StartsWith("Size =", StringComparison.Ordinal) && float.TryParse(t.Substring(6).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) old = v;
                }
            }
            catch (IOException) { }
            var entry = f.Bind("Scenery", "Scale", DefaultScenerySize, "Size of the 3D scenery, from 0.5 to 1.5. 1 is 75% of the scale the game's village is built at.");
            if (old.HasValue && !hasScale) entry.Value = (float)Math.Round(Math.Min(1.5, Math.Max(0.5, old.Value / SceneryUnit)) * 20) / 20f;
            return entry;
        }

        /// <summary>The village's own scale, as a fraction, at a Scale of 1.</summary>
        public const float SceneryUnit = 0.75f;

        public static bool VrDisabled => string.Equals((Runtime.Value ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase);

        /// <summary>Resolves the Runtime setting to a manifest path. Empty means the system's active runtime.</summary>
        public static string ResolveRuntime()
        {
            var v = (Runtime.Value ?? "auto").Trim();
            if (v.Length == 0 || v.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "";
            string hint = v.ToLowerInvariant() switch { "steamvr" => "steamxr_win64.json", "vdxr" => "virtualdesktop-openxr.json", _ => null };
            if (hint == null) return v;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1\AvailableRuntimes");
                var found = key?.GetValueNames().FirstOrDefault(n => n.EndsWith(hint, StringComparison.OrdinalIgnoreCase) && File.Exists(n));
                if (found != null) return found;
            }
            catch (Exception e) { Plugin.L.LogWarning("Could not read the OpenXR runtime list: " + e.Message); }
            Plugin.L.LogWarning($"OpenXR runtime '{v}' was not found, using the system's active runtime.");
            return "";
        }
    }
}
