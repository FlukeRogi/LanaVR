using System;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace LanaVR
{
    /// <summary>
    /// Keeps a small record of how smoothly the game runs in VR, in BepInEx\LanaVR-performance.log, which unlike the
    /// BepInEx log survives a restart. Every 10 seconds of play it notes stutters, and once a minute it notes memory,
    /// so a slowdown that starts mid-session can be traced afterwards. The file is kept under about 1 MB.
    /// </summary>
    internal static class PerfWatch
    {
        const float Window = 10f, MemoryEvery = 60f;
        const long MaxBytes = 1_000_000;
        static readonly string FilePath = Path.Combine(BepInEx.Paths.BepInExRootPath, "LanaVR-performance.log");
        static float _windowStart = -1, _lastMemory = -1000, _worst, _total;
        static int _frames, _long, _notes;
        static float _sweepWorst, _notesSince;
        public static float LastSweepMs;

        /// <summary>The time the mod's twice-a-second check of the game's objects took.</summary>
        public static void Sweep(float ms) { LastSweepMs = ms; _sweepWorst = Mathf.Max(_sweepWorst, ms); }

        /// <summary>Something worth knowing about, such as a reflection being resized. At most 20 a minute.</summary>
        public static void Note(string what)
        {
            if (Time.unscaledTime - _notesSince > 60f) { _notesSince = Time.unscaledTime; _notes = 0; }
            if (++_notes <= 20) Write("note: " + what + " | " + Where());
        }
        static bool _wasBad, _started;

        public static void Tick()
        {
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            if (!_started) { _started = true; Write($"--- LanaVR {Plugin.Version} started, {SystemInfo.graphicsDeviceName}"); }
            if (_windowStart < 0) { _windowStart = now; return; }
            _frames++;
            _total += dt;
            _worst = Mathf.Max(_worst, dt);
            if (dt > 0.02f) _long++;  // Slower than 50 frames a second, which a headset shows as judder.
            if (now - _windowStart < Window) return;

            float average = _total / Mathf.Max(1, _frames);
            bool bad = _long > _frames / 20;
            if (bad || _wasBad)
                Write($"{(bad ? "stutter" : "smooth again")}: {_frames} frames, {1f / average:F0} fps, {_long} over 20 ms, worst {_worst * 1000f:F0} ms, " +
                      $"mod {XrRig.I?.FrameMs:F1} ms, sweep worst {_sweepWorst:F1} ms | {Where()} | {Memory()}");
            _wasBad = bad;
            if (now - _lastMemory >= MemoryEvery)
            {
                _lastMemory = now;
                Write($"minute: {1f / average:F0} fps, {_long} over 20 ms, sweep worst {_sweepWorst:F1} ms | {Where()} | {Memory()}");
            }
            _windowStart = now; _frames = 0; _long = 0; _worst = 0; _total = 0; _sweepWorst = 0;
        }

        static string Where()
        {
            var lana = PoL.Characters.Lana.LanaMachine.instance;
            return $"{Skybox.CurrentBackdrop} x={(lana != null ? lana.transform.position.x.ToString("F0") : "-")} scenes={SceneManager.sceneCount}";
        }

        public static string Memory() =>
            $"managed {GC.GetTotalMemory(false) / 1048576} MB, il2cpp heap {Profiler.GetMonoHeapSizeLong() / 1048576} MB used {Profiler.GetMonoUsedSizeLong() / 1048576} MB, " +
            $"unity {Profiler.GetTotalAllocatedMemoryLong() / 1048576} MB, gfx {Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576} MB, " +
            $"process {System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1048576} MB";

        static void Write(string line)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    // Keeps the newer half.
                    var text = File.ReadAllText(FilePath);
                    File.WriteAllText(FilePath, text.Substring(text.Length / 2));
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
