using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Fosa.AvatarTextureOptimizer.Editor.Integration;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using nadena.dev.ndmf;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>EN: Splits only vertices requiring different atlas UVs while preserving all mesh data and blend-shape frames. ZH: 仅拆分需要不同图集 UV 的顶点，并保留全部网格数据与形态键帧。</summary>
    internal static class MeshUvRemapper
    {
        private readonly struct VertexKey : IEquatable<VertexKey>
        {
            private readonly int _original;
            private readonly string _signature;
            public VertexKey(int original, string signature) { _original = original; _signature = signature; }
            public bool Equals(VertexKey other) => _original == other._original && _signature == other._signature;
            public override bool Equals(object obj) => obj is VertexKey other && Equals(other);
            public override int GetHashCode() => (_original * 397) ^ (_signature?.GetHashCode() ?? 0);
        }

        public static void Remap(BuildContext context, BuildPlan plan, BuildProgress progress, AtoBuildReport report)
        {
            var layoutByIsland = plan.TypeGroups.SelectMany(x => x.Layouts).SelectMany(layout => layout.Islands.Select(i => (i, layout)))
                .ToDictionary(x => x.i, x => x.layout);
            for (var recordIndex = 0; recordIndex < plan.Renderers.Count; recordIndex++)
            {
                progress.Report("Remapping mesh UVs / 重新分配网格 UV", recordIndex, Math.Max(1, plan.Renderers.Count));
                var record = plan.Renderers[recordIndex];
                var groups = plan.UvGroups.Where(x => x.Renderer == record && !x.Whitelisted && x.Islands.Count > 0 && x.Islands.All(layoutByIsland.ContainsKey)).ToList();
                if (groups.Count == 0) continue;
                var output = BuildMesh(record.SourceMesh, groups, layoutByIsland, out var newToOld);
                output.name = record.SourceMesh.name + "_ATO";
                context.AssetSaver.SaveAsset(output);
                ObjectRegistry.RegisterReplacedObject(record.SourceMesh, output);
                record.WorkingMesh = output;
                if (record.Renderer is SkinnedMeshRenderer skinned)
                {
                    skinned.sharedMesh = output;
                    AaoUvCompatibility.RegisterEvacuations(plan, skinned, output, newToOld, report);
                }
                else
                {
                    var filter = record.Renderer.GetComponent<MeshFilter>();
                    if (filter != null) filter.sharedMesh = output;
                }
            }
        }

        private static Mesh BuildMesh(Mesh source, IReadOnlyList<UvGroup> groups,
            IReadOnlyDictionary<UvIsland, AtlasLayout> layoutByIsland, out List<int> newToOld)
        {
            var sourceUvs = new Dictionary<int, List<Vector4>>();
            for (var channel = 0; channel < 8; channel++)
            {
                var values = new List<Vector4>(); source.GetUVs(channel, values);
                sourceUvs[channel] = values;
            }
            var groupTriangleMaps = groups.ToDictionary(g => g,
                g => g.Islands.SelectMany(i => i.Triangles.Select(t => (t, i)))
                    .GroupBy(x => (x.t.A, x.t.B, x.t.C)).ToDictionary(x => x.Key, x => x.First().i));
            var keyToNew = new Dictionary<VertexKey, int>();
            newToOld = new List<int>();
            var uvOverrides = new Dictionary<int, Dictionary<int, Vector4>>();
            var submeshIndices = new List<int[]>();

            for (var submesh = 0; submesh < source.subMeshCount; submesh++)
            {
                var originalIndices = source.GetIndices(submesh, true);
                var topology = source.GetTopology(submesh);
                var outputIndices = new int[originalIndices.Length];
                for (var cursor = 0; cursor < originalIndices.Length; cursor++)
                {
                    var original = originalIndices[cursor];
                    var overrides = new Dictionary<int, Vector4>();
                    if (topology == MeshTopology.Triangles)
                    {
                        var triangleStart = cursor / 3 * 3;
                        if (triangleStart + 2 < originalIndices.Length)
                        {
                            var triangle = (originalIndices[triangleStart], originalIndices[triangleStart + 1], originalIndices[triangleStart + 2]);
                            foreach (var group in groups.Where(x => x.SubMesh == submesh))
                                if (groupTriangleMaps[group].TryGetValue(triangle, out var island))
                                    overrides[group.UvChannel] = RemapUv(sourceUvs[group.UvChannel][original], group, island, layoutByIsland[island]);
                        }
                    }
                    var signature = Signature(overrides);
                    var key = new VertexKey(original, signature);
                    if (!keyToNew.TryGetValue(key, out var mapped))
                    {
                        mapped = newToOld.Count; keyToNew[key] = mapped; newToOld.Add(original);
                        uvOverrides[mapped] = overrides;
                    }
                    outputIndices[cursor] = mapped;
                }
                submeshIndices.Add(outputIndices);
            }

            var output = new Mesh
            {
                indexFormat = newToOld.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                name = source.name + "_ATO",
            };
            CopyVertexData(source, output, newToOld, sourceUvs, uvOverrides);
            output.subMeshCount = source.subMeshCount;
            for (var submesh = 0; submesh < source.subMeshCount; submesh++)
                output.SetIndices(submeshIndices[submesh], source.GetTopology(submesh), submesh, false, 0);
            output.bindposes = source.bindposes;
            output.bounds = source.bounds;
            CopySkinWeights(source, output, newToOld);
            CopyBlendShapes(source, output, newToOld);
            return output;
        }

        private static Vector4 RemapUv(Vector4 original, UvGroup group, UvIsland island, AtlasLayout layout)
        {
            var normalized = new Vector2(
                (original.x + group.IntegerTranslation.x - island.UvBounds.x) / Mathf.Max(1e-8f, island.UvBounds.width),
                (original.y + group.IntegerTranslation.y - island.UvBounds.y) / Mathf.Max(1e-8f, island.UvBounds.height));
            float x, y;
            if (!island.Placement.Rotated)
            {
                x = (island.Placement.X + normalized.x * island.TargetPixelSize.x) / layout.Width;
                y = (island.Placement.Y + normalized.y * island.TargetPixelSize.y) / layout.Height;
            }
            else
            {
                x = (island.Placement.X + (1f - normalized.y) * island.TargetPixelSize.y) / layout.Width;
                y = (island.Placement.Y + normalized.x * island.TargetPixelSize.x) / layout.Height;
            }
            return new Vector4(x, y, original.z, original.w);
        }

        private static string Signature(IReadOnlyDictionary<int, Vector4> values)
        {
            if (values.Count == 0) return string.Empty;
            var builder = new StringBuilder();
            foreach (var pair in values.OrderBy(x => x.Key))
            {
                builder.Append(pair.Key).Append(':')
                    .Append(pair.Value.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(pair.Value.y.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            }
            return builder.ToString();
        }

        private static void CopyVertexData(Mesh source, Mesh output, IReadOnlyList<int> map,
            IReadOnlyDictionary<int, List<Vector4>> sourceUvs, IReadOnlyDictionary<int, Dictionary<int, Vector4>> overrides)
        {
            output.SetVertices(Duplicate(source.vertices, map));
            if (source.normals.Length == source.vertexCount) output.SetNormals(Duplicate(source.normals, map));
            if (source.tangents.Length == source.vertexCount) output.SetTangents(Duplicate(source.tangents, map));
            if (source.colors.Length == source.vertexCount) output.SetColors(Duplicate(source.colors, map));
            else if (source.colors32.Length == source.vertexCount) output.SetColors(Duplicate(source.colors32, map));
            for (var channel = 0; channel < 8; channel++)
            {
                var values = sourceUvs[channel];
                if (values.Count != source.vertexCount && !overrides.Values.Any(x => x.ContainsKey(channel))) continue;
                var duplicated = new List<Vector4>(map.Count);
                for (var i = 0; i < map.Count; i++)
                {
                    var value = values.Count == source.vertexCount ? values[map[i]] : Vector4.zero;
                    if (overrides[i].TryGetValue(channel, out var replacement)) value = replacement;
                    duplicated.Add(value);
                }
                output.SetUVs(channel, duplicated);
            }
        }

        private static List<T> Duplicate<T>(IReadOnlyList<T> source, IReadOnlyList<int> map)
        {
            var output = new List<T>(map.Count); foreach (var original in map) output.Add(source[original]); return output;
        }

        private static void CopySkinWeights(Mesh source, Mesh output, IReadOnlyList<int> map)
        {
            var sourceCounts = source.GetBonesPerVertex();
            var sourceWeights = source.GetAllBoneWeights();
            {
                // EN: Unity returns Allocator.None views; they must not be disposed by the caller.
                // ZH: Unity 返回 Allocator.None 视图；调用方不得 Dispose。
                if (sourceCounts.Length != source.vertexCount) return;
                var offsets = new int[source.vertexCount + 1];
                for (var i = 0; i < source.vertexCount; i++) offsets[i + 1] = offsets[i] + sourceCounts[i];
                var total = map.Sum(x => (int)sourceCounts[x]);
                using (var counts = new NativeArray<byte>(map.Count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
                using (var weights = new NativeArray<BoneWeight1>(total, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
                {
                    var cursor = 0;
                    for (var i = 0; i < map.Count; i++)
                    {
                        var original = map[i]; counts[i] = sourceCounts[original];
                        for (var w = offsets[original]; w < offsets[original + 1]; w++) weights[cursor++] = sourceWeights[w];
                    }
                    output.SetBoneWeights(counts, weights);
                }
            }
        }

        private static void CopyBlendShapes(Mesh source, Mesh output, IReadOnlyList<int> map)
        {
            if (source.blendShapeCount == 0) return;
            var dv = new Vector3[source.vertexCount]; var dn = new Vector3[source.vertexCount]; var dt = new Vector3[source.vertexCount];
            for (var shape = 0; shape < source.blendShapeCount; shape++)
            for (var frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
            {
                source.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
                output.AddBlendShapeFrame(source.GetBlendShapeName(shape), source.GetBlendShapeFrameWeight(shape, frame),
                    Duplicate(dv, map).ToArray(), Duplicate(dn, map).ToArray(), Duplicate(dt, map).ToArray());
            }
        }
    }
}
