using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    internal sealed class UvIslandExtractor
    {
        public bool Extract(UvGroupRecord group, out string failure, bool alignMipBounds = false)
        {
            failure = null;
            var mesh = group.Renderer.Mesh;
            if (mesh.GetTopology(group.Slot.Slot) != MeshTopology.Triangles)
            {
                failure = "atlas remapping requires triangle topology"; return false;
            }
            var triangles = mesh.GetTriangles(group.Slot.Slot);
            var uvs = new List<Vector2>();
            mesh.GetUVs(group.UvChannel, uvs);
            if (triangles.Length % 3 != 0 || uvs.Count != mesh.vertexCount)
            {
                failure = "mesh topology or UV channel is invalid"; return false;
            }
            var triangleCount = triangles.Length / 3;
            var sets = new DisjointSet(triangleCount);
            var owners = new Dictionary<QuantizedUv, int>();
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                if ((triangle & 1023) == 0) ATOProgress.Checkpoint("Extracting UV connectivity");
                for (var corner = 0; corner < 3; corner++)
                {
                    var key = new QuantizedUv(uvs[triangles[triangle * 3 + corner]]);
                    if (owners.TryGetValue(key, out var owner)) sets.Union(triangle, owner); else owners[key] = triangle;
                }
            }

            MergeOverlappingComponents(sets, triangles, uvs, triangleCount);
            var byRoot = new Dictionary<int, UvIsland>();
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var root = sets.Find(triangle);
                if (!byRoot.TryGetValue(root, out var island)) byRoot[root] = island = new UvIsland { UvGroupId = group.Id };
                island.TriangleIndices.Add(triangle);
            }

            var islands = byRoot.Values.ToList();
            var next = 0;
            foreach (var island in islands)
            {
                island.Id = next++;
                if (!NormalizeAndMeasure(group, island, triangles, uvs, out failure)) return false;
                if (alignMipBounds && !AlignBoundsToSharedMipTexels(group, island, out failure)) return false;
                if (!ValidatePixelFootprints(group, island, out failure)) return false;
            }
            if (!TryBuildBlendShapeTriangleAreaUpperBounds(mesh, mesh.vertices, triangles,
                    out var triangleAreaUpperBounds))
            {
                failure = "blend-shape frames cannot establish a finite 0..100 surface-area bound";
                return false;
            }
            foreach (var island in islands)
                if (!MeasureSurfaceAndPixelBounds(group, island, triangleAreaUpperBounds, out failure)) return false;
            group.Islands.AddRange(islands);
            return true;
        }

        private const int SpatialGridThreshold = 256;
        private const int MaximumSpatialReferences = 1024 * 1024;
        private const int MaximumSpatialPairs = 1024 * 1024;

        private static void MergeOverlappingComponents(DisjointSet sets, int[] triangles,
            List<Vector2> uvs, int triangleCount)
        {
            var bounds = new List<TriangleUvBounds>(triangleCount);
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                if ((triangle & 255) == 0) ATOProgress.Checkpoint("Indexing UV-overlap spatial bounds");
                var first = uvs[triangles[triangle * 3]];
                var second = uvs[triangles[triangle * 3 + 1]];
                var third = uvs[triangles[triangle * 3 + 2]];
                bounds.Add(new TriangleUvBounds(triangle, first, second, third));
            }

            // A bounded uniform grid avoids the sweep path's quadratic active list on dense meshes. Pathological
            // huge triangles can reference most cells, so both references and unique candidate pairs have hard caps;
            // exceeding either cap conservatively resumes with the allocation-light sweep implementation.
            // 有界空间格加速密集网格；超大三角形导致引用或候选对超预算时，保守回退到低内存 sweep。
            if (triangleCount >= SpatialGridThreshold)
            {
                var outcome = TryMergeWithSpatialGrid(sets, bounds);
                if (outcome == SpatialGridOutcome.Completed || outcome == SpatialGridOutcome.InvalidBoundsHandled)
                    return;
            }
            MergeWithSweep(sets, bounds);
        }

        internal enum SpatialGridOutcome
        {
            Completed,
            InvalidBoundsHandled,
            ReferenceBudgetExceeded,
            CandidateBudgetExceeded
        }

        private static SpatialGridOutcome TryMergeWithSpatialGrid(DisjointSet sets,
            List<TriangleUvBounds> bounds, int? referenceBudgetOverride = null, int? pairBudgetOverride = null)
        {
            var globalMinX = float.PositiveInfinity; var globalMinY = float.PositiveInfinity;
            var globalMaxX = float.NegativeInfinity; var globalMaxY = float.NegativeInfinity;
            foreach (var value in bounds)
            {
                if (!IsFinite(value.MinX) || !IsFinite(value.MaxX) ||
                    !IsFinite(value.MinY) || !IsFinite(value.MaxY))
                    return SpatialGridOutcome.InvalidBoundsHandled; // NormalizeAndMeasure reports the precise failure.
                globalMinX = Mathf.Min(globalMinX, value.MinX); globalMaxX = Mathf.Max(globalMaxX, value.MaxX);
                globalMinY = Mathf.Min(globalMinY, value.MinY); globalMaxY = Mathf.Max(globalMaxY, value.MaxY);
            }
            var extentX = globalMaxX - globalMinX; var extentY = globalMaxY - globalMinY;
            if (!IsFinite(extentX) || !IsFinite(extentY)) return SpatialGridOutcome.InvalidBoundsHandled;
            var resolution = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(bounds.Count / 8f)), 4, 64);
            var cellsX = extentX > 1e-12f ? resolution : 1;
            var cellsY = extentY > 1e-12f ? resolution : 1;
            var cells = new List<int>[cellsX * cellsY];
            var referenceBudget = referenceBudgetOverride ?? (int)Math.Min(MaximumSpatialReferences,
                Math.Max(4096L, (long)bounds.Count * 128L));
            if (referenceBudget < 0) throw new ArgumentOutOfRangeException(nameof(referenceBudgetOverride));
            var references = 0; var indexedTriangles = 0;
            foreach (var value in bounds)
            {
                if ((indexedTriangles++ & 255) == 0)
                    ATOProgress.Checkpoint("Building UV-overlap spatial references");
                var minX = SpatialCell(value.MinX, globalMinX, extentX, cellsX);
                var maxX = SpatialCell(value.MaxX, globalMinX, extentX, cellsX);
                var minY = SpatialCell(value.MinY, globalMinY, extentY, cellsY);
                var maxY = SpatialCell(value.MaxY, globalMinY, extentY, cellsY);
                for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                {
                    if (references >= referenceBudget) return SpatialGridOutcome.ReferenceBudgetExceeded;
                    var cell = y * cellsX + x;
                    if (cells[cell] == null) cells[cell] = new List<int>();
                    cells[cell].Add(value.Triangle); references++;
                }
            }

            var pairBudget = pairBudgetOverride ?? (int)Math.Min(MaximumSpatialPairs,
                Math.Max(8192L, (long)bounds.Count * 128L));
            if (pairBudget < 0) throw new ArgumentOutOfRangeException(nameof(pairBudgetOverride));
            var visitedPairs = new HashSet<ulong>();
            var comparisons = 0;
            for (var cell = 0; cell < cells.Length; cell++)
            {
                ATOProgress.Checkpoint("Merging UV overlaps from spatial grid");
                var occupants = cells[cell];
                if (occupants == null || occupants.Count < 2) continue;
                for (var firstIndex = 0; firstIndex < occupants.Count - 1; firstIndex++)
                for (var secondIndex = firstIndex + 1; secondIndex < occupants.Count; secondIndex++)
                {
                    if ((comparisons++ & 4095) == 0)
                        ATOProgress.Checkpoint("Testing spatial UV-overlap candidates");
                    var firstTriangle = occupants[firstIndex]; var secondTriangle = occupants[secondIndex];
                    if (sets.Find(firstTriangle) == sets.Find(secondTriangle)) continue;
                    var first = bounds[firstTriangle]; var second = bounds[secondTriangle];
                    if (!BoundsOverlap(first, second)) continue;
                    var low = Math.Min(firstTriangle, secondTriangle);
                    var high = Math.Max(firstTriangle, secondTriangle);
                    var key = ((ulong)(uint)low << 32) | (uint)high;
                    if (!visitedPairs.Add(key)) continue;
                    if (visitedPairs.Count > pairBudget) return SpatialGridOutcome.CandidateBudgetExceeded;
                    if (Geometry2D.TrianglesOverlap(first.First, first.Second, first.Third,
                            second.First, second.Second, second.Third))
                        sets.Union(firstTriangle, secondTriangle);
                }
            }
            return SpatialGridOutcome.Completed;
        }

        internal static int MergeOverlappingWithSpatialBudgetsForTesting(IReadOnlyList<Vector2> triangleVertices,
            int referenceBudget, int candidateBudget, out SpatialGridOutcome outcome, out bool usedSweepFallback)
        {
            if (triangleVertices == null) throw new ArgumentNullException(nameof(triangleVertices));
            if (triangleVertices.Count == 0 || triangleVertices.Count % 3 != 0)
                throw new ArgumentException("Triangle vertices must contain one or more complete triangles.",
                    nameof(triangleVertices));
            var triangleCount = triangleVertices.Count / 3;
            var sets = new DisjointSet(triangleCount);
            var bounds = new List<TriangleUvBounds>(triangleCount);
            for (var triangle = 0; triangle < triangleCount; triangle++)
                bounds.Add(new TriangleUvBounds(triangle, triangleVertices[triangle * 3],
                    triangleVertices[triangle * 3 + 1], triangleVertices[triangle * 3 + 2]));
            outcome = TryMergeWithSpatialGrid(sets, bounds, referenceBudget, candidateBudget);
            usedSweepFallback = outcome == SpatialGridOutcome.ReferenceBudgetExceeded ||
                                outcome == SpatialGridOutcome.CandidateBudgetExceeded;
            if (usedSweepFallback) MergeWithSweep(sets, bounds);
            var roots = new HashSet<int>();
            for (var triangle = 0; triangle < triangleCount; triangle++) roots.Add(sets.Find(triangle));
            return roots.Count;
        }

        private static void MergeWithSweep(DisjointSet sets, List<TriangleUvBounds> bounds)
        {
            const float epsilon = 1e-7f;
            bounds.Sort((first, second) => first.MinX.CompareTo(second.MinX));
            var active = new List<TriangleUvBounds>();
            var visited = 0; var comparisons = 0;
            foreach (var current in bounds)
            {
                if ((visited++ & 255) == 0) ATOProgress.Checkpoint("Merging overlapping UV triangles with sweep fallback");
                for (var index = active.Count - 1; index >= 0; index--)
                    if (active[index].MaxX + epsilon < current.MinX) active.RemoveAt(index);
                foreach (var candidate in active)
                {
                    if ((comparisons++ & 4095) == 0)
                        ATOProgress.Checkpoint("Testing sweep UV-overlap candidates");
                    if (candidate.MaxY + epsilon < current.MinY || current.MaxY + epsilon < candidate.MinY ||
                        sets.Find(candidate.Triangle) == sets.Find(current.Triangle)) continue;
                    if (Geometry2D.TrianglesOverlap(candidate.First, candidate.Second, candidate.Third,
                            current.First, current.Second, current.Third))
                        sets.Union(candidate.Triangle, current.Triangle);
                }
                active.Add(current);
            }
        }

        private static int SpatialCell(float value, float minimum, float extent, int count)
        {
            if (count <= 1 || extent <= 1e-12f) return 0;
            return Mathf.Clamp(Mathf.FloorToInt((value - minimum) / extent * count), 0, count - 1);
        }

        private static bool BoundsOverlap(TriangleUvBounds first, TriangleUvBounds second)
        {
            const float epsilon = 1e-7f;
            return first.MaxX + epsilon >= second.MinX && second.MaxX + epsilon >= first.MinX &&
                   first.MaxY + epsilon >= second.MinY && second.MaxY + epsilon >= first.MinY;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private readonly struct TriangleUvBounds
        {
            public readonly int Triangle;
            public readonly Vector2 First, Second, Third;
            public readonly float MinX, MaxX, MinY, MaxY;

            public TriangleUvBounds(int triangle, Vector2 first, Vector2 second, Vector2 third)
            {
                Triangle = triangle; First = first; Second = second; Third = third;
                MinX = Mathf.Min(first.x, Mathf.Min(second.x, third.x));
                MaxX = Mathf.Max(first.x, Mathf.Max(second.x, third.x));
                MinY = Mathf.Min(first.y, Mathf.Min(second.y, third.y));
                MaxY = Mathf.Max(first.y, Mathf.Max(second.y, third.y));
            }
        }

        private static bool NormalizeAndMeasure(UvGroupRecord group, UvIsland island, int[] triangles,
            List<Vector2> uvs, out string failure)
        {
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (var triangle in island.TriangleIndices)
            for (var corner = 0; corner < 3; corner++)
            {
                var uv = uvs[triangles[triangle * 3 + corner]];
                if (float.IsNaN(uv.x) || float.IsInfinity(uv.x) || float.IsNaN(uv.y) || float.IsInfinity(uv.y)) { failure = "UV contains NaN or infinity"; return false; }
                min = Vector2.Min(min, uv); max = Vector2.Max(max, uv);
            }
            var wrapU = CommonWrapMode(group, true);
            var wrapV = CommonWrapMode(group, false);
            var shift = new Vector2(NormalizingShift(min.x, max.x, wrapU),
                NormalizingShift(min.y, max.y, wrapV));
            if (float.IsNaN(shift.x) || float.IsNaN(shift.y))
            {
                failure = "UV wrapping is mixed, mirrored, or crosses a Repeat seam and cannot be normalized safely";
                return false;
            }
            min += shift; max += shift;
            if (max.x - min.x <= 1e-7f || max.y - min.y <= 1e-7f)
            {
                failure = "UV island has a zero-width or zero-height bound"; return false;
            }
            island.IntegerNormalization = shift;
            island.UvBounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            failure = null; return true;
        }

        internal static bool AlignBoundsToSharedMipTexels(UvGroupRecord group, UvIsland island,
            out string failure)
        {
            failure = null;
            if (group == null || island == null)
            {
                failure = "UV group or island is missing"; return false;
            }
            var mipBindings = group.Bindings.Where(binding => binding != null && binding.Texture != null &&
                binding.Texture.mipmapCount > 1).ToArray();
            if (mipBindings.Length == 0) return true;

            // The packed content rectangle has integer texel dimensions. Expanding the sampling domain to a grid
            // shared by every source makes the crop footprint integral without excluding any covered UV. This keeps
            // offset-zero (and, where divisible, lower POT) LOD candidates available for ordinary fractional UV bounds.
            // 图集内容矩形只能是整数像素；向所有源贴图共享的像素网格外扩，可在不裁掉几何覆盖的前提下保留精确 LOD 候选。
            var gridX = 0; var gridY = 0;
            foreach (var binding in mipBindings)
            {
                gridX = GreatestCommonDivisor(gridX, binding.Texture.width);
                gridY = GreatestCommonDivisor(gridY, binding.Texture.height);
            }
            if (gridX <= 0 || gridY <= 0 ||
                !TryAlignAxis(island.UvBounds.xMin, island.UvBounds.xMax, gridX, out var minX, out var maxX) ||
                !TryAlignAxis(island.UvBounds.yMin, island.UvBounds.yMax, gridY, out var minY, out var maxY))
            {
                failure = "UV bounds cannot be aligned to a finite shared mip texel grid"; return false;
            }
            island.UvBounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        private static bool ValidatePixelFootprints(UvGroupRecord group, UvIsland island, out string failure)
        {
            foreach (var binding in group.Bindings)
            {
                if (binding == null || binding.Texture == null)
                {
                    failure = "UV group contains a missing texture binding"; return false;
                }
                var width = (double)island.UvBounds.width * binding.Texture.width;
                var height = (double)island.UvBounds.height * binding.Texture.height;
                if (!(width > 0.0) || !(height > 0.0) || double.IsNaN(width) || double.IsInfinity(width) ||
                    double.IsNaN(height) || double.IsInfinity(height) || width > int.MaxValue || height > int.MaxValue)
                {
                    failure = "UV sampling footprint is non-finite or exceeds supported integer dimensions";
                    return false;
                }
            }
            failure = null; return true;
        }

        private static bool TryAlignAxis(float minimum, float maximum, int grid,
            out float alignedMinimum, out float alignedMaximum)
        {
            var low = Math.Floor((double)minimum * grid) / grid;
            var high = Math.Ceiling((double)maximum * grid) / grid;
            alignedMinimum = (float)low; alignedMaximum = (float)high;
            return grid > 0 && !double.IsNaN(low) && !double.IsInfinity(low) &&
                   !double.IsNaN(high) && !double.IsInfinity(high) &&
                   !float.IsNaN(alignedMinimum) && !float.IsInfinity(alignedMinimum) &&
                   !float.IsNaN(alignedMaximum) && !float.IsInfinity(alignedMaximum) &&
                   alignedMaximum > alignedMinimum;
        }

        private static int GreatestCommonDivisor(int first, int second)
        {
            first = Math.Abs(first); second = Math.Abs(second);
            while (second != 0) { var remainder = first % second; first = second; second = remainder; }
            return first;
        }

        private static TextureWrapMode? CommonWrapMode(UvGroupRecord group, bool horizontal)
        {
            TextureWrapMode? common = null;
            foreach (var binding in group.Bindings)
            {
                var mode = horizontal ? binding.Texture.wrapModeU : binding.Texture.wrapModeV;
                if (common.HasValue && common.Value != mode) return null;
                common = mode;
            }
            return common;
        }

        internal static float NormalizingShift(float min, float max, TextureWrapMode? wrapMode)
        {
            if (!wrapMode.HasValue || float.IsNaN(min) || float.IsInfinity(min) ||
                float.IsNaN(max) || float.IsInfinity(max) || max < min) return float.NaN;
            // Clamp has no periodic seam. Keep its original, possibly out-of-range source domain; the GPU source
            // loads clamp exactly as the original sampler does, while atlas UV remapping normalizes that finite domain.
            // Clamp 没有周期接缝：保留原始越界域，由 GPU Clamp 取样，再归一到图集局部坐标。
            if (wrapMode.Value == TextureWrapMode.Clamp) return 0f;
            if (wrapMode.Value != TextureWrapMode.Repeat) return float.NaN;
            var tile = Mathf.Floor(min);
            var shift = -tile;
            // Repeat is periodic only when the complete island stays strictly before the next tile seam. The product
            // currently rejects all Repeat sources before extraction because atlas edge filtering/mips cannot yet prove
            // seam equivalence; retain this total helper for extensions and focused regression tests.
            return min + shift >= 0f && max + shift < 1f ? shift : float.NaN;
        }

        private static bool MeasureSurfaceAndPixelBounds(UvGroupRecord group, UvIsland island,
            float[] triangleAreaUpperBounds, out string failure)
        {
            var maximumArea = 0f;
            foreach (var triangle in island.TriangleIndices)
            {
                maximumArea += triangleAreaUpperBounds[triangle];
                if (!IsFinite(maximumArea))
                {
                    failure = "mesh, blend shape, or transform contains non-finite geometry";
                    return false;
                }
            }
            var scaledArea = maximumArea * group.Renderer.MaximumAreaScale;
            if (!IsFinite(scaledArea))
            {
                failure = "mesh, blend shape, or transform contains non-finite geometry";
                return false;
            }
            island.SurfaceAreaSquareMeters = scaledArea;
            var maxWidth = 1; var maxHeight = 1;
            foreach (var binding in group.Bindings)
            {
                maxWidth = Mathf.Max(maxWidth, Mathf.CeilToInt(island.UvBounds.width * binding.Texture.width));
                maxHeight = Mathf.Max(maxHeight, Mathf.CeilToInt(island.UvBounds.height * binding.Texture.height));
            }
            island.OriginalPixelBounds = new Vector2Int(maxWidth, maxHeight);
            island.TargetPixelSize = island.OriginalPixelBounds;
            failure = null;
            return true;
        }

        private static bool TryBuildBlendShapeTriangleAreaUpperBounds(Mesh mesh, Vector3[] vertices,
            int[] triangles, out float[] triangleAreaUpperBounds)
        {
            var triangleCount = triangles.Length / 3;
            triangleAreaUpperBounds = new float[triangleCount];
            var firstEdgeUpper = new float[triangleCount];
            var secondEdgeUpper = new float[triangleCount];
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var i0 = triangles[triangle * 3]; var i1 = triangles[triangle * 3 + 1];
                var i2 = triangles[triangle * 3 + 2];
                firstEdgeUpper[triangle] = (vertices[i1] - vertices[i0]).magnitude;
                secondEdgeUpper[triangle] = (vertices[i2] - vertices[i0]).magnitude;
                triangleAreaUpperBounds[triangle] = Geometry2D.TriangleArea(vertices[i0], vertices[i1], vertices[i2]);
                if (!IsFinite(firstEdgeUpper[triangle]) || !IsFinite(secondEdgeUpper[triangle]) ||
                    !IsFinite(triangleAreaUpperBounds[triangle])) return false;
            }
            if (mesh.blendShapeCount == 0) return true;

            for (var shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                ATOProgress.Checkpoint("Measuring blend-shape frame envelope");
                if (!TryLoadBlendShapeFrames(mesh, shape, vertices.Length, out var weights, out var frames))
                    return false;
                if (frames.Length == 0) continue;
                if (!TryBlendShapeAtWeight(weights, frames, 0f, vertices.Length, out var atZero) ||
                    !TryBlendShapeAtWeight(weights, frames, 100f, vertices.Length, out var atHundred)) return false;

                var states = new List<Vector3[]>(frames.Length + 2) { atZero, atHundred };
                for (var frame = 0; frame < frames.Length; frame++)
                    if (weights[frame] > 0f && weights[frame] < 100f) states.Add(frames[frame]);

                var shapeFirstMaximum = new float[triangleCount];
                var shapeSecondMaximum = new float[triangleCount];
                foreach (var delta in states)
                {
                    for (var triangle = 0; triangle < triangleCount; triangle++)
                    {
                        if ((triangle & 1023) == 0) ATOProgress.Checkpoint("Bounding blend-shape triangle edges");
                        var i0 = triangles[triangle * 3]; var i1 = triangles[triangle * 3 + 1];
                        var i2 = triangles[triangle * 3 + 2];
                        var first = (delta[i1] - delta[i0]).magnitude;
                        var second = (delta[i2] - delta[i0]).magnitude;
                        if (!IsFinite(first) || !IsFinite(second)) return false;
                        shapeFirstMaximum[triangle] = Mathf.Max(shapeFirstMaximum[triangle], first);
                        shapeSecondMaximum[triangle] = Mathf.Max(shapeSecondMaximum[triangle], second);
                    }
                }
                for (var triangle = 0; triangle < triangleCount; triangle++)
                {
                    firstEdgeUpper[triangle] += shapeFirstMaximum[triangle];
                    secondEdgeUpper[triangle] += shapeSecondMaximum[triangle];
                    if (!IsFinite(firstEdgeUpper[triangle]) || !IsFinite(secondEdgeUpper[triangle])) return false;
                }
            }

            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var combinedUpper = 0.5f * firstEdgeUpper[triangle] * secondEdgeUpper[triangle];
                if (!IsFinite(combinedUpper)) return false;
                triangleAreaUpperBounds[triangle] = Mathf.Max(triangleAreaUpperBounds[triangle], combinedUpper);
            }
            // Each frame interval is linear in its weight, and the norm of an edge delta is convex. Therefore its
            // interval maximum lies at a breakpoint. Triangle inequality then covers simultaneous 0..100 shape weights.
            // 每段形态键 delta 对权重线性，边差范数为凸函数；断点包络结合三角不等式可覆盖所有形态键同时取 0..100。
            return true;
        }

        private static bool TryLoadBlendShapeFrames(Mesh mesh, int shape, int vertexCount,
            out float[] weights, out Vector3[][] frames)
        {
            var frameCount = mesh.GetBlendShapeFrameCount(shape);
            weights = new float[frameCount]; frames = new Vector3[frameCount][];
            var previous = float.NegativeInfinity;
            for (var frame = 0; frame < frameCount; frame++)
            {
                var weight = mesh.GetBlendShapeFrameWeight(shape, frame);
                if (!IsFinite(weight) || weight <= previous) return false;
                previous = weights[frame] = weight;
                var delta = frames[frame] = new Vector3[vertexCount];
                mesh.GetBlendShapeFrameVertices(shape, frame, delta, null, null);
                for (var vertex = 0; vertex < vertexCount; vertex++)
                {
                    if ((vertex & 65535) == 0) ATOProgress.Checkpoint("Reading blend-shape frame vertices");
                    if (!IsFinite(delta[vertex])) return false;
                }
            }
            return true;
        }

        private static bool TryBlendShapeAtWeight(float[] weights, Vector3[][] frames, float target,
            int vertexCount, out Vector3[] result)
        {
            result = new Vector3[vertexCount];
            if (frames.Length == 0) return true;
            var lower = -1; var upper = -1;
            for (var frame = 0; frame < frames.Length; frame++)
            {
                if (weights[frame] == target)
                {
                    Array.Copy(frames[frame], result, vertexCount); return true;
                }
                if (weights[frame] < target) lower = frame;
                else if (upper < 0) upper = frame;
            }

            float lowerWeight; float upperWeight; Vector3[] lowerDelta; Vector3[] upperDelta;
            if (lower >= 0 && upper >= 0)
            {
                lowerWeight = weights[lower]; lowerDelta = frames[lower];
                upperWeight = weights[upper]; upperDelta = frames[upper];
            }
            else if (upper >= 0)
            {
                lowerWeight = 0f; lowerDelta = result;
                upperWeight = weights[upper]; upperDelta = frames[upper];
            }
            else if (lower > 0)
            {
                lowerWeight = weights[lower - 1]; lowerDelta = frames[lower - 1];
                upperWeight = weights[lower]; upperDelta = frames[lower];
            }
            else
            {
                if (weights[0] == 0f) return false;
                lowerWeight = 0f; lowerDelta = result;
                upperWeight = weights[0]; upperDelta = frames[0];
            }

            var denominator = upperWeight - lowerWeight;
            if (!IsFinite(denominator) || denominator == 0f) return false;
            var t = (target - lowerWeight) / denominator;
            if (!IsFinite(t)) return false;
            for (var vertex = 0; vertex < vertexCount; vertex++)
            {
                result[vertex] = Vector3.LerpUnclamped(lowerDelta[vertex], upperDelta[vertex], t);
                if (!IsFinite(result[vertex])) return false;
            }
            return true;
        }

        internal static Vector3[] BlendShapeAt100(Mesh mesh, int shape, int vertexCount)
        {
            if (!TryLoadBlendShapeFrames(mesh, shape, vertexCount, out var weights, out var frames) ||
                !TryBlendShapeAtWeight(weights, frames, 100f, vertexCount, out var result))
                throw new InvalidOperationException("Blend-shape frames cannot be evaluated safely at weight 100.");
            return result;
        }

        private static Rect Bounds(Vector2 a, Vector2 b, Vector2 c)
        {
            var min = Vector2.Min(a, Vector2.Min(b, c)); var max = Vector2.Max(a, Vector2.Max(b, c));
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private readonly struct QuantizedUv : IEquatable<QuantizedUv>
        {
            private readonly long _x; private readonly long _y;
            public QuantizedUv(Vector2 value) { _x = (long)Math.Round(value.x * 1000000.0); _y = (long)Math.Round(value.y * 1000000.0); }
            public bool Equals(QuantizedUv other) => _x == other._x && _y == other._y;
            public override bool Equals(object obj) => obj is QuantizedUv other && Equals(other);
            public override int GetHashCode() { unchecked { return (_x.GetHashCode() * 397) ^ _y.GetHashCode(); } }
        }
    }
}
