// English: Public extension hooks for advanced users / third-party developers.
// 中文：给高级用户与第三方开发者的扩展钩子。
using System;
using System.Collections.Generic;
using net.fosa.ato;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Register via [InitializeOnLoad] + AtoHooks.Register. / 通过 InitializeOnLoad 注册。</summary>
    public static class AtoHooks
    {
        public static event Action<AtoPipelineContext> BeforeAnalyze;
        public static event Action<AtoPipelineContext> AfterAnalyze;
        public static event Action<AtoPipelineContext> BeforePack;
        public static event Action<AtoPipelineContext> AfterApply;
        public static event Func<Material, string, AtoTextureClass?> ClassifyProperty;
        public static event Func<Texture2D, bool?> ExtraWhitelist;

        public static AtoTextureClass? TryClassify(Material m, string prop)
        {
            if (ClassifyProperty == null) return null;
            foreach (Func<Material, string, AtoTextureClass?> d in ClassifyProperty.GetInvocationList())
            {
                try
                {
                    var r = d(m, prop);
                    if (r.HasValue) return r;
                }
                catch (Exception e) { AtoLog.Warn("Classify hook: " + e.Message); }
            }
            return null;
        }

        public static bool? TryExtraWhitelist(Texture2D t)
        {
            if (ExtraWhitelist == null) return null;
            foreach (Func<Texture2D, bool?> d in ExtraWhitelist.GetInvocationList())
            {
                try
                {
                    var r = d(t);
                    if (r.HasValue) return r;
                }
                catch (Exception e) { AtoLog.Warn("Whitelist hook: " + e.Message); }
            }
            return null;
        }

        internal static void RaiseBeforeAnalyze(AtoPipelineContext c) => Safe(BeforeAnalyze, c);
        internal static void RaiseAfterAnalyze(AtoPipelineContext c) => Safe(AfterAnalyze, c);
        internal static void RaiseBeforePack(AtoPipelineContext c) => Safe(BeforePack, c);
        internal static void RaiseAfterApply(AtoPipelineContext c) => Safe(AfterApply, c);

        private static void Safe(Action<AtoPipelineContext> ev, AtoPipelineContext c)
        {
            if (ev == null) return;
            foreach (Action<AtoPipelineContext> d in ev.GetInvocationList())
            {
                try { d(c); }
                catch (Exception e) { AtoLog.Warn("Hook: " + e.Message); }
            }
        }
    }

    public sealed class AtoPipelineContext
    {
        public GameObject AvatarRoot;
        public AtoPlatformSettings Settings;
        public AtoBakeReport Report;
        public List<AtoUvGroup> UvGroups;
        public List<AtoTypeGroup> TypeGroups;
    }
}
