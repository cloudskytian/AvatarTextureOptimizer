using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    internal sealed class MeshAtlasRemapper
    {
        private readonly AAOUvCompatibilityBridge _aaoBridge;
        public MeshAtlasRemapper(AAOUvCompatibilityBridge aaoBridge) { _aaoBridge = aaoBridge; }

        public Dictionary<Renderer, Mesh> Build(AvatarAnalysis analysis, AtlasPlan plan)
        {
            var result = new Dictionary<Renderer, Mesh>();
            try
            {
                foreach (var renderer in analysis.Renderers)
                {
                    ATOProgress.Checkpoint("Remapping mesh " + renderer.Renderer.name);
                    var groups = plan.Pages.SelectMany(page => page.Groups).Where(group => group.Renderer == renderer).ToList();
                    if (groups.Count == 0) continue;
                    Mesh clone = null;
                    try
                    {
                        clone = Remap(renderer.Mesh, renderer, groups, plan, _aaoBridge);
                        clone.name = "ATO_" + renderer.Mesh.name;
                        result[renderer.Renderer] = clone; clone = null;
                    }
                    finally
                    {
                        if (clone != null && !UnityEditor.EditorUtility.IsPersistent(clone))
                            UnityEngine.Object.DestroyImmediate(clone);
                    }
                }
                return result;
            }
            catch
            {
                DestroyTransient(result.Values); throw;
            }
        }

        internal static void DestroyTransient(IEnumerable<Mesh> meshes)
        {
            foreach (var mesh in (meshes ?? Enumerable.Empty<Mesh>()).Where(value => value != null).Distinct())
                if (!UnityEditor.EditorUtility.IsPersistent(mesh)) UnityEngine.Object.DestroyImmediate(mesh);
        }

        private static Mesh Remap(Mesh source, RendererRecord renderer, List<UvGroupRecord> groups, AtlasPlan plan,
            AAOUvCompatibilityBridge aaoBridge)
        {
            var placementCodes = new Dictionary<AtlasPlacement, int>(); var placements = new List<AtlasPlacement>();
            foreach (var page in plan.Pages) foreach (var placement in page.Placements.Where(value => value.Group.Renderer == renderer))
            { placementCodes[placement] = placements.Count; placements.Add(placement); }
            var islandPlacements = placements.ToDictionary(value => value.Island, value => value);
            var triangleMaps = new Dictionary<UvGroupRecord, Dictionary<int, AtlasPlacement>>();
            foreach (var group in groups)
            {
                var map = new Dictionary<int, AtlasPlacement>();
                foreach (var island in group.Islands) foreach (var triangle in island.TriangleIndices)
                    map[triangle] = islandPlacements[island];
                triangleMaps[group] = map;
            }

            var keys = new List<VertexKey>(); var keyToIndex = new Dictionary<VertexKey, int>();
            var submeshIndices = new List<int[]>();
            for (var submesh = 0; submesh < source.subMeshCount; submesh++)
            {
                // Unity 2022.3 defaults applyBaseVertex to true. Keep it explicit: VertexKey.SourceVertex is an
                // absolute vertex-buffer index, while the rebuilt mesh deliberately normalizes every descriptor to
                // baseVertex zero. / 显式应用 baseVertex，避免把子网格局部索引误当成绝对顶点索引。
                var indices = source.GetIndices(submesh, true); var topology = source.GetTopology(submesh);
                var slotGroups = groups.Where(value => value.Slot.Slot == submesh).ToArray();
                if (slotGroups.Length > 0 && topology != MeshTopology.Triangles) throw new InvalidOperationException("ATO topology changed after analysis.");
                for (var corner = 0; corner < indices.Length; corner++)
                {
                    if ((corner & 4095) == 0) ATOProgress.Checkpoint("Rebuilding mesh index buffers");
                    var codes = VertexKey.EmptyCodes();
                    if (slotGroups.Length > 0)
                    {
                        var triangle = corner / 3;
                        foreach (var group in slotGroups)
                        {
                            if (!triangleMaps[group].TryGetValue(triangle, out var placement))
                                throw new InvalidOperationException("ATO island-to-triangle mapping is incomplete.");
                            codes[group.UvChannel] = placementCodes[placement];
                        }
                    }
                    var key = new VertexKey(indices[corner], codes);
                    if (!keyToIndex.TryGetValue(key, out var newIndex))
                    { newIndex = keys.Count; keys.Add(key); keyToIndex.Add(key, newIndex); }
                    indices[corner] = newIndex;
                }
                submeshIndices.Add(indices);
            }

            var transformedUvs = BuildTransformedUvs(source, keys, placements, plan);
            Mesh output = null;
            try
            {
                output = CopyRawMesh(source, keys, submeshIndices, transformedUvs);
                aaoBridge?.CopyOriginalUvs(renderer.Renderer, source, output, keys.Select(value => value.SourceVertex).ToArray());
                CopySkinWeights(source, output, keys);
                CopyBlendShapes(source, output, keys);
                output.bindposes = source.bindposes; output.bounds = source.bounds;
                // Procedural meshes do not receive import-time UV distribution metrics. Recompute them after the UV
                // rewrite so mip streaming selects levels from the generated atlas coordinates rather than stale data.
                // 代码生成的 Mesh 不会自动获得导入期 UV 分布指标；改写 UV 后必须重新计算。
                output.RecalculateUVDistributionMetrics();
                var completed = output; output = null; return completed;
            }
            finally { if (output != null) UnityEngine.Object.DestroyImmediate(output); }
        }

        private static Dictionary<int, Vector4[]> BuildTransformedUvs(Mesh source, List<VertexKey> keys,
            IReadOnlyList<AtlasPlacement> placements, AtlasPlan plan)
        {
            var result = new Dictionary<int, Vector4[]>();
            for (var channel = 0; channel < 8; channel++)
            {
                if (!keys.Any(value => value.Code(channel) >= 0)) continue;
                var sourceUvs = new List<Vector4>(); source.GetUVs(channel, sourceUvs);
                if (sourceUvs.Count != source.vertexCount)
                    throw new InvalidOperationException("ATO UV stream changed after analysis.");
                var outputUvs = new Vector4[keys.Count];
                for (var vertex = 0; vertex < keys.Count; vertex++)
                {
                    var key = keys[vertex]; var uv = sourceUvs[key.SourceVertex]; var code = key.Code(channel);
                    outputUvs[vertex] = code >= 0 ? TransformUv(uv, placements[code], plan) : uv;
                }
                result[channel] = outputUvs;
            }
            return result;
        }

        private static Mesh CopyRawMesh(Mesh source, List<VertexKey> keys, List<int[]> submeshIndices,
            IReadOnlyDictionary<int, Vector4[]> transformedUvs)
        {
            var output = new Mesh(); var writeArray = default(Mesh.MeshDataArray); var allocated = false; var applied = false;
            try
            {
                using (var read = Mesh.AcquireReadOnlyMeshData(source))
                {
                    writeArray = Mesh.AllocateWritableMeshData(1); allocated = true;
                    var write = writeArray[0]; var sourceData = read[0];
                    var attributes = source.GetVertexAttributes(); write.SetVertexBufferParams(keys.Count, attributes);
                    var streamCount = attributes.Length == 0 ? 0 : attributes.Max(value => value.stream) + 1;
                    for (var stream = 0; stream < streamCount; stream++)
                    {
                        var stride = source.GetVertexBufferStride(stream);
                        var input = sourceData.GetVertexData<byte>(stream); var destination = write.GetVertexData<byte>(stream);
                        for (var vertex = 0; vertex < keys.Count; vertex++)
                            NativeArray<byte>.Copy(input, keys[vertex].SourceVertex * stride, destination, vertex * stride, stride);
                    }
                    foreach (var pair in transformedUvs)
                    {
                        var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + pair.Key);
                        var stream = source.GetVertexAttributeStream(attribute);
                        var attributeOffset = source.GetVertexAttributeOffset(attribute);
                        var format = source.GetVertexAttributeFormat(attribute);
                        var dimension = source.GetVertexAttributeDimension(attribute);
                        if (dimension < 2 || !IsWritableUvFormat(format))
                            throw new InvalidOperationException("ATO cannot preserve this UV vertex format safely: " + format);
                        var stride = source.GetVertexBufferStride(stream);
                        var destination = write.GetVertexData<byte>(stream);
                        for (var vertex = 0; vertex < pair.Value.Length; vertex++)
                        {
                            var byteOffset = vertex * stride + attributeOffset;
                            WriteUvComponent(destination, byteOffset, format, pair.Value[vertex].x);
                            WriteUvComponent(destination, byteOffset + ComponentSize(format), format, pair.Value[vertex].y);
                        }
                    }

                    var indexCount = submeshIndices.Sum(value => value.Length);
                    var indexFormat = source.indexFormat == IndexFormat.UInt32 || keys.Count > ushort.MaxValue + 1
                        ? IndexFormat.UInt32 : IndexFormat.UInt16;
                    write.SetIndexBufferParams(indexCount, indexFormat);
                    var indices32 = indexFormat == IndexFormat.UInt32
                        ? write.GetIndexData<uint>() : default(NativeArray<uint>);
                    var indices16 = indexFormat == IndexFormat.UInt16
                        ? write.GetIndexData<ushort>() : default(NativeArray<ushort>);
                    write.subMeshCount = submeshIndices.Count; var indexOffset = 0;
                    for (var submesh = 0; submesh < submeshIndices.Count; submesh++)
                    {
                        var min = int.MaxValue; var max = 0;
                        foreach (var index in submeshIndices[submesh])
                        {
                            if (indexFormat == IndexFormat.UInt32) indices32[indexOffset] = (uint)index;
                            else indices16[indexOffset] = (ushort)index;
                            indexOffset++; min = Math.Min(min, index); max = Math.Max(max, index);
                        }
                        var original = source.GetSubMesh(submesh);
                        var descriptor = new SubMeshDescriptor(indexOffset - submeshIndices[submesh].Length, submeshIndices[submesh].Length, original.topology)
                        { bounds = original.bounds, baseVertex = 0, firstVertex = min == int.MaxValue ? 0 : min,
                            vertexCount = min == int.MaxValue ? 0 : max - min + 1 };
                        write.SetSubMesh(submesh, descriptor, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                    }
                    Mesh.ApplyAndDisposeWritableMeshData(writeArray, output,
                        MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                    applied = true;
                }
                return output;
            }
            catch { UnityEngine.Object.DestroyImmediate(output); throw; }
            finally { if (allocated && !applied) writeArray.Dispose(); }
        }

        internal static bool IsWritableUvFormat(VertexAttributeFormat format)
        {
            return format == VertexAttributeFormat.Float32 || format == VertexAttributeFormat.Float16 ||
                   format == VertexAttributeFormat.UNorm8 || format == VertexAttributeFormat.SNorm8 ||
                   format == VertexAttributeFormat.UNorm16 || format == VertexAttributeFormat.SNorm16;
        }

        private static int ComponentSize(VertexAttributeFormat format)
        {
            if (format == VertexAttributeFormat.Float32) return 4;
            if (format == VertexAttributeFormat.Float16 || format == VertexAttributeFormat.UNorm16 ||
                format == VertexAttributeFormat.SNorm16) return 2;
            return 1;
        }

        private static void WriteUvComponent(NativeArray<byte> data, int offset, VertexAttributeFormat format, float value)
        {
            if (format == VertexAttributeFormat.Float32)
            {
                WriteUInt32(data, offset, math.asuint(value)); return;
            }
            if (format == VertexAttributeFormat.Float16)
            {
                WriteUInt16(data, offset, (ushort)math.f32tof16(value)); return;
            }
            if (format == VertexAttributeFormat.UNorm8)
            {
                data[offset] = (byte)math.round(math.saturate(value) * 255f); return;
            }
            if (format == VertexAttributeFormat.SNorm8)
            {
                data[offset] = unchecked((byte)(sbyte)math.round(math.clamp(value, -1f, 1f) * 127f)); return;
            }
            if (format == VertexAttributeFormat.UNorm16)
            {
                WriteUInt16(data, offset, (ushort)math.round(math.saturate(value) * 65535f)); return;
            }
            if (format == VertexAttributeFormat.SNorm16)
            {
                WriteUInt16(data, offset, unchecked((ushort)(short)math.round(math.clamp(value, -1f, 1f) * 32767f))); return;
            }
            throw new InvalidOperationException("Unsupported UV vertex format: " + format);
        }

        private static void WriteUInt16(NativeArray<byte> data, int offset, ushort value)
        {
            data[offset] = (byte)value; data[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(NativeArray<byte> data, int offset, uint value)
        {
            data[offset] = (byte)value; data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16); data[offset + 3] = (byte)(value >> 24);
        }

        private static void CopySkinWeights(Mesh source, Mesh output, List<VertexKey> keys)
        {
            NativeArray<byte> sourceCounts = default;
            NativeArray<BoneWeight1> sourceWeights = default;
            try
            {
                // Acquire inside the guarded region: GetAllBoneWeights can throw after GetBonesPerVertex has already
                // allocated a Temp NativeArray. / 两次 Unity 分配均置于 finally 边界内，第二次失败也不泄漏第一次。
                sourceCounts = source.GetBonesPerVertex();
                sourceWeights = source.GetAllBoneWeights();
                if (sourceCounts.Length == 0) return;
                var offsets = new int[sourceCounts.Length + 1];
                for (var i = 0; i < sourceCounts.Length; i++) offsets[i + 1] = offsets[i] + sourceCounts[i];
                var outputCounts = new NativeArray<byte>(keys.Count, Allocator.Temp);
                NativeArray<BoneWeight1> outputWeights = default;
                try
                {
                    var weightCount = keys.Sum(key => sourceCounts[key.SourceVertex]);
                    outputWeights = new NativeArray<BoneWeight1>(weightCount, Allocator.Temp); var cursor = 0;
                    for (var i = 0; i < keys.Count; i++)
                    {
                        var old = keys[i].SourceVertex; outputCounts[i] = sourceCounts[old];
                        for (var weight = offsets[old]; weight < offsets[old + 1]; weight++) outputWeights[cursor++] = sourceWeights[weight];
                    }
                    output.SetBoneWeights(outputCounts, outputWeights);
                }
                finally
                {
                    if (outputCounts.IsCreated) outputCounts.Dispose();
                    if (outputWeights.IsCreated) outputWeights.Dispose();
                }
            }
            finally
            {
                if (sourceCounts.IsCreated) sourceCounts.Dispose();
                if (sourceWeights.IsCreated) sourceWeights.Dispose();
            }
        }

        private static void CopyBlendShapes(Mesh source, Mesh output, List<VertexKey> keys)
        {
            var count = source.vertexCount;
            for (var shape = 0; shape < source.blendShapeCount; shape++)
            for (var frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
            {
                ATOProgress.Checkpoint("Copying mesh blend shapes");
                var positions = new Vector3[count]; var normals = new Vector3[count]; var tangents = new Vector3[count];
                source.GetBlendShapeFrameVertices(shape, frame, positions, normals, tangents);
                var outPositions = new Vector3[keys.Count]; var outNormals = new Vector3[keys.Count]; var outTangents = new Vector3[keys.Count];
                for (var vertex = 0; vertex < keys.Count; vertex++)
                {
                    var old = keys[vertex].SourceVertex; outPositions[vertex] = positions[old];
                    outNormals[vertex] = normals[old]; outTangents[vertex] = tangents[old];
                }
                output.AddBlendShapeFrame(source.GetBlendShapeName(shape), source.GetBlendShapeFrameWeight(shape, frame),
                    outPositions, outNormals, outTangents);
            }
        }

        internal static Vector4 TransformUv(Vector4 source, AtlasPlacement placement, AtlasPlan plan)
        {
            var local = new Vector2((source.x + placement.Island.IntegerNormalization.x - placement.Island.UvBounds.xMin) /
                                    placement.Island.UvBounds.width,
                (source.y + placement.Island.IntegerNormalization.y - placement.Island.UvBounds.yMin) /
                                    placement.Island.UvBounds.height);
            if (placement.Rotated) local = new Vector2(1f - local.y, local.x);
            var page = plan.Pages.First(value => value.Id == PageFor(plan, placement));
            source.x = (placement.ContentRect.x + local.x * placement.ContentRect.width) / page.Size.x;
            source.y = (placement.ContentRect.y + local.y * placement.ContentRect.height) / page.Size.y;
            return source;
        }

        private static int PageFor(AtlasPlan plan, AtlasPlacement placement) =>
            plan.Pages.First(page => page.Placements.Contains(placement)).Id;

        private readonly struct VertexKey : IEquatable<VertexKey>
        {
            public readonly int SourceVertex;
            private readonly int _a, _b, _c, _d, _e, _f, _g, _h;
            public VertexKey(int source, int[] values)
            { SourceVertex = source; _a = values[0]; _b = values[1]; _c = values[2]; _d = values[3]; _e = values[4]; _f = values[5]; _g = values[6]; _h = values[7]; }
            public static int[] EmptyCodes() => new[] { -1, -1, -1, -1, -1, -1, -1, -1 };
            public int Code(int channel)
            {
                switch (channel) { case 0: return _a; case 1: return _b; case 2: return _c; case 3: return _d;
                    case 4: return _e; case 5: return _f; case 6: return _g; default: return _h; }
            }
            public bool Equals(VertexKey other) => SourceVertex == other.SourceVertex && _a == other._a && _b == other._b &&
                _c == other._c && _d == other._d && _e == other._e && _f == other._f && _g == other._g && _h == other._h;
            public override bool Equals(object obj) => obj is VertexKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { var hash = SourceVertex; for (var i = 0; i < 8; i++) hash = hash * 397 ^ Code(i); return hash; }
            }
        }
    }
}
