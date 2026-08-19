using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Collects MeshRenderer / SkinnedMeshRenderer, skipping EditorOnly.
    /// 收集 MeshRenderer / SkinnedMeshRenderer，跳过 EditorOnly。
    /// </summary>
    internal static class ATOAvatarScanner
    {
        public static void Run(ATOContext ctx)
        {
            var root = ctx.Build.AvatarRootObject;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (IsEditorOnly(r.gameObject)) continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;

                var mesh = GetMesh(r);
                if (mesh == null)
                {
                    ctx.Log.Detail($"Skip renderer '{r.name}': no mesh");
                    continue;
                }

                var info = new ATORendererInfo
                {
                    Renderer = r,
                    Mesh = mesh,
                    IsSkinned = r is SkinnedMeshRenderer,
                    EnabledNow = r.enabled && r.gameObject.activeInHierarchy,
                    SharedMaterials = r.sharedMaterials ?? new Material[0],
                    MaxWorldScale = MaxAxis(r.transform.lossyScale)
                };
                ctx.Renderers.Add(info);
            }

            ctx.Report.RendererCount = ctx.Renderers.Count;
            ctx.Log.Info($"Renderers: {ctx.Renderers.Count}");
        }

        public static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (r is MeshRenderer)
            {
                var mf = r.GetComponent<MeshFilter>();
                return mf != null ? mf.sharedMesh : null;
            }
            return null;
        }

        public static bool IsEditorOnly(GameObject go)
        {
            var t = go.transform;
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }
            return false;
        }

        public static float MaxAxis(Vector3 s)
        {
            return Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        }
    }
}
