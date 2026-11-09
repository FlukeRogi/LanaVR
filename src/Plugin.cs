using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace LanaVR
{
    [BepInPlugin("com.lanavr.planetoflana", "LanaVR", Version)]
    public class Plugin : BasePlugin
    {
        public const string Version = "1.15.0";
        internal static ManualLogSource L;

        public override void Load()
        {
            L = Log;
            Cfg.Bind(Config);
            Log.LogInfo("LanaVR loading");
            AddComponent<XrRig>();
            AddComponent<VrMenu>();
            var harmony = new Harmony("com.lanavr.planetoflana");
            harmony.PatchAll(typeof(VrMenu.BackPatch));
            harmony.PatchAll(typeof(VrMenu.SetValuePatch));
            harmony.PatchAll(typeof(VrMenu.RefreshPatch));
            harmony.PatchAll(typeof(VrMenu.ToggleNextPatch));
            harmony.PatchAll(typeof(VrMenu.TogglePrevPatch));
            harmony.PatchAll(typeof(VrMenu.ToggleFlipPatch));
            harmony.PatchAll(typeof(VrMenu.ToggleRefreshPatch));
            if (!Cfg.LabCommands.Value) Log.LogInfo("Saves and Steam achievements work as normal");
            else
            {
                Log.LogWarning("Developer test mode is on: saves and Steam achievements are blocked");
                harmony.PatchAll(typeof(Lab.BlockSaves));
                harmony.PatchAll(typeof(Lab.BlockAwards));
                AddComponent<Lab>();
            }
        }
    }
}
