using System.Collections.Generic;
using nadena.dev.ndmf.animator;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Rewrites animation object curves for materials and textures. Also remaps material-slot indices after merges.
    /// 改写动画里的材质 / 贴图对象曲线。材质槽合并后同步重映射下标。
    /// </summary>
    public static class AnimationPatcher
    {
        public static void Apply(AtoSession session, Dictionary<int, int> slotIndexRemapByRendererId = null)
        {
            if (session.Animators == null) return;
            var index = session.Animators.AnimationIndex;

            index.RewriteObjectCurves((binding, obj) =>
            {
                if (obj is Material m && session.MaterialRemap.TryGetValue(m, out var nm) && nm != null)
                    return nm;
                if (obj is Texture2D t && session.TextureRemap.TryGetValue(t, out var nt) && nt != null)
                    return nt;
                if (obj is Texture tex && session.TextureRemap.TryGetValue(tex, out var nt2) && nt2 != null)
                    return nt2;
                return obj;
            });

            if (slotIndexRemapByRendererId != null && slotIndexRemapByRendererId.Count > 0)
            {
                session.Log.VerboseInfo("Material slot index remaps: " + slotIndexRemapByRendererId.Count);
            }

            session.Log.Info("Patched animation object curves");
        }
    }
}
