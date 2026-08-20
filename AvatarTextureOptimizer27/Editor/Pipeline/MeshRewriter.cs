using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class MeshRewriter
    {
        public static void Apply(List<UvGroup> groups, List<AtlasResult> atlases, AtoPlatformSettings settings)
        {
            using (AtoLog.Time("Rewrite meshes"))
            {
                var byMesh = groups.GroupBy(g => g.SourceMesh);
                foreach (var meshGroup in byMesh)
                {
                    var mesh = meshGroup.Key;
                    if (mesh == null) continue;
                    bool anyAtlas = meshGroup.Any(g => !g.Whitelisted && g.Islands.Any(i => !i.SkipAtlas) && settings.GenerateAtlas);
                    if (!anyAtlas)
                    {
                        RetargetMaterialsOnly(meshGroup, atlases);
                        continue;
                    }

                    var copy = Object.Instantiate(mesh);
                    copy.name = mesh.name + "_ATO";
                    foreach (var g in meshGroup)
                    {
                        if (g.Whitelisted) continue;
                        var uvs = new List<Vector2>();
                        copy.GetUVs(g.UvChannel, uvs);
                        if (uvs.Count == 0) continue;
                        if (g.NeedsNormalize)
                        {
                            for (int i = 0; i < uvs.Count; i++)
                                uvs[i] -= g.NormalizeOffset;
                        }
                        var atlasFor = atlases.FirstOrDefault(a => a.UvRects.Keys.Any(k => g.Islands.Contains(k)));
                        if (atlasFor == null) { copy.SetUVs(g.UvChannel, uvs); continue; }

                        var tris = copy.triangles;
                        foreach (var isl in g.Islands)
                        {
                            if (!atlasFor.UvRects.TryGetValue(isl, out var rect)) continue;
                            var used = new HashSet<int>();
                            foreach (var t in isl.TriangleIndices)
                            {
                                used.Add(tris[t * 3]);
                                used.Add(tris[t * 3 + 1]);
                                used.Add(tris[t * 3 + 2]);
                            }
                            foreach (var vi in used)
                            {
                                var uv = uvs[vi];
                                float nx = isl.Bounds01.width > 1e-8f ? (uv.x - isl.Bounds01.xMin) / isl.Bounds01.width : 0.5f;
                                float ny = isl.Bounds01.height > 1e-8f ? (uv.y - isl.Bounds01.yMin) / isl.Bounds01.height : 0.5f;
                                uvs[vi] = new Vector2(rect.x + nx * rect.width, rect.y + ny * rect.height);
                            }
                        }
                        copy.SetUVs(g.UvChannel, uvs);
                        AssignAtlasTextures(g, atlasFor, atlases);
                    }

                    foreach (var g in meshGroup)
                    {
                        if (g.SourceRenderer is SkinnedMeshRenderer smr) smr.sharedMesh = copy;
                        else
                        {
                            var mf = g.SourceRenderer != null ? g.SourceRenderer.GetComponent<MeshFilter>() : null;
                            if (mf != null) mf.sharedMesh = copy;
                        }
                    }
                }
            }
        }

        static void RetargetMaterialsOnly(IEnumerable<UvGroup> meshGroup, List<AtlasResult> atlases)
        {
            foreach (var g in meshGroup)
            {
                if (g.SourceRenderer == null) continue;
                var mats = g.SourceRenderer.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    var mat = Object.Instantiate(mats[i]);
                    mat.name = mats[i].name + "_ATO";
                    for (int t = 0; t < g.Textures.Count; t++)
                    {
                        // scale-only already replaced entries in g.Textures
                    }
                    var names = ShaderPropertyAnalyzer.Analyze(mat, out _);
                    foreach (var b in names)
                    {
                        int idx = g.Textures.FindIndex(x => x != null && b.Texture != null && (x == b.Texture || x.name.Contains(b.Texture.name)));
                        if (idx >= 0 && g.Textures[idx] != null)
                            mat.SetTexture(b.Property, g.Textures[idx]);
                    }
                    mats[i] = mat;
                }
                g.SourceRenderer.sharedMaterials = mats;
            }
        }

        static void AssignAtlasTextures(UvGroup g, AtlasResult primary, List<AtlasResult> all)
        {
            if (g.SourceRenderer == null) return;
            var mats = g.SourceRenderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                var mat = Object.Instantiate(mats[i]);
                mat.name = mats[i].name + "_ATO";
                var names = ShaderPropertyAnalyzer.Analyze(mat, out _);
                foreach (var b in names)
                {
                    var match = all.FirstOrDefault(a => a.Semantic == b.Semantic && a.TypeGroup == g.TypeGroup) ?? primary;
                    if (match != null && match.Atlas != null)
                        mat.SetTexture(b.Property, match.Atlas);
                }
                mats[i] = mat;
            }
            g.SourceRenderer.sharedMaterials = mats;
        }
    }
}
