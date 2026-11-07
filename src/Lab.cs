using System;
using System.IO;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LanaVR
{
    /// <summary>
    /// Test automation channel. Polls a command file so an external script can drive the game
    /// without a person at the controls. Only active when the command file's folder exists.
    /// </summary>
    public class Lab : MonoBehaviour
    {
        /// <summary>Test runs must never change a save: Steam Cloud uploads modified saves when the game
        /// closes, which could replace the player's real progress. Only installed when lab mode is on.</summary>
        [HarmonyLib.HarmonyPatch(typeof(Platforms.Platform), nameof(Platforms.Platform.Award))]
        public static class BlockAwards
        {
            // Jumping between levels in tests must not earn the player's Steam achievements.
            public static bool Prefix() { Plugin.L.LogInfo("[lab] blocked an achievement"); return false; }
        }

        [HarmonyLib.HarmonyPatch(typeof(Platforms.Platform), nameof(Platforms.Platform.Save))]
        public static class BlockSaves
        {
            public static bool Prefix(Il2CppSystem.Action onComplete)
            {
                Plugin.L.LogInfo("[lab] blocked a save write");
                onComplete?.Invoke();
                return false;
            }
        }

        public Lab(IntPtr p) : base(p) { }

        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "LanaVR", "lab");
        static string CmdFile => Path.Combine(Dir, "cmd.txt");
        static string OutFile => Path.Combine(Dir, "out.txt");
        float _next;

        static readonly System.Collections.Generic.List<Renderer> _hiddenRenderers = new System.Collections.Generic.List<Renderer>();
        static readonly System.Collections.Generic.List<Canvas> _hiddenCanvases = new System.Collections.Generic.List<Canvas>();
        static string Safe(string name) { foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_'); return name; }

        static void ExportTexture(Texture source, Rect rect, string path)
        {
            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Graphics.Blit(source, rt);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var copy = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(rect, 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(path, UnityEngine.ImageConversion.EncodeToPNG(copy));
            UnityEngine.Object.Destroy(copy);
        }

        bool _watch; float _watchNext;
        void WatchTick()
        {
            if (!_watch || Time.unscaledTime < _watchNext) return;
            _watchNext = Time.unscaledTime + 0.5f;
            var sb = new StringBuilder($"{Time.unscaledTime:F1} ");
            var lana = PoL.Characters.Lana.LanaMachine.instance;
            sb.Append($"x={(lana != null ? lana.transform.position.x.ToString("F1") : "-")} ");
            foreach (var v in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.PostProcessing.PostProcessVolume>())
                if (v.gameObject.scene.IsValid() && v.isActiveAndEnabled && v.weight > 0.001f) sb.Append($"[{v.name}={v.weight:F2}] ");
            sb.Append($"ambient={RenderSettings.ambientSkyColor.r:F2},{RenderSettings.ambientSkyColor.g:F2},{RenderSettings.ambientSkyColor.b:F2} ");
            foreach (var li in Resources.FindObjectsOfTypeAll<Light>())
                if (li.gameObject.scene.IsValid() && li.isActiveAndEnabled && li.type == LightType.Directional) sb.Append($"{li.name}={li.intensity:F2}({li.color.r:F2},{li.color.g:F2},{li.color.b:F2}) ");
            foreach (var pd in Resources.FindObjectsOfTypeAll<UnityEngine.Playables.PlayableDirector>())
                if (pd.gameObject.scene.IsValid() && pd.state == UnityEngine.Playables.PlayState.Playing) sb.Append($"TL:{(pd.playableAsset != null ? pd.playableAsset.name : pd.name)}@{pd.time:F1} ");
            for (int i = 0; i < SceneManager.sceneCount; i++) { var n = SceneManager.GetSceneAt(i).name; if (n.StartsWith("Chapter") && n.Contains("Scene")) sb.Append(n.Replace("Chapter 01 Scene ", "S")).Append(' '); }
            File.AppendAllText(Path.Combine(Dir, "watch.txt"), sb + "\n");
        }

        void Update()
        {
            PadTick();
            WatchTick();
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;
            if (!File.Exists(CmdFile)) return;
            string[] lines;
            try { lines = File.ReadAllLines(CmdFile); File.Delete(CmdFile); } catch { return; }
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                try { Out($"> {line}\n{Run(line)}"); }
                catch (Exception e) { Out($"> {line}\nERROR {e}"); }
            }
        }

        static void Out(string s)
        {
            File.AppendAllText(OutFile, s + "\n");
        }

        string Run(string line)
        {
            var parts = line.Split(' ', 2);
            var arg = parts.Length > 1 ? parts[1] : "";
            switch (parts[0])
            {
                case "dump": return Dump();
                case "ui": return DumpSelectables();
                case "exec": return ExecSelectable(arg);
                case "execi": { var a = arg.Split(' ', 2); return ExecSelectable(a[1], int.Parse(a[0])); }
                case "pp": return DumpPP();
                case "vr":
                    var x = XrRig.I;
                    return $"active={x.Active} eye={x.EyeWidth}x{x.EyeHeight} frameMs={x.FrameMs:F2} fps={1f / Time.smoothDeltaTime:F1} deltaMs={Time.unscaledDeltaTime * 1000:F2} focal={x.Focal:F2} head={x.HeadInfo} submitted={Native.LVR_FramesSubmitted()} state={Native.LVR_SessionState()} win={Screen.width}x{Screen.height}";
                case "reflres":
                    foreach (var r in Resources.FindObjectsOfTypeAll<PoL.Rendering.ReflectionCamera>())
                    {
                        if (!r.gameObject.scene.IsValid() || !r.isActiveAndEnabled) continue;
                        r.renderResolutionBase = int.Parse(arg);
                        r.RebuildRenderTargetTexture();
                    }
                    return DumpPP().Split('\n').FirstOrDefault(l => l.StartsWith("REFL"));
                case "shadows": QualitySettings.shadows = (ShadowQuality)int.Parse(arg); return "ok " + QualitySettings.shadows;
                case "shadowdist": QualitySettings.shadowDistance = float.Parse(arg); return "ok";
                case "shadowcasc": QualitySettings.shadowCascades = int.Parse(arg); return "ok";
                case "q": QualitySettings.SetQualityLevel(int.Parse(arg), true); return "ok " + QualitySettings.names[QualitySettings.GetQualityLevel()] + " of " + string.Join(",", QualitySettings.names);
                case "stats":
                    return $"shadows={QualitySettings.shadows} dist={QualitySettings.shadowDistance} casc={QualitySettings.shadowCascades} res={QualitySettings.shadowResolution} lod={QualitySettings.lodBias} pixelLights={QualitySettings.pixelLightCount} aniso={QualitySettings.anisotropicFiltering} softP={QualitySettings.softParticles} rendererCount={Resources.FindObjectsOfTypeAll<Renderer>().Length} lights={Resources.FindObjectsOfTypeAll<Light>().Length}";
                case "menu":
                    if (arg == "open") VrMenu.I.Open();
                    else if (arg == "close") VrMenu.I.Close();
                    return VrMenu.I.Status;
                case "bg": UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior = (UnityEngine.InputSystem.InputSettings.BackgroundBehavior)int.Parse(arg); return "bg " + UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior;
                case "res": { var r = arg.Split('x'); Screen.SetResolution(int.Parse(r[0]), int.Parse(r[1]), FullScreenMode.FullScreenWindow); return "ok"; }
                case "skyload":
                {
                    var a = arg.Split('|');
                    Native.LVR_LoadSkybox(a[0], int.Parse(a[1]), a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 0f);
                    return "loading";
                }
                case "sky": return "status=" + Native.SkyboxStatus.Replace(' ', '_') + " brightness=" + Cfg.SkyBrightness.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " mapped=" + Skybox.MappedCount + " file=" + Skybox.CurrentFile + " backdrop=" + Skybox.CurrentBackdrop.Replace(' ', '_') + " tintVolumes=" + Skybox.TintVolumes + $" tint={Skybox.CurrentTint.r:F2},{Skybox.CurrentTint.g:F2},{Skybox.CurrentTint.b:F2}";
                case "waterdebug": Native.LVR_SetWaterDebug(int.Parse(arg)); return "debug " + arg;
                case "water":
                {
                    Native.LVR_GetWater(out float y, out int known);
                    return "y=" + y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " floorKnown=" + known;
                }
                case "settingscanvas":
                    foreach (var v in Resources.FindObjectsOfTypeAll<PoL.UI.SettingsView>()) if (v.gameObject.scene.IsValid()) v.canvas.enabled = arg == "1";
                    return "ok";
                case "scenes":
                {
                    var sb = new StringBuilder();
                    foreach (var db in Resources.FindObjectsOfTypeAll<PoL.Story.Structure.ChapterDatabaseAsset>())
                        foreach (var ch in db.chapters)
                            foreach (var sc in ch.scenes)
                                sb.AppendLine($"{sc.name} | {(sc.backdrop != null ? sc.backdrop.name : "(none)")} | build={sc.IncludeInBuild}");
                    return sb.ToString();
                }
                case "goto":
                {
                    foreach (var db in Resources.FindObjectsOfTypeAll<PoL.Story.Structure.ChapterDatabaseAsset>())
                    {
                        var sc = db.GetSceneAssetByName(arg);
                        if (sc == null) continue;
                        PoL.Gameplay.GameplayEvents.LoadChapterScene(sc.Cast<PoL.Story.Structure.IChapterSceneAsset>());
                        return "loading " + sc.name;
                    }
                    return "scene not found";
                }
                case "selscene": PoL.UI.UIEvents.SelectScene(arg); return "selected " + arg;
                case "lanapos": { var l = PoL.Characters.Lana.LanaMachine.instance; return l != null ? "lana=" + l.transform.position : "lana=none"; }
                case "darken":
                {
                    var sb = new StringBuilder();
                    foreach (var v in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.PostProcessing.PostProcessVolume>())
                    {
                        if (!v.gameObject.scene.IsValid()) continue;
                        sb.Append($"VOL {PathOf(v.transform)} on={v.isActiveAndEnabled} weight={v.weight:F3} prio={v.priority} profile={(v.sharedProfile != null ? v.sharedProfile.name : "none")}");
                        if (v.sharedProfile != null)
                            foreach (var e in v.sharedProfile.settings)
                            {
                                var cg = e.TryCast<UnityEngine.Rendering.PostProcessing.ColorGrading>();
                                if (cg != null) sb.Append($" exposure={cg.postExposure.value:F2}/{cg.postExposure.overrideState} sat={cg.saturation.value:F1} bright={cg.brightness.value:F1} contrast={cg.contrast.value:F1} filter={cg.colorFilter.value} mode={cg.gradingMode.value}");
                            }
                        sb.AppendLine();
                    }
                    foreach (var t in Resources.FindObjectsOfTypeAll<PoL.Gameplay.TriggerActionChangePostprocess>())
                    {
                        if (!t.gameObject.scene.IsValid()) continue;
                        sb.AppendLine($"PPTRIG {t.gameObject.scene.name} | {PathOf(t.transform)} on={t.isActiveAndEnabled} pos={t.transform.position} weight={t.weight:F2} dur={t.duration:F1} vol={(t.volume != null ? t.volume.name : "none")} profile={(t.profile != null ? t.profile.name : "none")}");
                    }
                    foreach (var t in Resources.FindObjectsOfTypeAll<PoL.Gameplay.TriggerActionChangeLighting>())
                    {
                        if (!t.gameObject.scene.IsValid()) continue;
                        sb.AppendLine($"LIGHTTRIG {t.gameObject.scene.name} | {PathOf(t.transform)} on={t.isActiveAndEnabled} pos={t.transform.position}");
                    }
                    sb.AppendLine($"ambient={RenderSettings.ambientLight} ambientSky={RenderSettings.ambientSkyColor} fog={RenderSettings.fog}/{RenderSettings.fogColor} sun={(RenderSettings.sun != null ? RenderSettings.sun.color + " x" + RenderSettings.sun.intensity : "none")}");
                    return sb.ToString();
                }
                case "actions":
                {
                    var sb = new StringBuilder();
                    foreach (var a in Resources.FindObjectsOfTypeAll<PoL.Gameplay.AbstractTriggerAction>())
                    {
                        if (!a.gameObject.scene.IsValid()) continue;
                        var type = a.GetIl2CppType().Name;
                        if (type.IndexOf("Light", StringComparison.OrdinalIgnoreCase) < 0 && type.IndexOf("Post", StringComparison.OrdinalIgnoreCase) < 0 && type.IndexOf("Fog", StringComparison.OrdinalIgnoreCase) < 0 && type.IndexOf("Scene", StringComparison.OrdinalIgnoreCase) < 0 && type.IndexOf("Cutscene", StringComparison.OrdinalIgnoreCase) < 0 && type.IndexOf("Timeline", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        sb.AppendLine($"ACT {a.gameObject.scene.name} | {type} | {PathOf(a.transform)} | pos={a.transform.position} on={a.isActiveAndEnabled}");
                    }
                    foreach (var pd in Resources.FindObjectsOfTypeAll<UnityEngine.Playables.PlayableDirector>())
                        if (pd.gameObject.scene.IsValid()) sb.AppendLine($"TL {pd.gameObject.scene.name} | {PathOf(pd.transform)} | asset={(pd.playableAsset != null ? pd.playableAsset.name : "none")} state={pd.state} t={pd.time:F1} pos={pd.transform.position}");
                    return sb.ToString();
                }
                case "watch": _watch = arg == "on"; _watchNext = 0; return "watch " + _watch;
                case "padhold":
                {
                    if (_pad == null) { _pad = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Gamepad>(); _pad.MakeCurrent(); }
                    int hold = arg switch { "up" => 0, "down" => 1, "left" => 2, "right" => 3, "y" => 4, "b" => 5, "a" => 6, "x" => 7, _ => -1 };
                    _padRelease = -1;
                    QueuePad(hold >= 0 ? 1u << hold : 0u);
                    return "holding " + arg;
                }
                case "teleport":
                {
                    var p = arg.Split(' ');
                    var l = PoL.Characters.Lana.LanaMachine.instance;
                    if (l == null) return "no lana";
                    l.transform.position = new Vector3(float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), l.transform.position.z);
                    return "lana=" + l.transform.position;
                }
                case "ppw":
                {
                    var i = arg.LastIndexOf(' ');
                    var who = arg.Substring(0, i); float w = float.Parse(arg.Substring(i + 1), System.Globalization.CultureInfo.InvariantCulture); int n = 0;
                    foreach (var v in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.PostProcessing.PostProcessVolume>())
                        if (v.gameObject.scene.IsValid() && v.name == who) { v.weight = w; n++; }
                    return $"set {n} volume(s)";
                }
                case "bdlist":
                {
                    var sb = new StringBuilder();
                    var cam = Camera.main;
                    foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
                    {
                        if (!r.gameObject.scene.IsValid() || !r.gameObject.scene.name.Contains(" Backdrop") || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                        var m = r.sharedMaterial; var t = m != null ? m.mainTexture : null;
                        var dist = cam != null ? Vector3.Dot(r.bounds.center - cam.transform.position, cam.transform.forward) : 0;
                        sb.AppendLine($"{dist:F0} | {r.bounds.size.x:F0}x{r.bounds.size.y:F0} | {r.GetIl2CppType().Name} | layer={r.gameObject.layer} | {PathOf(r.transform)} | mat={(m != null ? m.name : "none")} | shader={(m != null && m.shader != null ? m.shader.name : "none")} | tex={(t != null ? t.name + " " + t.width + "x" + t.height : "none")}");
                    }
                    return sb.ToString();
                }
                case "bdonly":
                {
                    _hiddenRenderers.Clear(); _hiddenCanvases.Clear();
                    foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
                    {
                        if (!r.gameObject.scene.IsValid() || r.gameObject.scene.name.Contains(" Backdrop") || !r.enabled) continue;
                        r.enabled = false; _hiddenRenderers.Add(r);
                    }
                    foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>()) if (c.gameObject.scene.IsValid() && c.isRootCanvas && c.enabled) { c.enabled = false; _hiddenCanvases.Add(c); }
                    return "hid " + _hiddenRenderers.Count + " renderers";
                }
                case "skylayers":
                {
                    float far = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture); var cam = Camera.main; int n = 0;
                    foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
                    {
                        if (!r.gameObject.scene.IsValid() || !r.gameObject.scene.name.Contains(" Backdrop") || !r.enabled) continue;
                        if (Vector3.Dot(r.transform.position - cam.transform.position, cam.transform.forward) >= far) continue;
                        r.enabled = false; _hiddenRenderers.Add(r); n++;
                    }
                    return "hid " + n + " near backdrop renderers";
                }
                case "bdrestore":
                    foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
                    foreach (var c in _hiddenCanvases) if (c != null) c.enabled = true;
                    _hiddenRenderers.Clear(); _hiddenCanvases.Clear();
                    return "restored";
                case "bdexport":
                {
                    var parts2 = arg.Split('|'); var folder = parts2[0]; float far = float.Parse(parts2[1], System.Globalization.CultureInfo.InvariantCulture); var cam = Camera.main;
                    var seen = new System.Collections.Generic.HashSet<string>(); var sb = new StringBuilder();
                    foreach (var r in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
                    {
                        if (!r.gameObject.scene.IsValid() || !r.gameObject.scene.name.Contains(" Backdrop") || r.sprite == null) continue;
                        float dist = Vector3.Dot(r.transform.position - cam.transform.position, cam.transform.forward);
                        if (dist < far) continue;
                        var sp = r.sprite;
                        // A packed sprite has no simple rectangle, so its whole texture page is exported instead.
                        var tr = sp.packed ? new Rect(0, 0, sp.texture.width, sp.texture.height) : sp.rect;
                        if (tr.width < 1 || tr.height < 1) tr = new Rect(0, 0, sp.texture.width, sp.texture.height);
                        if (!seen.Add(sp.name + "|" + sp.texture.name)) { continue; }
                        ExportTexture(sp.texture, tr, Path.Combine(folder, Safe(sp.name) + ".png"));
                        sb.AppendLine($"{Safe(sp.name)}.png | {(int)tr.width}x{(int)tr.height} | {dist:F0} away | {(r.gameObject.activeInHierarchy && r.enabled ? (r.color.a > 0.01f ? "showing" : "faded out") : "not showing here")} | {PathOf(r.transform)} | colour={r.color}");
                    }
                    var sky = RenderSettings.skybox;
                    if (sky != null)
                        foreach (var prop in sky.GetTexturePropertyNames())
                        {
                            var t = sky.GetTexture(prop);
                            if (t == null || !seen.Add("sky|" + t.name)) continue;
                            if (t.TryCast<Texture2D>() == null) continue;
                            ExportTexture(t, new Rect(0, 0, t.width, t.height), Path.Combine(folder, "skybox_" + Safe(t.name) + ".png"));
                            sb.AppendLine($"skybox_{Safe(t.name)}.png | {t.width}x{t.height} | skybox material {sky.name} | showing | property {prop} | colour=-");
                        }
                    return sb.ToString();
                }
                case "camwide":
                {
                    var cam = Camera.main; var brain = cam.GetComponent<Cinemachine.CinemachineBrain>();
                    if (brain != null) brain.enabled = false;
                    cam.fieldOfView = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                    return $"fov={cam.fieldOfView} clear={cam.clearFlags} skybox={(RenderSettings.skybox != null ? RenderSettings.skybox.name + "/" + RenderSettings.skybox.shader.name : "none")} pos={cam.transform.position}";
                }
                case "bars":
                {
                    var a = arg.Split(' ');
                    PoL.UI.UIEvents.CinematicBars(a[0] == "on", a.Length > 1 && a[1] == "instant");
                    return "bars " + arg;
                }
                case "barsstate":
                {
                    var sb = new StringBuilder();
                    foreach (var v in Resources.FindObjectsOfTypeAll<PoL.UI.CinematicView>())
                    {
                        if (!v.gameObject.scene.IsValid()) continue;
                        sb.Append($"view={v.name} canvas={v.canvas.enabled} size={v.Size:F3}");
                        foreach (var b in new[] { v.top, v.bottom })
                            sb.Append(b == null ? " bar=none" : $" bar={b.name} active={b.gameObject.activeSelf} h={b.rect.height:F1}");
                        sb.AppendLine();
                    }
                    return sb.Length > 0 ? sb.ToString() : "no view";
                }
                case "census": return Census(arg);
                case "scenery": return Scenery.Status;
                case "propgallery": return PropGallery(arg);
                case "propload":
                {
                    _propLoad = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(arg, UnityEngine.SceneManagement.LoadSceneMode.Additive);
                    return _propLoad != null ? "loading " + arg : "load refused";
                }
                case "propunload":
                    UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(arg);
                    return "unloading " + arg;
                case "propgc": Resources.UnloadUnusedAssets(); return "unloading unused assets";
                case "pad": return Pad(arg);
                case "ts": Time.timeScale = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture); return "timeScale " + Time.timeScale;
                case "cone":
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"volumeLights={XrRig.I.VolumeLightCount} volumeCameraInEyePass={XrRig.I.VolumeCameraInEyePass}");
                    foreach (var v in Resources.FindObjectsOfTypeAll<VolumeLight>())
                    {
                        if (!v.gameObject.scene.IsValid() || !v.isActiveAndEnabled) continue;
                        var m = v.GetMaterial();
                        sb.AppendLine($"VOL {v.name} target={(v.TargetCamera != null ? v.TargetCamera.name : "null")} source={(v.sourceCamera != null ? v.sourceCamera.name : "null")} depthBuf={(v.depthBuffer != null ? v.depthBuffer.width + "x" + v.depthBuffer.height + " " + v.depthBuffer.format : "null")} colorBuf={(v.colorBuffer != null ? v.colorBuffer.width + "x" + v.colorBuffer.height : "null")} cookie={v.useCookieCutter} susp={v.IsSuspended} enabledByRenderer={v.isEnabledByRenderer}");
                        if (m != null)
                        {
                            sb.AppendLine($"  shader={m.shader.name} queue={m.renderQueue} keywords=[{string.Join(",", m.shaderKeywords)}]");
                            sb.AppendLine($"  _CameraPos={m.GetVector(VolumeLight.ShaderProperties.CameraPos)} _CameraValues={m.GetVector(VolumeLight.ShaderProperties.CameraValues)} _Origin={m.GetVector(VolumeLight.ShaderProperties.OrginPosition)} radius={m.GetFloat(VolumeLight.ShaderProperties.RadiusLength)}");
                            sb.AppendLine($"  VP={m.GetMatrix(VolumeLight.ShaderProperties.VolumetricCameraVP)}");
                            var t = m.GetTexture(VolumeLight.ShaderProperties.VolumetricCameraDepth);
                            sb.AppendLine($"  depthTex={(t != null ? t.name + " " + t.width + "x" + t.height : "null")}");
                        }
                        var r = v.volumeMeshRender;
                        if (r != null) sb.AppendLine($"  mesh renderer={r.name} layer={r.gameObject.layer} enabled={r.enabled} bounds={r.bounds.center} {r.bounds.size}");
                    }
                    var mc = Camera.main;
                    sb.AppendLine($"main={(mc != null ? mc.name + " pos=" + mc.transform.position : "null")} globalCamPos={Shader.GetGlobalVector("_CameraPosition")} worldCamPos={Shader.GetGlobalVector("_WorldSpaceCameraPos")}");
                    sb.AppendLine($"globalVP={Shader.GetGlobalMatrix("_ViewProjection")}");
                    if (mc != null) sb.AppendLine($"expected VP={GL.GetGPUProjectionMatrix(mc.projectionMatrix, true) * mc.worldToCameraMatrix}");
                    return sb.ToString();
                }
                case "audio":
                {
                    var sb = new StringBuilder();
                    foreach (var r in Resources.FindObjectsOfTypeAll<PoL.UI.ToggleAudioVolumeButton>())
                        if (r.gameObject.scene.IsValid() && !string.IsNullOrEmpty(r.parameter)) sb.Append(r.parameter).Append('=').Append(r.Volume.ToString("F3")).Append(' ');
                    return sb.ToString();
                }
                case "input":
                {
                    var st = UnityEngine.InputSystem.InputSystem.settings;
                    var ds = string.Join(", ", System.Linq.Enumerable.Select(UnityEngine.InputSystem.InputSystem.devices.ToArray(), d => d.name + (d.enabled ? "" : "(disabled)")));
                    var m = PoL.Input.InputHandler.BindingMaskDevice;
                    var es = UnityEngine.EventSystems.EventSystem.current;
                    return $"focused={Application.isFocused} runInBg={Application.runInBackground} update={st.updateMode} devices=[{ds}] padDown={(_pad != null && _pad.dpad.down.isPressed)} padA={(_pad != null && _pad.buttonSouth.isPressed)} bg={st.backgroundBehavior} current={UnityEngine.InputSystem.Gamepad.current?.name} mask={m?.name} module={es?.currentInputModule?.GetIl2CppType().FullName} esFocus={(es != null ? es.isFocused : false)} sendNav={(es != null ? es.sendNavigationEvents : false)}";
                }
                case "cfg": return $"width={Cfg.ScreenWidth.Value} distance={Cfg.ScreenDistance.Value} stereo={Cfg.Stereo.Value} head={Cfg.HeadTracking.Value} sky={Cfg.SkyBrightness.Value} water={Cfg.WaterHeight.Value} ripple={Cfg.RippleStrength.Value} borders={Cfg.CutsceneBorders.Value} scenery={Cfg.Scenery.Value}";
                case "recenter": XrRig.I.RequestRecenter(); return "ok";
                case "set":
                {
                    var a = arg.Split(' ');
                    float v = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
                    switch (a[0])
                    {
                        case "width": Cfg.ScreenWidth.Value = v; break;
                        case "distance": Cfg.ScreenDistance.Value = v; break;
                        case "stereo": Cfg.Stereo.Value = v; break;
                        case "head": Cfg.HeadTracking.Value = v; break;
                        case "sky": Cfg.SkyBrightness.Value = v; break;
                        case "water": Cfg.WaterHeight.Value = v; break;
                        case "ripple": Cfg.RippleStrength.Value = v; break;
                        case "borders": Cfg.CutsceneBorders.Value = v > 0.5f; break;
                        case "scenery": Cfg.Scenery.Value = v > 0.5f; break;
                        default: return "unknown setting";
                    }
                    return "ok";
                }
                case "tree": return Tree(arg);
                case "quit": Application.Quit(); return "quitting";
                case "shot":
                    ScreenCapture.CaptureScreenshot(Path.Combine(Dir, arg.Length > 0 ? arg : "shot.png"));
                    return "ok";
                default: return "unknown";
            }
        }

        static string PathOf(Transform t)
        {
            var s = t.name;
            while ((t = t.parent) != null) s = t.name + "/" + s;
            return s;
        }

        static string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"time={Time.time:F1} timeScale={Time.timeScale} res={Screen.width}x{Screen.height} fs={Screen.fullScreenMode} " +
                          $"colorSpace={QualitySettings.activeColorSpace} vsync={QualitySettings.vSyncCount} targetFps={Application.targetFrameRate} " +
                          $"quality={QualitySettings.GetQualityLevel()}/{QualitySettings.names[QualitySettings.GetQualityLevel()]} shadowDist={QualitySettings.shadowDistance} " +
                          $"gfx={SystemInfo.graphicsDeviceType} multithread={SystemInfo.graphicsMultiThreaded} runInBg={Application.runInBackground}");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                sb.AppendLine($"scene[{i}] {s.name} idx={s.buildIndex} loaded={s.isLoaded}");
            }
            var main = Camera.main;
            sb.AppendLine($"Camera.main={(main != null ? PathOf(main.transform) : "null")} allCameras={Camera.allCamerasCount}");
            sb.AppendLine("--- cameras");
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (!c.gameObject.scene.IsValid()) continue;
                var tt = c.targetTexture;
                sb.AppendLine($"CAM {PathOf(c.transform)} en={c.enabled} active={c.gameObject.activeInHierarchy} depth={c.depth} clear={c.clearFlags} " +
                              $"bg={c.backgroundColor} mask=0x{c.cullingMask:X8} ortho={c.orthographic} fov={c.fieldOfView:F2} near={c.nearClipPlane} far={c.farClipPlane} " +
                              $"rect={c.rect} hdr={c.allowHDR} msaa={c.allowMSAA} path={c.actualRenderingPath} depthMode={c.depthTextureMode} " +
                              $"target={(tt != null ? $"{tt.name} {tt.width}x{tt.height} {tt.format}" : "none")} cmdbufs={c.commandBufferCount} " +
                              $"pos={c.transform.position} rot={c.transform.rotation.eulerAngles}");
                sb.AppendLine("   comps: " + string.Join(", ", c.GetComponents<Component>().Select(x => x.GetIl2CppType().FullName)));
            }
            sb.AppendLine("--- canvases");
            foreach (var cv in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (!cv.gameObject.scene.IsValid() || !cv.isRootCanvas) continue;
                sb.AppendLine($"CANVAS {PathOf(cv.transform)} en={cv.enabled} active={cv.gameObject.activeInHierarchy} mode={cv.renderMode} " +
                              $"cam={(cv.worldCamera != null ? cv.worldCamera.name : "none")} sort={cv.sortingOrder} planeDist={cv.planeDistance} layer={cv.gameObject.layer}");
            }
            return sb.ToString();
        }

        // A virtual gamepad, so tests exercise the game's real input path. IL2CPP stripped the generic
        // state helpers, so these are raw state events with the Gamepad state layout (28 bytes).
        static UnityEngine.InputSystem.Gamepad _pad;
        static int _padRelease = -1;

        static unsafe void QueuePad(uint buttons)
        {
            byte* e = stackalloc byte[52];
            for (int i = 0; i < 52; i++) e[i] = 0;
            *(int*)e = 0x53544154;                     // 'STAT' event
            *(ushort*)(e + 4) = 52;                    // size in bytes
            *(ushort*)(e + 6) = (ushort)_pad.deviceId;
            *(double*)(e + 8) = UnityEngine.InputSystem.LowLevel.InputState.currentTime;
            *(int*)(e + 20) = 0x47504144;              // 'GPAD' state format
            *(uint*)(e + 24) = buttons;
            UnityEngine.InputSystem.InputSystem.QueueEvent(new UnityEngine.InputSystem.LowLevel.InputEventPtr((UnityEngine.InputSystem.LowLevel.InputEvent*)e));
        }

        static string Pad(string button)
        {
            if (_pad == null) { _pad = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Gamepad>(); _pad.MakeCurrent(); }
            int bit = button switch { "up" => 0, "down" => 1, "left" => 2, "right" => 3, "y" => 4, "b" => 5, "a" => 6, "x" => 7, "l3" => 8, "r3" => 9, "start" => 12, _ => -1 };
            uint mask = 0;
            if (button == "recenter") mask = (1u << 8) | (1u << 9);
            else if (bit >= 0) mask = 1u << bit;
            else return "unknown button";
            QueuePad(mask);
            _padRelease = Time.frameCount + 6;
            return "pressed " + button;
        }

        static void PadTick()
        {
            if (_padRelease < 0 || Time.frameCount < _padRelease) return;
            QueuePad(0);
            _padRelease = -1;
        }

        static string DumpPP()
        {
            var sb = new StringBuilder();
            foreach (var l in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.PostProcessing.PostProcessLayer>())
            {
                if (!l.gameObject.scene.IsValid()) continue;
                sb.AppendLine($"LAYER {PathOf(l.transform)} en={l.enabled} aa={l.antialiasingMode} stopNaN={l.stopNaNPropagation} directRT={l.finalBlitToCameraTarget} volLayer=0x{l.volumeLayer.value:X} trigger={(l.volumeTrigger != null ? l.volumeTrigger.name : "none")}");
            }
            foreach (var v in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.PostProcessing.PostProcessVolume>())
            {
                if (!v.gameObject.scene.IsValid() || !v.isActiveAndEnabled) continue;
                var p = v.sharedProfile;
                sb.AppendLine($"VOL {PathOf(v.transform)} global={v.isGlobal} weight={v.weight} prio={v.priority} profile={(p != null ? p.name : "none")}");
                if (p != null) foreach (var e in p.settings) sb.AppendLine($"   {e.GetIl2CppType().Name} active={e.active} enabled={e.enabled.value}");
            }
            foreach (var g in Resources.FindObjectsOfTypeAll<PoL.Cameras.GameplayCamera>())
            {
                if (!g.gameObject.scene.IsValid()) continue;
                sb.AppendLine($"GCAM {PathOf(g.transform)} active={g.isActiveAndEnabled} dist={g.Distance} pos={g.Position} cam={(g.Camera != null ? g.Camera.name : "null")}");
            }
            foreach (var r in Resources.FindObjectsOfTypeAll<PoL.Rendering.ReflectionCamera>())
            {
                if (!r.gameObject.scene.IsValid() || !r.isActiveAndEnabled) continue;
                sb.AppendLine($"REFL {PathOf(r.transform)} toReflect={(r.cameraToReflect != null ? r.cameraToReflect.name : "null")} rt={(r.reflectionRenderTarget != null ? r.reflectionRenderTarget.width.ToString() : "null")}");
            }
            return sb.ToString();
        }

        static string DumpSelectables()
        {
            var sb = new StringBuilder();
            foreach (var u in Resources.FindObjectsOfTypeAll<PoL.UI.UISelectable>())
            {
                if (!u.gameObject.scene.IsValid() || !u.gameObject.activeInHierarchy) continue;
                sb.AppendLine($"{u.GetIl2CppType().Name}: {PathOf(u.transform)}");
            }
            return sb.ToString();
        }

        static string ExecSelectable(string nameContains, int index = 0)
        {
            if (string.IsNullOrWhiteSpace(nameContains)) return "refused: empty filter";
            foreach (var u in Resources.FindObjectsOfTypeAll<PoL.UI.UISelectable>())
            {
                if (!u.gameObject.scene.IsValid() || !u.gameObject.activeInHierarchy) continue;
                if (PathOf(u.transform).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0 && index-- == 0)
                {
                    u.Execute();
                    return "executed " + PathOf(u.transform);
                }
            }
            return "not found";
        }

        // A trial of the idea of building a small scene around the player from the game's own 3D props: copies
        // a few from a loaded level into a place of their own, and renders them with a separate camera.
        const int PropLayer = 30;
        static readonly Vector3 PropOrigin = new Vector3(20000, 20000, 0);
        static AsyncOperation _propLoad;

        /// <summary>Lays out every usable mesh and material pair from the loaded level on a grid, unlit, and
        /// saves it from two sides, because the art is painted for one viewing direction.</summary>
        static string PropGallery(string arg)
        {
            var parts = arg.Split('|', 2);
            var prefix = parts[0];
            var filter = parts.Length > 1 ? new System.Text.RegularExpressions.Regex(parts[1]) : null;
            var root = new GameObject("Prop gallery");
            var seen = new System.Collections.Generic.HashSet<string>();
            var items = new System.Collections.Generic.List<(string key, MeshRenderer r, Mesh mesh)>();
            foreach (var r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (!r.gameObject.scene.IsValid() || r.gameObject.scene.name != "Chapter 01 Scene 10" || r.isPartOfStaticBatch) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || r.sharedMaterial == null) continue;
                var n = mf.sharedMesh.name;
                if (!(n.StartsWith("Std_Pg_") || n.StartsWith("Tailo_prop_"))) continue;
                var key = n + " | " + r.sharedMaterial.name;
                if (filter != null && !filter.IsMatch(key)) continue;
                if (seen.Add(key)) items.Add((key, r, mf.sharedMesh));
            }
            items.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
            if (items.Count > 84) items.RemoveRange(84, items.Count - 84);
            var unlit = Shader.Find("Unlit/Texture");
            const int Columns = 12; const float Cell = 7f;
            var list = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                var (key, r, mesh) = items[i];
                var go = new GameObject(key) { layer = PropLayer };
                go.transform.SetParent(root.transform, false);
                go.transform.rotation = r.transform.rotation;
                go.transform.localScale = r.transform.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var nr = go.AddComponent<MeshRenderer>();
                var mats = r.sharedMaterials;
                var own = new Material[mats.Length];
                for (int m = 0; m < mats.Length; m++)
                {
                    own[m] = new Material(unlit);
                    if (mats[m] != null && mats[m].mainTexture != null) own[m].mainTexture = mats[m].mainTexture;
                }
                nr.materials = own;
                nr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                float biggest = Mathf.Max(nr.bounds.size.x, nr.bounds.size.y, nr.bounds.size.z);
                if (biggest > Cell * 0.9f) go.transform.localScale *= Cell * 0.9f / biggest;
                int row = i / Columns, col = i % Columns;
                var want = PropOrigin + new Vector3(col * Cell, 0, -row * Cell);
                var b = nr.bounds;
                go.transform.position += want - new Vector3(b.center.x, b.min.y, b.center.z);
                list.AppendLine($"r{row} c{col} {key} size={b.size} verts={mesh.vertexCount} tex={(mats[0] != null && mats[0].mainTexture != null ? mats[0].mainTexture.name : "none")} count-in-scene=1");
            }
            File.WriteAllText(Path.Combine(Dir, prefix + ".txt"), list.ToString());
            int rows = (items.Count + Columns - 1) / Columns;
            var centre = PropOrigin + new Vector3((Columns - 1) * Cell * 0.5f, 2f, -(rows - 1) * Cell * 0.5f);
            foreach (var (side, yaw) in new[] { ("front", 0f), ("back", 180f), ("left", 90f) })
            {
                var go = new GameObject("Gallery camera");
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = rows * Cell * 0.5f + 3f;
                cam.cullingMask = 1 << PropLayer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.2f, 0.62f, 0.72f, 1);
                cam.farClipPlane = 1000;
                var rt = new RenderTexture(3840, 2160, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                go.transform.position = centre + Quaternion.Euler(25, yaw, 0) * new Vector3(0, 0, -300);
                go.transform.rotation = Quaternion.Euler(25, yaw, 0);
                cam.Render();
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = previous;
                File.WriteAllBytes(Path.Combine(Dir, $"{prefix}_{side}.png"), UnityEngine.ImageConversion.EncodeToPNG(tex));
                cam.targetTexture = null;
                UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(tex); UnityEngine.Object.Destroy(go);
            }
            UnityEngine.Object.Destroy(root);
            return $"{items.Count} items, {rows} rows";
        }

        /// <summary>Lists what each loaded scene draws with, to show which of the game's art is real 3D.</summary>
        static string Census(string file)
        {
            var counts = new System.Collections.Generic.SortedDictionary<string, int>();
            var rows = new System.Collections.Generic.SortedDictionary<string, int>();
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (!r.gameObject.scene.IsValid()) continue;
                var type = r.GetIl2CppType().Name; var scene = r.gameObject.scene.name;
                var key = scene + " | " + type;
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
                var m = r.sharedMaterial; var t = m != null ? m.mainTexture : null;
                string art = type;
                var mf = r.GetComponent<MeshFilter>(); var sk = r.TryCast<SkinnedMeshRenderer>(); var sr = r.TryCast<SpriteRenderer>();
                if (mf != null && mf.sharedMesh != null) art = $"mesh={mf.sharedMesh.name} verts={mf.sharedMesh.vertexCount} readable={mf.sharedMesh.isReadable}";
                else if (sk != null && sk.sharedMesh != null) art = $"skinned={sk.sharedMesh.name} verts={sk.sharedMesh.vertexCount} readable={sk.sharedMesh.isReadable}";
                else if (sr != null && sr.sprite != null) art = $"sprite={sr.sprite.name}";
                var row = $"{scene} | {type} | {r.name} | {art} | size={r.bounds.size.x:F1}x{r.bounds.size.y:F1}x{r.bounds.size.z:F1} | shader={(m != null && m.shader != null ? m.shader.name : "none")} | tex={(t != null ? t.name : "none")}";
                rows[row] = rows.TryGetValue(row, out var c) ? c + 1 : 1;
            }
            var sb = new StringBuilder();
            foreach (var kv in counts) sb.AppendLine($"{kv.Key} = {kv.Value}");
            var all = new StringBuilder(sb.ToString());
            foreach (var kv in rows) all.AppendLine($"x{kv.Value} {kv.Key}");
            File.WriteAllText(Path.Combine(Dir, file), all.ToString());
            return sb.ToString();
        }

        static string Tree(string rootName)
        {
            var sb = new StringBuilder();
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.parent != null || !t.gameObject.scene.IsValid()) continue;
                if (rootName.Length > 0 && t.name.IndexOf(rootName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Walk(t, 0, sb, rootName.Length > 0 ? 8 : 0);
            }
            return sb.ToString();
        }

        static void Walk(Transform t, int d, StringBuilder sb, int max)
        {
            sb.Append(' ', d * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)").Append(" [")
              .Append(string.Join(",", t.GetComponents<Component>().Where(c => c.GetIl2CppType().Name != "Transform" && c.GetIl2CppType().Name != "RectTransform").Select(c => c.GetIl2CppType().Name))).AppendLine("]");
            if (d >= max) return;
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), d + 1, sb, max);
        }
    }
}



