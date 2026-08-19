using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    public readonly struct AtoRendererInfo
    {
        public readonly Renderer Renderer;
        public readonly Mesh Mesh;
        public readonly bool IsSkinned;
        public readonly bool InitiallyEnabled;
        public readonly Material[] Materials;

        public AtoRendererInfo(Renderer r, Mesh mesh, bool skinned, bool enabled, Material[] mats)
        {
            Renderer = r;
            Mesh = mesh;
            IsSkinned = skinned;
            InitiallyEnabled = enabled;
            Materials = mats;
        }
    }

    public static class RendererCollector
    {
        public static List<AtoRendererInfo> Collect(GameObject avatarRoot, AnimationCollector anim, AtoLog log)
        {
            var list = new List<AtoRendererInfo>();
            if (avatarRoot == null) return list;

            var renderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (r.CompareTag("EditorOnly") || IsUnderEditorOnly(r.transform, avatarRoot.transform))
                {
                    log?.VerboseInfo("Skip EditorOnly renderer " + r.name);
                    continue;
                }

                Mesh mesh = null;
                var skinned = false;
                if (r is SkinnedMeshRenderer smr)
                {
                    mesh = smr.sharedMesh;
                    skinned = true;
                }
                else if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    mesh = mf != null ? mf.sharedMesh : null;
                }
                else
                {
                    continue;
                }

                if (mesh == null)
                {
                    log?.VerboseInfo("Skip renderer without mesh " + r.name);
                    continue;
                }

                var enabled = r.enabled && r.gameObject.activeInHierarchy;
                var animEnables = false;
                if (anim != null && anim.PerRenderer.TryGetValue(r, out var ra))
                    animEnables = ra.Enables;

                if (!enabled && !animEnables)
                {
                    log?.VerboseInfo("Skip disabled renderer (no enable animation) " + r.name);
                    continue;
                }

                var mats = r.sharedMaterials ?? System.Array.Empty<Material>();
                list.Add(new AtoRendererInfo(r, mesh, skinned, enabled, mats));
            }

            log?.Info("Eligible renderers: " + list.Count);
            return list;
        }

        public static bool IsUnderEditorOnly(Transform t, Transform root)
        {
            while (t != null && t != root)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }

            return false;
        }
    }
}
