using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// 分析阶段：构建 UV 组、扫描动画、提取 UV 岛、计算有效面积（形态键/缩放）、UV 越界归一。
    /// Analyze: UV groups, animation scan, island extraction, effective area (morph/scale), UV normalization.
    /// </summary>
    public class ATOAnalyzer
    {
        private readonly nadena.dev.ndmf.BuildContext _ctx;
        private readonly ATOBuildData _data;

        public ATOAnalyzer(nadena.dev.ndmf.BuildContext ctx, ATOBuildData data)
        {
            _ctx = ctx;
            _data = data;
        }

        public void Run()
        {
            using var step = ATOLogger.Step("Analyze animation & UV mapping");
            ATOLogger.Begin("stage.analyze");

            var animInfo = ScanAnimations();
            ATOLogger.Report(0.25f);

            // 把动画切换的贴图并入 UV 组对应关系（需求原文要求，记得去重）。
            IntegrateAnimatedTextures(animInfo);
            ATOLogger.Report(0.5f);

            BuildUVGroups();
            ATOLogger.Report(0.7f);

            ExtractIslands(animInfo);
            ATOLogger.Report(0.95f);

            ATOLogger.Report(1f);
            ATOLogger.Info($"UV groups: {_data.uvGroups.Count}, islands: {_data.allIslands.Count}");
        }

        // ---- UV 组 ----
        private void BuildUVGroups()
        {
            _data.uvGroups.Clear();
            foreach (var slot in _data.allSlots)
            {
                var key = $"{slot.renderer.GetInstanceID()}:{slot.materialSlotIndex}:{slot.uvChannel}";
                if (!_data.uvGroups.TryGetValue(key, out var group))
                {
                    group = new ATOUVGroup
                    {
                        renderer = slot.renderer,
                        materialSlotIndex = slot.materialSlotIndex,
                        uvChannel = slot.uvChannel,
                    };
                    _data.uvGroups[key] = group;
                }
                group.slots.Add(slot);
            }
        }

        // ---- 动画扫描 ----
        public class AnimInfo
        {
            public HashSet<GameObject> animatedEnabled = new HashSet<GameObject>();
            public float maxScale = 1f;
            public bool anyRenderModeChange = false;
            public bool anyCutoffChange = false;
            public Dictionary<Renderer, Dictionary<string, List<Texture2D>>> swappedTextures =
                new Dictionary<Renderer, Dictionary<string, List<Texture2D>>>();
            // 有 ST 变换动画的属性：path → 属性名集合（"material._MainTex"）。
            public Dictionary<string, HashSet<string>> stAnimatedProps = new Dictionary<string, HashSet<string>>();
        }

        private AnimInfo ScanAnimations()
        {
            var info = new AnimInfo();
            var controllers = new HashSet<RuntimeAnimatorController>();

            foreach (var animator in _ctx.AvatarRootObject.GetComponentsInChildren<Animator>(true))
            {
                if (animator.runtimeAnimatorController != null)
                    controllers.Add(animator.runtimeAnimatorController);
            }

            foreach (var controller in controllers)
            {
                foreach (var clip in controller.animationClips)
                {
                    if (clip == null) continue;
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    {
                        ATOLogger.ThrowIfCancelled();
                        var path = binding.path;
                        var prop = binding.propertyName;

                        // 启用/禁用。m_IsActive.
                        if (prop == "m_IsActive")
                        {
                            var curve = AnimationUtility.GetEditorCurve(clip, binding);
                            if (CurveHasNonZero(curve))
                            {
                                var go = FindAtPath(_ctx.AvatarRootObject, path);
                                if (go != null) info.animatedEnabled.Add(go);
                            }
                            continue;
                        }

                        // 缩放。m_LocalScale.
                        if (prop.StartsWith("m_LocalScale"))
                        {
                            var curve = AnimationUtility.GetEditorCurve(clip, binding);
                            if (curve != null && curve.keys.Length > 0)
                            {
                                foreach (var k in curve.keys) info.maxScale = Mathf.Max(info.maxScale, Mathf.Abs(k.value));
                            }
                            continue;
                        }

                        // 渲染模式 / Cutoff 修改。material._Cutoff / _ZWrite / _Cull etc.
                        if (prop.StartsWith("material.") && prop.Contains("_Cutoff")) info.anyCutoffChange = true;
                        if (prop.StartsWith("material.") &&
                            (prop.Contains("_ZWrite") || prop.Contains("_Cull") || prop.Contains("_Blend") || prop.Contains("_SrcBlend") || prop.Contains("_DstBlend")))
                            info.anyRenderModeChange = true;

                        // ST 变换动画：绑定名形如 material.<prop>_ST.x/.y/.z/.w，
                        // 曲线偏离默认值（scale=1 / offset=0）→ 该贴图白名单。
                        if (prop.StartsWith("material.") && prop.Contains("_ST"))
                        {
                            var curve = AnimationUtility.GetEditorCurve(clip, binding);
                            if (STCurveNonIdentity(prop, curve))
                            {
                                int stIdx = prop.IndexOf("_ST");
                                var texProp = stIdx > 0 ? prop.Substring(0, stIdx) : prop; // "material._MainTex"
                                if (!info.stAnimatedProps.TryGetValue(path, out var set))
                                    info.stAnimatedProps[path] = set = new HashSet<string>();
                                set.Add(texProp);
                            }
                            continue;
                        }

                        // 贴图切换。material.<texture prop> via object reference curve.
                        if (prop.StartsWith("material."))
                        {
                            var objCurve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                            if (objCurve == null || objCurve.Length == 0) continue;
                            var target = FindAtPath(_ctx.AvatarRootObject, path);
                            var renderer = target?.GetComponent<Renderer>();
                            if (renderer == null) continue;
                            if (!info.swappedTextures.TryGetValue(renderer, out var dict))
                                info.swappedTextures[renderer] = dict = new Dictionary<string, List<Texture2D>>();
                            if (!dict.TryGetValue(prop, out var list))
                                dict[prop] = list = new List<Texture2D>();
                            foreach (var kv in objCurve)
                                if (kv.value is Texture2D t2 && !list.Contains(t2))
                                    list.Add(t2);
                        }
                    }
                }
            }
            return info;
        }

        private static bool CurveHasNonZero(AnimationCurve curve)
        {
            if (curve == null) return false;
            foreach (var k in curve.keys) if (Mathf.Abs(k.value) > 0.001f) return true;
            return false;
        }

        /// <summary>
        /// ST 曲线是否非默认值。绑定名以 .x/.y 结尾是 scale（默认 1），.z/.w 是 offset（默认 0）。
        /// </summary>
        private static bool STCurveNonIdentity(string prop, AnimationCurve curve)
        {
            if (curve == null || curve.keys.Length == 0) return false;
            bool isScale = prop.EndsWith(".x") || prop.EndsWith(".y");
            float def = isScale ? 1f : 0f;
            foreach (var k in curve.keys)
                if (Mathf.Abs(k.value - def) > 1e-4f) return true;
            return false;
        }

        /// <summary>
        /// 把动画切换的贴图并入 entries 与 UV 组对应关系（去重）。
        /// Integrate animation-swapped textures into entries & UV groups (deduplicated).
        /// </summary>
        private void IntegrateAnimatedTextures(AnimInfo info)
        {
            foreach (var kv in info.swappedTextures)
            {
                var renderer = kv.Key;
                foreach (var (propName, textures) in kv.Value)
                {
                    // propName 形如 "material._MainTex"。
                    var texProp = propName.StartsWith("material.") ? propName.Substring("material.".Length) : propName;

                    // ST 变换动画 → 白名单跳过。
                    if (info.stAnimatedProps.TryGetValue(GetPath(renderer.transform), out var stSet) && stSet.Contains(propName))
                    {
                        foreach (var t in textures) _data.whitelistSet.Add(t);
                        ATOLogger.Warn(ATOLocalization.Tr("warning.skipTransform", string.Join(",", textures)));
                        continue;
                    }

                    // 找到使用该属性名的材质槽（保守：所有匹配槽）。
                    var slotIndex = FindSlotUsingProperty(renderer, texProp);
                    var mat = slotIndex >= 0 && renderer.sharedMaterials[slotIndex] != null
                        ? renderer.sharedMaterials[slotIndex] : null;

                    var type = ATOTextureType.MainColor;
                    bool isNormal = false;
                    if (mat != null)
                    {
                        foreach (var p in ATOShaderAnalyzer.GetTextureProperties(mat))
                            if (p.name == texProp) { type = p.type; isNormal = p.isNormalMap; break; }
                    }

                    foreach (var tex in textures)
                    {
                        if (tex == null) continue;
                        if (!_data.entriesByTexture.TryGetValue(tex, out var entry))
                        {
                            entry = ATOCollector.CreateEntryCore(tex);
                            entry.whitelisted = _data.whitelistSet.Contains(tex);
                            _data.entriesByTexture[tex] = entry;
                            _data.entries.Add(entry);
                        }
                        if (entry.whitelisted) continue;

                        var slot = new ATOTextureSlot
                        {
                            renderer = renderer,
                            materialSlotIndex = slotIndex,
                            material = mat,
                            propertyName = texProp,
                            type = type,
                            uvChannel = 0,
                            texture = tex,
                            st = Vector4.zero,
                            isNormalMap = isNormal,
                        };
                        _data.allSlots.Add(slot);
                        entry.slots.Add(slot);
                    }
                }
            }
        }

        private int FindSlotUsingProperty(Renderer renderer, string texProp)
        {
            var mats = renderer.sharedMaterials;
            if (mats == null) return 0;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                if (m.HasProperty(texProp)) return i;
            }
            return 0;
        }

        private string GetPath(Transform t)
        {
            if (t == _ctx.AvatarRootTransform) return "";
            var parts = new List<string>();
            while (t != _ctx.AvatarRootTransform && t != null)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }

        private static GameObject FindAtPath(GameObject root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            var t = root.transform.Find(path);
            return t != null ? t.gameObject : null;
        }

        // ---- 岛提取 ----
        private void ExtractIslands(AnimInfo animInfo)
        {
            _data.allIslands.Clear();
            foreach (var group in _data.uvGroups.Values)
            {
                if (group.slots.Count == 0) continue;

                var renderer = group.renderer;
                Mesh mesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                          : renderer is MeshRenderer mr ? mr.GetComponent<MeshFilter>()?.sharedMesh
                          : null;
                if (mesh == null) continue;

                // 跳过永久禁用且无动画启用的渲染器。
                bool animatedEnabled = animInfo.animatedEnabled.Contains(renderer.gameObject)
                                       || renderer.transform.GetComponentsInParent<Animator>(true).Length > 0;
                if (!renderer.enabled && !animatedEnabled) continue;

                foreach (var slot in group.slots)
                {
                    var entry = _data.entriesByTexture[slot.texture];
                    if (entry.whitelisted) continue; // 白名单贴图不处理

                    var islands = ExtractIslandsFor(mesh, slot, group, entry, animInfo.maxScale);
                    _data.allIslands.AddRange(islands);
                }
            }
        }

        private List<ATOIsland> ExtractIslandsFor(Mesh mesh, ATOTextureSlot slot, ATOUVGroup group,
            ATOTextureEntry entry, float maxScale)
        {
            var result = new List<ATOIsland>();
            int uvChannel = slot.uvChannel;
            if (uvChannel >= 8) return result;

            var uv = new List<Vector2>();
            mesh.GetUVs(uvChannel, uv);
            if (uv.Count == 0) return result;

            var tris = mesh.triangles;
            int triCount = tris.Length / 3;

            // 用 UV 位置（量化）做三角形连通性，识别 UV 岛。
            var pointToTris = new Dictionary<Vector2, List<int>>(Vector2Comparer.Instance);
            for (int t = 0; t < triCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int vi = tris[t * 3 + k];
                    if (vi >= uv.Count) continue;
                    var p = Quantize(uv[vi]);
                    if (!pointToTris.TryGetValue(p, out var list))
                        pointToTris[p] = list = new List<int>();
                    list.Add(t);
                }
            }

            var visited = new bool[triCount];
            for (int t = 0; t < triCount; t++)
            {
                if (visited[t]) continue;
                // BFS 连通分量。
                var comp = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(t); visited[t] = true;
                while (queue.Count > 0)
                {
                    var cur = queue.Dequeue();
                    comp.Add(cur);
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = tris[cur * 3 + k];
                        if (vi >= uv.Count) continue;
                        var p = Quantize(uv[vi]);
                        if (pointToTris.TryGetValue(p, out var neighbors))
                            foreach (var nb in neighbors)
                                if (!visited[nb]) { visited[nb] = true; queue.Enqueue(nb); }
                    }
                }

                var island = BuildIsland(mesh, uv, tris, comp, uvChannel, group, entry, slot, maxScale);
                if (island != null) result.Add(island);
            }
            return result;
        }

        private ATOIsland BuildIsland(Mesh mesh, List<Vector2> uv, int[] tris, List<int> comp,
            int uvChannel, ATOUVGroup group, ATOTextureEntry entry, ATOTextureSlot slot, float maxScale)
        {
            // 计算 UV 包围盒。
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var t in comp)
                for (int k = 0; k < 3; k++)
                {
                    int vi = tris[t * 3 + k];
                    if (vi >= uv.Count) continue;
                    var p = uv[vi];
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }

            var bounds = new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
            if (bounds.width <= 0 || bounds.height <= 0) return null;

            // UV 越界归一：整体平移到 [0,1]（不跨 wrap 缝）。
            // 越界但跨缝/依赖 repeat 的：白名单 + warning。
            bool normalized = false;
            if (min.x < 0 || max.x > 1 || min.y < 0 || max.y > 1)
            {
                if (bounds.width > 1.0001f || bounds.height > 1.0001f)
                {
                    // 跨 wrap 缝，无法安全归一。
                    entry.whitelisted = true;
                    ATOLogger.Warn(ATOLocalization.Tr("warning.normalizeFailed", entry.texture.name));
                    return null;
                }
                // 整体平移回 [0,1]。
                float dx = min.x < 0 ? -Mathf.Floor(min.x) : (max.x > 1 ? -(max.x - 1f) : 0f);
                float dy = min.y < 0 ? -Mathf.Floor(min.y) : (max.y > 1 ? -(max.y - 1f) : 0f);
                if (Mathf.Abs(dx) > 1e-5f || Mathf.Abs(dy) > 1e-5f)
                {
                    normalized = true;
                    for (int i = 0; i < uv.Count; i++) uv[i] = new Vector2(uv[i].x + dx, uv[i].y + dy);
                    bounds = new Rect(bounds.x + dx, bounds.y + dy, bounds.width, bounds.height);
                }
            }

            // 岛局部 UV（相对左下角）。
            var islandUv = new List<Vector2>();
            var usedVerts = new HashSet<int>();
            foreach (var t in comp)
                for (int k = 0; k < 3; k++)
                {
                    int vi = tris[t * 3 + k];
                    if (vi >= uv.Count || usedVerts.Contains(vi)) continue;
                    usedVerts.Add(vi);
                    islandUv.Add(new Vector2((uv[vi].x - bounds.x) / bounds.width,
                                             (uv[vi].y - bounds.y) / bounds.height));
                }

            var worldArea = ComputeWorldArea(mesh, tris, comp, maxScale);

            return new ATOIsland
            {
                uvGroup = group,
                texture = entry,
                mesh = mesh,
                triangles = comp.ToArray(),
                uv = islandUv.ToArray(),
                bounds = bounds,
                worldArea = worldArea,
                isSolidColor = false,
                skipScale = false,
                type = slot.type,
                isNormalMap = slot.isNormalMap,
            };
        }

        /// <summary>世界空间面积（含动画缩放；形态键取 0/100 最大值）。</summary>
        private float ComputeWorldArea(Mesh mesh, int[] tris, List<int> comp, float maxScale)
        {
            var verts = mesh.vertices;
            float area = 0f;
            foreach (var t in comp)
            {
                var a = verts[tris[t * 3 + 0]];
                var b = verts[tris[t * 3 + 1]];
                var c = verts[tris[t * 3 + 2]];
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }

            // 形态键：对每个形态键取 0/100 面积最大值。
            if (mesh.blendShapeCount > 0)
            {
                var delta = new Vector3[verts.Length];
                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    var name = mesh.GetBlendShapeName(s);
                    System.Array.Clear(delta, 0, delta.Length);
                    int fc = mesh.GetBlendShapeFrameCount(s);
                    if (fc > 0)
                    {
                        var d = new Vector3[verts.Length];
                        var dn = new Vector3[verts.Length];
                        var dt = new Vector3[verts.Length];
                        mesh.GetBlendShapeFrameVertices(s, fc - 1, d, dn, dt);
                        float shapeArea = 0f;
                        foreach (var t in comp)
                        {
                            var a = verts[tris[t * 3 + 0]] + d[tris[t * 3 + 0]];
                            var b = verts[tris[t * 3 + 1]] + d[tris[t * 3 + 1]];
                            var c = verts[tris[t * 3 + 2]] + d[tris[t * 3 + 2]];
                            shapeArea += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                        }
                        area = Mathf.Max(area, shapeArea);
                    }
                }
            }

            return area * maxScale * maxScale; // 缩放按面积平方
        }

        private static Vector2 Quantize(Vector2 p) => new Vector2(Mathf.Round(p.x * 10000f), Mathf.Round(p.y * 10000f));

        private sealed class Vector2Comparer : IEqualityComparer<Vector2>
        {
            public static readonly Vector2Comparer Instance = new Vector2Comparer();
            public bool Equals(Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;
            public int GetHashCode(Vector2 p) => p.x.GetHashCode() ^ (p.y.GetHashCode() * 397);
        }
    }
}
