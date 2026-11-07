using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace LanaVR
{
    [BepInPlugin("com.lanavr.planetoflana", "LanaVR", "1.7.0")]
    public class Plugin : BasePlugin
    {
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
            if (Cfg.LabCommands.Value)
            {
                harmony.PatchAll(typeof(Lab.BlockSaves));
                harmony.PatchAll(typeof(Lab.BlockAwards));
                AddComponent<Lab>();
            }
        }
    }
}
