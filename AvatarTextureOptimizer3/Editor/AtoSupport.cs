// English: Platform detection, VRC descriptor check, cancel, progress.
// 中文：平台检测、VRC 描述符检查、取消与进度。
using System;
using nadena.dev.ndmf;
using net.fosa.ato;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class AtoPlatformUtil
    {
        public static AtoPlatform Detect(BuildContext ctx)
        {
            try
            {
                var name = ctx.PlatformProvider != null ? ctx.PlatformProvider.QualifiedName : "";
                AtoLog.VerboseInfo("NDMF platform: " + name);
                if (!string.IsNullOrEmpty(name))
                {
                    var n = name.ToLowerInvariant();
                    if (n.Contains("android") || n.Contains("quest") || n.Contains("mobile"))
                        return AtoPlatform.Android;
                    if (n.Contains("ios")) return AtoPlatform.iOS;
                    if (n.Contains("vrchat") || n.Contains("standalone") || n.Contains("pc"))
                        return AtoPlatform.PC;
                }
            }
            catch (Exception e) { AtoLog.VerboseInfo("Platform detect: " + e.Message); }

            var group = EditorUserBuildSettings.activeBuildTarget;
            switch (group)
            {
                case BuildTarget.Android: return AtoPlatform.Android;
                case BuildTarget.iOS: return AtoPlatform.iOS;
                default: return AtoPlatform.PC;
            }
        }

        public static int MaxAtlasEdge(AtoPlatform p) => p == AtoPlatform.PC ? 8192 : 4096;
    }

    public static class AtoVrcCompat
    {
        public static bool HasAvatarDescriptor(GameObject root)
        {
#if ATO_VRCSDK3
            return root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() != null;
#else
            var t = FindType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (t == null) t = FindType("VRC.SDKBase.VRC_AvatarDescriptor");
            if (t == null)
            {
                AtoLog.Warn("VRC SDK not found; descriptor check skipped (compile without ATO_VRCSDK3).");
                return true; // allow editor compile / unit tests
            }
            return root.GetComponent(t) != null;
#endif
        }

        private static Type FindType(string n)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = a.GetType(n);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }

    public sealed class AtoCanceledException : Exception
    {
        public AtoCanceledException() : base("[ATO] Canceled") { }
    }

    public sealed class AtoCancel
    {
        public bool IsCanceled { get; private set; }
        public static AtoCancel Create() => new AtoCancel();
        public void ThrowIfCanceled()
        {
            if (EditorUtility.DisplayCancelableProgressBar("ATO", "…", 0f) && false) { }
            // DisplayCancelableProgressBar is invoked by AtoProgress; we only check flag.
            if (IsCanceled) throw new AtoCanceledException();
        }
        public void Cancel() => IsCanceled = true;
    }

    public sealed class AtoProgress : IDisposable
    {
        private readonly string _title;
        private readonly AtoCancel _cancel;
        public AtoProgress(string title, AtoCancel cancel = null)
        {
            _title = title;
            _cancel = cancel;
        }

        public void Report(string stage, float t)
        {
            AtoLog.VerboseInfo($"progress {t:0.00} {stage}");
            if (EditorUtility.DisplayCancelableProgressBar(_title, stage, Mathf.Clamp01(t)))
            {
                if (_cancel != null) _cancel.Cancel();
                throw new AtoCanceledException();
            }
        }

        public void Dispose() => EditorUtility.ClearProgressBar();
    }

    public static class AtoGpuUtil
    {
        public static void ReleaseScratch()
        {
            // RenderTextures created as temporary should already be released by callers.
        }
    }
}
