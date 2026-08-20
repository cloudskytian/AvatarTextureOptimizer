// Avatar scanning: renderers (enabled or animation-enabled), material slots
// (static + animated variants), EditorOnly skipping, animated scale factors.
// Avatar 扫描：渲染器（启用或动画启用）、材质槽（静态+动画变体）、跳过 EditorOnly、动画缩放因子。

using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class AvatarScanner
    {
        internal static void Scan(AtoSession s)
        {
            using var _ = ATOLog.Scope("ScanAvatar");
            Transform root = s.ctx.AvatarRootTransform;
            var anim = s.anim;

            // GameObjects that animation may activate. / 可能被动画激活的物体。
            var animActive = new HashSet<string>();
            foreach (var kv in anim.goActive)
                foreach (var v in kv.Value)
                    if (v > 0.5f)
                    {
                        animActive.Add(kv.Key);
                        break;
                    }

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;

                // EditorOnly tag anywhere in hierarchy -> skip entirely. / 层级内 EditorOnly 标签跳过。
                bool editorOnly = false;
                for (Transform t = r.transform; t != null; t = t.parent)
                {
                    if (t.CompareTag("EditorOnly")) { editorOnly = true; break; }
                    if (t == root) break;
                }
                if (editorOnly) { ATOLog.DebugL($"skip EditorOnly renderer {r.name}"); continue; }

                string path = RelativePath(root, r.transform);
                // active if activeInHierarchy or any ancestor/self animated active
                // 自身或任一祖先处于激活态/被动画激活
                bool activeSelf = r.gameObject.activeInHierarchy;
                if (!activeSelf)
                    for (Transform t = r.transform; t != null && t != root.parent; t = t.parent)
                    {
                        if (animActive.Contains(RelativePath(root, t))) { activeSelf = true; break; }
                        if (t == root) break;
                    }

                anim.renderers.TryGetValue(path, out var rAnim);
                bool enabled = r.enabled;
                if (rAnim != null && rAnim.enabledAnimated && rAnim.enabledValues.Exists(v => v > 0.5f))
                    enabled = true;

                // "Only process renderers that are enabled or animation-enabled."
                // 仅处理启用或动画启用的渲染器。
                if (!activeSelf || !enabled) { ATOLog.DebugL($"skip disabled renderer {path}"); continue; }

                Mesh mesh = GetMesh(r);
                if (mesh == null) continue;

                var info = new RendererInfo
                {
                    renderer = r, mesh = mesh, skinned = r is SkinnedMeshRenderer, path = path,
                    animatedEnabled = rAnim != null && rAnim.enabledAnimated,
                };
                info.animatedScaleFactor = ComputeScaleFactor(root, r.transform, anim);

                var shared = r.sharedMaterials;
                for (int slot = 0; slot < shared.Length; slot++)
                {
                    var variants = new List<Material>();
                    if (shared[slot] != null) variants.Add(shared[slot]);
                    if (rAnim != null && rAnim.slotMaterials.TryGetValue(slot, out var swaps))
                        foreach (var o in swaps)
                            if (o is Material m && !variants.Contains(m))
                                variants.Add(m);
                    info.slotMaterials.Add(variants.ToArray());
                }

                s.renderers.Add(info);
                ATOLog.DebugL($"renderer {path}: mesh={mesh.name} slots={info.slotMaterials.Count}");
            }

            ATOLog.Info($"scan: {s.renderers.Count} renderers");
        }

        internal static Mesh GetMesh(Renderer r)
        {
            switch (r)
            {
                case SkinnedMeshRenderer smr: return smr.sharedMesh;
                case MeshRenderer mr:
                    var mf = mr.GetComponent<MeshFilter>();
                    return mf == null ? null : mf.sharedMesh;
                default: return null;
            }
        }

        /// <summary>Per-axis max scale multiplier from animated ancestors (>=1, incl. base pose).
        /// 祖先链动画缩放的逐轴最大倍率（含基础姿态，>=1）。</summary>
        internal static Vector3 ComputeScaleFactor(Transform root, Transform t, AnimationData anim)
        {
            Vector3 factor = Vector3.one;
            for (Transform cur = t; cur != null && cur != root.parent; cur = cur.parent)
            {
                Vector3 local = cur.localScale;
                Vector3 animMax = local;
                if (cur != root && anim != null && anim.localScale.TryGetValue(RelativePath(root, cur), out var maxes))
                {
                    animMax = new Vector3(
                        Mathf.Max(Mathf.Abs(local.x), maxes[0]),
                        Mathf.Max(Mathf.Abs(local.y), maxes[1]),
                        Mathf.Max(Mathf.Abs(local.z), maxes[2]));
                }
                else animMax = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));

                // relative to edit-time base: >= 1 / 相对编辑期基准，>=1
                factor = new Vector3(
                    factor.x * (local.x != 0 ? animMax.x / Mathf.Abs(local.x) : 1f),
                    factor.y * (local.y != 0 ? animMax.y / Mathf.Abs(local.y) : 1f),
                    factor.z * (local.z != 0 ? animMax.z / Mathf.Abs(local.z) : 1f));
                if (cur == root) break;
            }

            return factor;
        }

        internal static string RelativePath(Transform root, Transform t)
        {
            if (t == root) return "";
            string path = t.name;
            while (t.parent != null && t.parent != root)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }
}
