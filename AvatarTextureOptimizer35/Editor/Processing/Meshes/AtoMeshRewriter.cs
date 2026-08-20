using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Mesh rewrite: clones the mesh, rewrites the UVs of atlased channels (translation → shrink →
    /// rotation → placement origin), applies AAO evacuation (copy original UV to a saved channel and
    /// register it), and swaps the renderer's mesh. Nothing else is modified (vertices, bones,
    /// weights, normals, tangents, blend shapes are untouched). /
    /// 网格重写：克隆网格，重写图集化通道的 UV（平移 → 缩放 → 旋转 → 放置原点），应用 AAO 疏散
    /// （把原始 UV 拷到 saved 通道并注册），替换渲染器网格。其他数据（顶点/骨骼/权重/法线/切线/形态键）不动。
    ///
    /// Rotation family (identical to the compositor's write mapping): (a,b) → (a,b) | (b,a) |
    /// (W−a,H−b) | (H−b,W−a). Content transpose + UV rotation together keep the sampled appearance
    /// exact; tangent data is never recomputed. / 旋转族（与合成器写入映射一致）：
    /// (a,b) → (a,b) | (b,a) | (W−a,H−b) | (H−b,W−a)。内容转置与 UV 旋转成对出现，采样外观保持一致；
    /// 切线数据绝不重算。
    /// </summary>
    internal static class AtoMeshRewriter
    {
        /// <summary>
        /// Rewrite one renderer's mesh. Returns the new mesh (null if nothing changed). /
        /// 重写一个渲染器的网格。返回新网格（无改动则为 null）。
        /// </summary>
        public static Mesh Rewrite(AtoContext ctx, AtoRendererData data)
        {
            // NEVER mutate the original mesh asset: work on a clone and swap only if changed. /
            // 绝不修改原始网格资产：在克隆上重写，仅在有改动时替换。
            var clone = Object.Instantiate(data.Mesh);
            clone.name = data.Mesh.name + "_ATO";

            var changed = false;
            var originalUvs = new Dictionary<int, List<Vector2>>(); // channel → original uv list. / 通道 → 原始 UV 列表。

            foreach (var kv in data.UvGroups)
            {
                var channel = kv.Key;
                var uvGroup = kv.Value;
                if (uvGroup.Whitelisted || uvGroup.AtlasSkipped) continue;
                if (uvGroup.Islands.Count == 0) continue;

                var hasPlacements = false;
                foreach (var island in uvGroup.Islands)
                {
                    if (ctx.PlacedIslands.ContainsKey(island))
                    {
                        hasPlacements = true;
                        break;
                    }
                }
                if (!hasPlacements) continue;

                var uvs = new List<Vector2>();
                clone.GetUVs(channel, uvs);
                if (uvs.Count == 0) continue;
                originalUvs[channel] = new List<Vector2>(uvs);

                foreach (var island in uvGroup.Islands)
                {
                    if (!ctx.PlacedIslands.TryGetValue(island, out var placed)) continue;

                    var t = island.NormalizationTranslation;
                    var baseMin = island.UvMin + new Vector2(t.x, t.y);
                    var uvSize = island.UvMax - island.UvMin;
                    var finalSize = island.FinalUvMax - island.FinalUvMin;
                    var sx = uvSize.x > 1e-6f ? finalSize.x / uvSize.x : 1f;
                    var sy = uvSize.y > 1e-6f ? finalSize.y / uvSize.y : 1f;

                    foreach (var vertexIndex in island.Triangles)
                    {
                        var uv = uvs[vertexIndex] + new Vector2(t.x, t.y);
                        var local = new Vector2((uv.x - baseMin.x) * sx, (uv.y - baseMin.y) * sy);
                        var rotated = Rotate(local, finalSize, placed.Rotation);
                        uvs[vertexIndex] = placed.UvOrigin + rotated;
                    }
                }

                clone.SetUVs(channel, uvs);
                changed = true;
            }

            if (!changed)
            {
                Object.DestroyImmediate(clone);
                return null;
            }

            // ---- AAO evacuation: copy original UV to the saved channel, then register ----
            if (data.Renderer is SkinnedMeshRenderer smr)
            {
                foreach (var kv in data.AaoEvacuations)
                {
                    var originalChannel = kv.Key;
                    var savedChannel = kv.Value;
                    if (originalUvs.TryGetValue(originalChannel, out var original))
                    {
                        clone.SetUVs(savedChannel, original);
                        AtoAaoIntegration.RegisterTexCoordEvacuation(smr, originalChannel, savedChannel);
                        AtoLog.Info($"[ATO] AAO evacuation: {data.Renderer.name} uv{originalChannel} -> uv{savedChannel}");
                    }
                }
            }

            // ---- swap the mesh ----
            ObjectRegistry.RegisterReplacedObject(data.Mesh, clone);

            if (data.Renderer is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                skinnedMeshRenderer.sharedMesh = clone;
            }
            else if (data.Renderer is MeshRenderer meshRenderer)
            {
                var filter = meshRenderer.GetComponent<MeshFilter>();
                if (filter != null) filter.sharedMesh = clone;
            }
            data.ResultMesh = clone;
            return clone;
        }

        /// <summary>
        /// Rotate local rect coordinates by the packing rotation (0..3). / 按装箱旋转（0..3）旋转局部矩形坐标。
        /// </summary>
        private static Vector2 Rotate(Vector2 local, Vector2 finalSize, int rotation)
        {
            switch (rotation)
            {
                case 1: return new Vector2(local.y, local.x);
                case 2: return new Vector2(finalSize.x - local.x, finalSize.y - local.y);
                case 3: return new Vector2(finalSize.y - local.y, finalSize.x - local.x);
                default: return local;
            }
        }
    }
}
