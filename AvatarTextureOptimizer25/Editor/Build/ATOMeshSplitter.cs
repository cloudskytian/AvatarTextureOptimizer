// Avatar Texture Optimizer / 头像贴图优化器
// Mesh vertex-splitting utility: duplicates vertices (all channels + skin
// weights + blendshape frames) so rewritten UVs never leak across submeshes.
// 网格顶点拆分工具：复制顶点（全部通道 + 蒙皮权重 + 形态键帧），使 UV 重写
// 绝不跨子网格泄漏。
//
// Implementation note: vertices are appended at the end; the triangle lists of
// the affected submeshes are redirected to the duplicates; blendshape frame
// delta arrays are extended by mirroring the source vertex's deltas.
// 实现说明：顶点追加到末尾；受影响子网格的三角形列表重定向到副本；形态键帧
// 的增量数组通过镜像源顶点增量来扩展。

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// In-memory mesh editor that can append duplicate vertices.
    /// 可追加重叠顶点的内存网格编辑器。
    /// </summary>
    public sealed class ATOMeshSplitter
    {
        private List<Vector3> _verts;
        private List<Vector3> _normals;
        private List<Vector4> _tangents;
        private List<Color> _colors;
        private List<Color32> _colors32;
        private List<BoneWeight> _boneWeights;
        private readonly List<List<Vector4>> _uvs = new List<List<Vector4>>(8);
        private bool _hasNormals, _hasTangents, _hasColors, _hasColors32, _hasBoneWeights;

        private readonly List<int[]> _submeshTris = new List<int[]>();
        private Matrix4x4[] _bindposes;
        private MeshTopology[] _topologies;

        /// <summary>For every vertex index, its duplicate-source (identity for originals). / 每个顶点索引对应的源（原始顶点为它自己）。</summary>
        private readonly List<int> _dupSources = new List<int>();

        /// <summary>Vertex count captured at LoadFrom (before any duplicates). / LoadFrom 时记录的顶点数（任何副本追加之前）。</summary>
        private int _originalVertexCount;

        private struct BlendShapeFrameData
        {
            public string shapeName;
            public float weight;
            public Vector3[] dv, dn, dt;
        }
        private readonly List<BlendShapeFrameData> _blendFrames = new List<BlendShapeFrameData>();

        /// <summary>Load all streams from a mesh (call once). / 从网格读入全部数据流（调用一次）。</summary>
        public void LoadFrom(Mesh mesh)
        {
            int vc = mesh.vertexCount;
            _originalVertexCount = vc;
            _dupSources.Clear();
            _verts = new List<Vector3>(mesh.vertices);
            for (int i = 0; i < vc; i++) _dupSources.Add(i);

            _hasNormals = mesh.normals != null && mesh.normals.Length == vc;
            _normals = _hasNormals ? new List<Vector3>(mesh.normals) : null;
            _hasTangents = mesh.tangents != null && mesh.tangents.Length == vc;
            _tangents = _hasTangents ? new List<Vector4>(mesh.tangents) : null;
            _hasColors = mesh.colors != null && mesh.colors.Length == vc;
            _colors = _hasColors ? new List<Color>(mesh.colors) : null;
            _hasColors32 = mesh.colors32 != null && mesh.colors32.Length == vc;
            _colors32 = _hasColors32 ? new List<Color32>(mesh.colors32) : null;
            _hasBoneWeights = mesh.boneWeights != null && mesh.boneWeights.Length == vc;
            _boneWeights = _hasBoneWeights ? new List<BoneWeight>(mesh.boneWeights) : null;

            for (int ch = 0; ch < 8; ch++)
            {
                var uvs = new List<Vector4>();
                mesh.GetUVs(ch, uvs);
                _uvs.Add(uvs.Count == vc ? uvs : null);
            }

            _submeshTris.Clear();
            _topologies = new MeshTopology[mesh.subMeshCount];
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                _submeshTris.Add(mesh.GetTriangles(sm));
                _topologies[sm] = mesh.GetTopology(sm);
            }
            _bindposes = mesh.bindposes;

            _blendFrames.Clear();
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                var name = mesh.GetBlendShapeName(s);
                int frames = mesh.GetBlendShapeFrameCount(s);
                for (int f = 0; f < frames; f++)
                {
                    float w = mesh.GetBlendShapeFrameWeight(s, f);
                    var dv = new Vector3[vc];
                    var dn = new Vector3[vc];
                    var dt = new Vector3[vc];
                    mesh.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                    _blendFrames.Add(new BlendShapeFrameData { shapeName = name, weight = w, dv = dv, dn = dn, dt = dt });
                }
            }
        }

        /// <summary>Append a duplicate of vertex <paramref name="v"/>; returns new index. / 追加重叠顶点，返回新索引。</summary>
        public int DuplicateVertex(int v)
        {
            int newIndex = _verts.Count;
            _verts.Add(_verts[v]);
            _dupSources.Add(v);
            if (_hasNormals) _normals.Add(_normals[v]);
            if (_hasTangents) _tangents.Add(_tangents[v]);
            if (_hasColors) _colors.Add(_colors[v]);
            if (_hasColors32) _colors32.Add(_colors32[v]);
            if (_hasBoneWeights) _boneWeights.Add(_boneWeights[v]);
            for (int ch = 0; ch < 8; ch++)
            {
                var uvs = _uvs[ch];
                if (uvs == null) continue;
                uvs.Add(uvs[v]);
            }
            return newIndex;
        }

        /// <summary>Replace one submesh's triangle list. / 置换某子网格的三角形列表。</summary>
        public void SetSubmeshTriangles(int submesh, int[] triangles)
        {
            _submeshTris[submesh] = triangles;
        }

        /// <summary>Write everything back into the given mesh (in place rebuild). / 将全部内容写回给定网格（就地重建）。</summary>
        public void WriteBack(Mesh mesh)
        {
            int newCount = _verts.Count;
            int oldCount = _originalVertexCount; // captured before any duplicates / 副本追加前的数量
            if (newCount == oldCount)
            {
                // No duplicates appended: nothing to rebuild. / 未追加副本，无需重建。
                return;
            }

            bool idx32 = newCount > 65535 || mesh.indexFormat == IndexFormat.UInt32;
            mesh.Clear(keepBlendShapes: false);

            mesh.indexFormat = idx32 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = _verts.ToArray();
            if (_hasNormals) mesh.normals = _normals.ToArray();
            if (_hasTangents) mesh.tangents = _tangents.ToArray();
            if (_hasColors) mesh.colors = _colors.ToArray();
            if (_hasColors32) mesh.colors32 = _colors32.ToArray();
            if (_hasBoneWeights) mesh.boneWeights = _boneWeights.ToArray();
            for (int ch = 0; ch < 8; ch++)
            {
                var uvs = _uvs[ch];
                if (uvs == null || uvs.Count == 0) continue;
                mesh.SetUVs(ch, uvs);
            }

            mesh.subMeshCount = _submeshTris.Count;
            for (int sm = 0; sm < _submeshTris.Count; sm++)
            {
                // Preserve the original topology (points/lines submeshes exist).
                // 保留原始拓扑（点/线子网格确实存在）。
                mesh.SetIndices(_submeshTris[sm], _topologies[sm], sm, false);
            }

            foreach (var frame in _blendFrames)
            {
                var fdv = new Vector3[newCount];
                var fdn = new Vector3[newCount];
                var fdt = new Vector3[newCount];
                for (int i = 0; i < oldCount; i++)
                {
                    fdv[i] = frame.dv[i];
                    fdn[i] = frame.dn[i];
                    fdt[i] = frame.dt[i];
                }
                for (int i = oldCount; i < newCount; i++)
                {
                    int src = _dupSources[i];
                    fdv[i] = frame.dv[src];
                    fdn[i] = frame.dn[src];
                    fdt[i] = frame.dt[src];
                }
                mesh.AddBlendShapeFrame(frame.shapeName, frame.weight, fdv, fdn, fdt);
            }

            if (_bindposes != null && _bindposes.Length > 0) mesh.bindposes = _bindposes;
            mesh.RecalculateBounds();
        }
    }
}
