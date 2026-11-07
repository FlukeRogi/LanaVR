using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LanaVR
{
    [StructLayout(LayoutKind.Sequential)]
    public struct EyePose { public float px, py, pz, qx, qy, qz, qw; }

    [StructLayout(LayoutKind.Sequential)]
    public struct XrFrame
    {
        public int running, shouldRender, viewsValid, pad;
        public EyePose left, right;
        // Tangents of each eye's view edges: left, right, up, down.
        public float leftL, leftR, leftU, leftD, rightL, rightR, rightU, rightD;
        public double periodSeconds;
    }

    internal static class Native
    {
        const string Dll = "LanaVRNative";

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

        /// <summary>Loads the bridge and its OpenXR loader from the plugin folder.</summary>
        public static bool Load(string pluginDir)
        {
            const uint LOAD_WITH_ALTERED_SEARCH_PATH = 8;
            var path = Path.Combine(pluginDir, Dll + ".dll");
            if (LoadLibraryExW(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH) != IntPtr.Zero) return true;
            Plugin.L.LogError($"Could not load {path} (Windows error {Marshal.GetLastWin32Error()}). " +
                              "If Smart App Control is on, it can block unsigned mod DLLs.");
            return false;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public static extern int LVR_Create(string runtimeJson);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void LVR_GetRecommended(out int w, out int h);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void LVR_SetEyeTextures(IntPtr left, IntPtr right, int w, int h);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void LVR_SetScreen(float px, float py, float pz, float qx, float qy, float qz, float qw, float width, float height);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void LVR_BeginFrame(out XrFrame frame);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr LVR_GetRenderEvent();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int LVR_SessionState();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern void LVR_LoadSkybox(string path, int maxFaceSize, float fadeSeconds);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void LVR_SetSkybox(float brightness, float yaw);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void LVR_SetSkyTint(float r, float g, float b);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void LVR_SetWater(float headY, float offset, float ripple);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void LVR_GetWater(out float y, out int floorKnown);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void LVR_SetWaterDebug(int mode);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern float LVR_GetFloor();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void LVR_SetScenery(IntPtr left, IntPtr right, IntPtr mirrorLeft, IntPtr mirrorRight);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void LVR_SetSceneryOn(int on);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void LVR_SkyboxStatus(StringBuilder buffer, int size);
        public static string SkyboxStatus
        {
            get
            {
                var sb = new StringBuilder(256);
                LVR_SkyboxStatus(sb, sb.Capacity);
                return sb.ToString();
            }
        }
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr LVR_RuntimeName();
        public static string RuntimeName => Marshal.PtrToStringAnsi(LVR_RuntimeName()) ?? "";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int LVR_HasSession();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int LVR_ExitRequested();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern ulong LVR_FramesSubmitted();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int LVR_PopLog(StringBuilder buffer, int size);

        public static void PumpLogs()
        {
            var sb = new StringBuilder(1024);
            while (LVR_PopLog(sb, sb.Capacity) != 0) Plugin.L.LogInfo("[native] " + sb);
        }
    }
}
