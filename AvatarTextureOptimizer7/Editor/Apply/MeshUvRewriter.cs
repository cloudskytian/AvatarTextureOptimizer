using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer.API;
using UnityEngine;
using nadena.dev.ndmf;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Rewrites mesh UVs into atlas space. Does not touch tangents (rotation is forbidden when normals exist).
    /// 把网格 UV 写到图集空间。不改切线（含法线时禁止旋转）。
    /// </summary>
    public static class MeshUvRewriter
    {
        public static void Apply(AtoSession session, AtoGraph graph, AtlasPlan plan)
        {
            var byMesh = new Dictionary<Mesh, List<UvGroup>>();
            foreach (var ug in graph.UvGroups)
            {
                if (ug.Mesh == null) continue;
                if (!plan.Layouts.ContainsKey(ug)) continue;
                if (!byMesh.TryGetValue(ug.Mesh, out var list))
                {
                    list = new List<UvGroup>();
                    byMesh[ug.Mesh] = list;
                }

                list.Add(ug);
            }

            var meshMap = new Dictionary<Mesh, Mesh>();
            foreach (var kv in byMesh)
            {
                var src = kv.Key;
                var clone = Object.Instantiate(src);
                clone.name = src.name + "_ATO";
                clone.hideFlags = HideFlags.HideAndDontSave;

                foreach (var ug in kv.Value)
                {
                    if (!plan.Layouts.TryGetValue(ug, out var layout)) continue;
                    AaoBridge.CopyOriginalUvForEvacuate(session, ug.Renderer as SkinnedMeshRenderer, clone, ug.UvChannel);
                    var atlasW = 1;
                    var atlasH = 1;
                    foreach (var a in plan.Atlases)
                    {
                        if (a.TypeGroup == ug.TypeGroup)
                        {
                            atlasW = a.Width;
                            atlasH = a.Height;
                            break;
                        }
                    }

                    var uvs = new List<Vector2>(clone.vertexCount);
                    clone.GetUVs(ug.UvChannel, uvs);
                    if (uvs.Count == 0) continue;

                    var written = new bool[uvs.Count];
                    foreach (var p in layout)
                    {
                        var isl = p.Island;
                        if (isl == null) continue;
                        foreach (var vi in isl.VertexIndices)
                        {
                            if ((uint)vi >= (uint)uvs.Count || written[vi]) continue;
                            var uv = uvs[vi] - isl.Translate;
                            var lx = (uv.x - isl.MinUvNorm.x) / isl.UvWidth;
                            var ly = (uv.y - isl.MinUvNorm.y) / isl.UvHeight;
                            lx = Mathf.Clamp01(lx);
                            ly = Mathf.Clamp01(ly);
                            float ax, ay;
                            if (p.Rotated)
                            {
                                // 90 CW in island local. / 岛局部 90° 顺时针。
                                var rx = 1f - ly;
                                var ry = lx;
                                ax = (p.X + rx * p.W + 0.5f) / atlasW;
                                ay = (p.Y + ry * p.H + 0.5f) / atlasH;
                            }
                            else
                            {
                                ax = (p.X + lx * p.W + 0.5f) / atlasW;
                                ay = (p.Y + ly * p.H + 0.5f) / atlasH;
                            }

                            uvs[vi] = new Vector2(ax, ay);
                            written[vi] = true;
                        }
                    }

                    clone.SetUVs(ug.UvChannel, uvs);
                    AaoBridge.EvacuateIfNeeded(session, ug.Renderer as SkinnedMeshRenderer, ug.UvChannel, clone);
                }

                session.Track(clone);
                session.Save(clone);
                meshMap[src] = clone;
                ObjectRegistry.RegisterReplacedObject(src, clone);
            }

            foreach (var ri in graph.Renderers)
            {
                if (ri.Mesh == null || !meshMap.TryGetValue(ri.Mesh, out var nm)) continue;
                if (ri.Renderer is SkinnedMeshRenderer smr) smr.sharedMesh = nm;
                else
                {
                    var mf = ri.Renderer.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = nm;
                }
            }

            session.Log.Info("Rewrote meshes: " + meshMap.Count);
        }
    }
}
