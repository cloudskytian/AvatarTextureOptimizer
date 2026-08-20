using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// 收集阶段：遍历渲染器与材质槽，收集贴图引用，分类，ST 检查，白名单解析，贴图去重。
    /// Collection: walk renderers & material slots, gather texture refs, classify, ST check, whitelist, dedup.
    /// </summary>
    public class ATOCollector
    {
        private readonly nadena.dev.ndmf.BuildContext _ctx;
        private readonly ATOBuildData _data;
        private readonly AvatarTextureOptimizer _comp;

        public ATOCollector(nadena.dev.ndmf.BuildContext ctx, ATOBuildData data)
        {
            _ctx = ctx;
            _data = data;
            _comp = data.component;
        }

        public void Run()
        {
            using var step = ATOLogger.Step("Collect textures & materials");
            ATOLogger.Begin("stage.collect");

            // 1) 收集渲染器（跳过 EditorOnly）。Collect renderers (skip EditorOnly).
            _data.renderers.Clear();
            foreach (var r in _ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                if (IsEditorOnly(r.gameObject)) continue;
                if (r is SkinnedMeshRenderer || r is MeshRenderer)
                    _data.renderers.Add(r);
            }

            // 2) 遍历材质槽与贴图属性。Walk material slots & texture props.
            _data.allSlots.Clear();
            int index = 0;
            foreach (var renderer in _data.renderers)
            {
                var materials = renderer.sharedMaterials;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    var mat = materials[slot];
                    if (mat == null) continue;

                    var props = ATOShaderAnalyzer.GetTextureProperties(mat);
                    foreach (var p in props)
                    {
                        ATOLogger.ThrowIfCancelled();
                        var tex = mat.GetTexture(p.name) as Texture2D;
                        if (tex == null) continue;

                        var slotRec = new ATOTextureSlot
                        {
                            renderer = renderer,
                            materialSlotIndex = slot,
                            material = mat,
                            propertyName = p.name,
                            type = p.type,
                            uvChannel = DetermineUVChannel(mat, p.name),
                            texture = tex,
                            st = new Vector4(
                                mat.GetTextureScale(p.name).x, mat.GetTextureScale(p.name).y,
                                mat.GetTextureOffset(p.name).x, mat.GetTextureOffset(p.name).y),
                            isNormalMap = p.isNormalMap,
                        };

                        // 任意 ST 变换 → 白名单处理（跳过）。Any ST transform -> whitelist.
                        if (ATOShaderAnalyzer.HasSTTransform(mat, p.name))
                        {
                            _data.whitelistSet.Add(tex);
                            ATOLogger.Warn(ATOLocalization.Tr("warning.skipTransform", tex.name));
                            continue;
                        }

                        _data.allSlots.Add(slotRec);
                    }
                }

                if (++index % 8 == 0)
                    ATOLogger.Report((float)index / Mathf.Max(1, _data.renderers.Count) * 0.5f);
            }

            // 3) 构建贴图条目（去重）。Build texture entries (dedup).
            _data.entries.Clear();
            _data.entriesByTexture.Clear();
            foreach (var slot in _data.allSlots)
            {
                var tex = slot.texture;
                if (!_data.entriesByTexture.TryGetValue(tex, out var entry))
                {
                    entry = CreateEntry(tex);
                    _data.entriesByTexture[tex] = entry;
                    _data.entries.Add(entry);
                }
                entry.slots.Add(slot);
            }

            // 4) 按 (像素内容 + 导入设置) 去重。Dedup by (pixel content + import settings).
            var seen = new Dictionary<string, ATOTextureEntry>();
            foreach (var entry in _data.entries)
            {
                if (entry.whitelisted) continue; // 白名单贴图不参与去重合并（其去重结果也视作白名单）
                var key = entry.importKey + "|" + ContentHash(entry.texture);
                if (seen.TryGetValue(key, out var canonical))
                {
                    entry.canonicalOf = canonical;
                    foreach (var s in entry.slots) canonical.slots.Add(s);
                    ATOLogger.VerboseLog($"Dedup: {entry.texture.name} -> {canonical.texture.name}");
                }
                else
                {
                    seen[key] = entry;
                }
            }

            // 4.5) 收集动画切换材质里的贴图。Collect textures from animation-swapped materials.
            CollectAnimatedMaterialTextures();

            // 5) 白名单传播：白名单对象引用的贴图全部白名单；去重结果视作白名单。
            PropagateWhitelist();

            ATOLogger.Report(1f);
            ATOLogger.Info($"Collected {_data.renderers.Count} renderers, {_data.allSlots.Count} texture slots, {_data.entries.Count} textures ({CountDup()} deduplicated)");
        }

        private int CountDup()
        {
            int c = 0; foreach (var e in _data.entries) if (e.IsDuplicate) c++; return c;
        }

        private ATOTextureEntry CreateEntry(Texture2D tex)
        {
            bool whitelisted = _data.whitelistSet.Contains(tex) || IsInWhitelistHierarchy(tex);
            var entry = CreateEntryCore(tex);
            entry.whitelisted = whitelisted;
            return entry;
        }

        /// <summary>创建贴图条目（不含白名单判断，供收集器与分析器复用）。</summary>
        internal static ATOTextureEntry CreateEntryCore(Texture2D tex)
        {
            return new ATOTextureEntry
            {
                texture = tex,
                importKey = ImportKey(tex),
                whitelisted = false,
                hasAlpha = HasAlphaChannel(tex),
                sRGB = IsSRGB(tex),
                filterMode = tex.filterMode,
                mipmaps = tex.mipmapCount > 1,
                width = tex.width,
                height = tex.height,
            };
        }

        /// <summary>
        /// 收集动画切换材质（m_Materials.Array.data[i]）中的贴图。
        /// 这些材质可能在 renderer.sharedMaterials 之外，需额外收集其贴图引用。
        /// </summary>
        private void CollectAnimatedMaterialTextures()
        {
            foreach (var animator in _ctx.AvatarRootObject.GetComponentsInChildren<Animator>(true))
            {
                var controller = animator.runtimeAnimatorController;
                if (controller == null) continue;
                foreach (var clip in controller.animationClips)
                {
                    if (clip == null) continue;
                    foreach (var binding in UnityEditor.AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        if (!binding.propertyName.StartsWith("m_Materials.Array.data[")) continue;
                        int idx = ATOUtil.ParseSlotIndex(binding.propertyName);
                        var go = ATOUtil.FindAtPath(_ctx.AvatarRootObject, binding.path);
                        var renderer = go?.GetComponent<Renderer>();
                        if (renderer == null || idx < 0) continue;

                        var curve = UnityEditor.AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        if (curve == null) continue;
                        foreach (var kv in curve)
                            if (kv.value is Material mat)
                                CollectFromAnimatedMaterial(renderer, idx, mat);
                    }
                }
            }
        }

        private void CollectFromAnimatedMaterial(Renderer renderer, int slotIndex, Material mat)
        {
            var props = ATOShaderAnalyzer.GetTextureProperties(mat);
            foreach (var p in props)
            {
                ATOLogger.ThrowIfCancelled();
                var tex = mat.GetTexture(p.name) as Texture2D;
                if (tex == null) continue;

                if (ATOShaderAnalyzer.HasSTTransform(mat, p.name))
                {
                    _data.whitelistSet.Add(tex);
                    ATOLogger.Warn(ATOLocalization.Tr("warning.skipTransform", tex.name));
                    continue;
                }

                // 去重：该槽是否已有同贴图同属性。
                bool exists = false;
                foreach (var s in _data.allSlots)
                    if (s.renderer == renderer && s.materialSlotIndex == slotIndex &&
                        s.propertyName == p.name && s.texture == tex)
                    { exists = true; break; }
                if (exists) continue;

                _data.allSlots.Add(new ATOTextureSlot
                {
                    renderer = renderer,
                    materialSlotIndex = slotIndex,
                    material = mat,
                    propertyName = p.name,
                    type = p.type,
                    uvChannel = 0,
                    texture = tex,
                    st = new Vector4(mat.GetTextureScale(p.name).x, mat.GetTextureScale(p.name).y,
                                     mat.GetTextureOffset(p.name).x, mat.GetTextureOffset(p.name).y),
                    isNormalMap = p.isNormalMap,
                });
            }
        }

        private void PropagateWhitelist()
        {
            // 白名单集合里的对象直接引用的贴图 → 白名单。
            var queue = new Queue<Object>(_data.whitelistSet);
            var visited = new HashSet<Object>(_data.whitelistSet);
            while (queue.Count > 0)
            {
                var obj = queue.Dequeue();
                foreach (var tex in ReferencedTextures(obj))
                {
                    if (tex == null || visited.Contains(tex)) continue;
                    visited.Add(tex);
                    if (_data.entriesByTexture.TryGetValue(tex, out var entry))
                    {
                        entry.whitelisted = true;
                        // 去重结果也视作白名单。
                        entry.Canonical.whitelisted = true;
                    }
                }
            }
        }

        private IEnumerable<Texture2D> ReferencedTextures(Object obj)
        {
            // 用序列化对象遍历其引用（材质/动画/GameObject 等）。Traverse serialized references.
            var result = new HashSet<Texture2D>();
            if (obj is GameObject go)
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.sharedMaterials != null)
                        foreach (var m in r.sharedMaterials) CollectFromMaterial(m, result);
                }
            }
            else if (obj is Material mat)
            {
                CollectFromMaterial(mat, result);
            }
            else if (obj is Texture2D t)
            {
                result.Add(t);
            }
            else
            {
                // 动画剪辑 / 其他资产：用 SerializedObject 扫描引用。
                var so = new SerializedObject(obj);
                CollectFromSerializedObject(so, result);
            }
            return result;
        }

        private void CollectFromMaterial(Material m, HashSet<Texture2D> result)
        {
            if (m == null) return;
            var so = new SerializedObject(m);
            CollectFromSerializedObject(so, result);
        }

        private void CollectFromSerializedObject(SerializedObject so, HashSet<Texture2D> result)
        {
            var prop = so.GetIterator();
            while (prop.Next(true))
            {
                if (prop.propertyType == SerializedPropertyType.ObjectReference)
                {
                    if (prop.objectReferenceValue is Texture2D t2) result.Add(t2);
                }
            }
        }

        private bool IsInWhitelistHierarchy(Texture2D tex)
        {
            // 贴图自身或其所在资产路径的根对象在白名单里（简化：直接对象白名单，已覆盖主要情况）。
            return _data.whitelistSet.Contains(tex);
        }

        private static bool IsEditorOnly(GameObject go)
        {
            if (go.CompareTag("EditorOnly")) return true;
            var t = go.transform.parent;
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }
            return false;
        }

        private static int DetermineUVChannel(Material mat, string propName)
        {
            // 简化：默认 uv0。可通过属性上的 [UV1] 等关键字识别（lilToon 用 _xxx UV 通道属性）。
            // Simplified: default uv0. Keyword-driven channels handled conservatively here.
            return 0;
        }

        internal static string ImportKey(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                return $"{tex.width}x{tex.height}|{importer.textureFormat}|{importer.sRGBTexture}|{importer.mipmapEnabled}|{importer.wrapMode}|{importer.filterMode}|{importer.alphaSource}";
            }
            return $"{tex.width}x{tex.height}|{tex.format}|{tex.wrapMode}|{tex.filterMode}";
        }

        private static string ContentHash(Texture2D tex)
        {
            try
            {
                var raw = tex.GetRawTextureData();
                if (raw == null || raw.Length == 0) return "n/a";
                unchecked
                {
                    uint h = 2166136261u;
                    for (int i = 0; i < raw.Length; i++) h = (h ^ raw[i]) * 16777619u;
                    return h.ToString("x8");
                }
            }
            catch
            {
                return $"{tex.width}x{tex.height}:{tex.format}";
            }
        }

        internal static bool HasAlphaChannel(Texture2D tex)
        {
            switch (tex.format)
            {
                case TextureFormat.RGBA32: case TextureFormat.ARGB32: case TextureFormat.BGRA32:
                case TextureFormat.RGBAFloat: case TextureFormat.RGBAHalf:
                case TextureFormat.DXT5: case TextureFormat.BC7:
                case TextureFormat.ASTC_4x4: case TextureFormat.ASTC_6x6: case TextureFormat.ASTC_8x8:
                case TextureFormat.ASTC_12x12: case TextureFormat.ETC2_RGBA8: case TextureFormat.PVRTC_RGBA4:
                    return true;
                default: return false;
            }
        }

        internal static bool IsSRGB(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            return importer != null ? importer.sRGBTexture : true;
        }
    }
}
