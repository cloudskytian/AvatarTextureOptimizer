// AvatarTextureOptimizer - AvatarScanner
// EN: Collects renderers eligible for optimization (enabled or animated-enabled, not EditorOnly, not whitelisted).
// CN: 收集可优化的渲染器（启用或动画启用、非 EditorOnly、非白名单）。
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// EN: Scans the avatar hierarchy and decides which renderers participate.
    /// CN: 扫描 Avatar 层级并决定哪些渲染器参与优化。
    /// </summary>
    public static class AvatarScanner
    {
        /// <summary>
        /// EN: Collects renderers & whitelist sets. Returns renderers that are enabled or animated-enabled.
        /// CN: 收集渲染器与白名单集合。返回启用或动画启用的渲染器。
        /// </summary>
        public static List<Renderer> Scan(GameObject root, AvatarTextureOptimizer component,
            AnimationData anim, AtoBuildState state)
        {
            state.WhitelistObjects.Clear();
            foreach (var o in component.whitelist)
                if (o != null) state.WhitelistObjects.Add(o);

            var result = new List<Renderer>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (IsEditorOnly(r)) continue;
                bool rendererWhitelisted = IsWhitelisted(r, state.WhitelistObjects);
                bool enabled = r.enabled || (anim != null && anim.animatedEnabled.Contains(r));
                if (!enabled)
                {
                    AtoLog.Detail($"Renderer {r.name} disabled & never animated -> skipped");
                    continue;
                }
                if (rendererWhitelisted)
                {
                    AtoLog.Detail($"Renderer {r.name} whitelisted");
                }
                result.Add(r);
            }
            return result;
        }

        /// <summary>EN: True when the object (or an ancestor) is EditorOnly. / CN: 对象或其祖先为 EditorOnly。</summary>
        public static bool IsEditorOnly(Component c)
        {
            var t = c.transform;
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// EN: Whitelist membership: the object itself or any ancestor/descendant-related entry in the list.
        /// CN: 白名单判定：对象本身、祖先或相关对象在白名单内。
        /// </summary>
        public static bool IsWhitelisted(UnityEngine.Object obj, HashSet<UnityEngine.Object> whitelist)
        {
            if (obj == null) return false;
            if (whitelist.Contains(obj)) return true;
            if (obj is Component comp)
            {
                var t = comp.transform;
                while (t != null)
                {
                    if (whitelist.Contains(t.gameObject)) return true;
                    t = t.parent;
                }
            }
            else if (obj is GameObject go)
            {
                var t = go.transform;
                while (t != null)
                {
                    if (whitelist.Contains(t.gameObject)) return true;
                    t = t.parent;
                }
            }
            return false;
        }
    }
}
