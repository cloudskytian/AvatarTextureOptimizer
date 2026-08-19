using System;
using System.Collections.Generic;
using NetFosa.AvatarTextureOptimizer.Editor.Analysis;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using UnityEditor;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>
    /// 动画修补器：把动画剪辑里对旧贴图/旧材质的引用替换为新的贴图/材质，
    /// 并处理材质槽索引变化（m_Materials.Array.data[i] → 合并后的索引）。
    /// </summary>
    public sealed class AnimationPatcher
    {
        private readonly ATOLogger _logger;

        // (renderer, slot, propName) → oldTexture → newTexture
        public readonly Dictionary<(Renderer, int, string), Dictionary<Texture, Texture>> TextureReplacements =
            new Dictionary<(Renderer, int, string), Dictionary<Texture, Texture>>();

        // oldMaterial → newMaterial
        public readonly Dictionary<Material, Material> MaterialReplacements = new Dictionary<Material, Material>();

        // renderer → slotIndex remap（材质槽合并后）
        public readonly Dictionary<Renderer, Dictionary<int, int>> SlotRemaps = new Dictionary<Renderer, Dictionary<int, int>>();

        public AnimationPatcher(ATOLogger logger) { _logger = logger; }

        public void AddTextureReplacement(Renderer renderer, int slot, string propName, Texture oldTex, Texture newTex)
        {
            var key = (renderer, slot, propName);
            if (!TextureReplacements.TryGetValue(key, out var map))
            {
                map = new Dictionary<Texture, Texture>();
                TextureReplacements[key] = map;
            }
            map[oldTex] = newTex;
        }

        public void AddSlotRemap(Renderer renderer, int oldSlot, int newSlot)
        {
            if (!SlotRemaps.TryGetValue(renderer, out var map))
            {
                map = new Dictionary<int, int>();
                SlotRemaps[renderer] = map;
            }
            map[oldSlot] = newSlot;
        }

        /// <summary>修补全部动画剪辑（引用替换 + 槽索引重映射）。</summary>
        public void PatchAll(IEnumerable<AnimationClip> clips)
        {
            int patchedClips = 0;
            foreach (var clip in clips)
            {
                if (clip == null) continue;
                if (PatchClip(clip)) patchedClips++;
            }
            if (patchedClips > 0)
                _logger.Info($"Animation patcher: updated {patchedClips} clip(s).");
        }

        private bool PatchClip(AnimationClip clip)
        {
            bool changed = false;
            try
            {
                var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                foreach (var binding in bindings)
                {
                    var frames = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (frames == null || frames.Length == 0) continue;

                    bool anyChange = false;
                    var newFrames = new ObjectReferenceKeyframe[frames.Length];

                    for (int i = 0; i < frames.Length; i++)
                    {
                        newFrames[i] = frames[i];
                        var value = frames[i].value;

                        if (value is Texture oldTex && TryGetTextureReplacement(binding, oldTex, out var newTex))
                        {
                            newFrames[i].value = newTex;
                            anyChange = true;
                        }
                        else if (value is Material mat && MaterialReplacements.TryGetValue(mat, out var newMat))
                        {
                            newFrames[i].value = newMat;
                            anyChange = true;
                        }
                    }

                    if (!anyChange) continue;

                    // 槽索引重映射（材质槽合并后）
                    var newBinding = binding;
                    int slot;
                    if (IsMaterialSlotBinding(binding.propertyName, out slot) && TryGetRenderer(binding, out var r))
                    {
                        if (SlotRemaps.TryGetValue(r, out var remap) && remap.TryGetValue(slot, out int ns))
                        {
                            newBinding.propertyName = ReplaceSlotIndex(binding.propertyName, slot, ns);
                        }
                    }

                    if (newBinding.propertyName != binding.propertyName)
                    {
                        AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                    }
                    AnimationUtility.SetObjectReferenceCurve(clip, newBinding, newFrames);
                    changed = true;
                }
            }
            catch (Exception e)
            {
                _logger.Warn($"Animation patcher failed on clip '{clip.name}': {e.Message}");
            }
            return changed;
        }

        private bool TryGetRenderer(EditorCurveBinding binding, out Renderer renderer)
        {
            renderer = null;
            if (string.IsNullOrEmpty(binding.path)) return false;
            if (PathCache.TryGetValue(binding.path, out var t) && t != null)
            {
                renderer = t.GetComponent<Renderer>();
                return renderer != null;
            }
            return false;
        }

        public Dictionary<string, Transform> PathCache = new Dictionary<string, Transform>();
        public GameObject Root;

        private bool TryGetTextureReplacement(EditorCurveBinding binding, Texture oldTex, out Texture newTex)
        {
            newTex = null;
            // 解析 renderer 与 slot
            if (!IsMaterialPropertyBinding(binding.propertyName, out int slot, out string propName)) return false;
            if (binding.path == null) return false;
            if (!PathCache.TryGetValue(binding.path, out var t) || t == null) return false;
            var r = t.GetComponent<Renderer>();
            if (r == null) return false;
            if (TextureReplacements.TryGetValue((r, slot, propName), out var map) && map.TryGetValue(oldTex, out newTex))
                return true;
            // 槽索引可能已变化
            if (SlotRemaps.TryGetValue(r, out var remap) && remap.TryGetValue(slot, out int ns))
            {
                if (TextureReplacements.TryGetValue((r, ns, propName), out var map2) && map2.TryGetValue(oldTex, out newTex))
                    return true;
            }
            return false;
        }

        internal static bool IsMaterialSlotBinding(string propertyName, out int slotIndex)
        {
            slotIndex = -1;
            const string prefix = "m_Materials.Array.data[";
            if (propertyName.StartsWith(prefix, StringComparison.Ordinal))
            {
                int s = prefix.Length;
                int e = propertyName.IndexOf(']', s);
                if (e > s && int.TryParse(propertyName.Substring(s, e - s), out slotIndex)) return true;
            }
            return false;
        }

        internal static bool IsMaterialPropertyBinding(string propertyName, out int slotIndex, out string propName)
        {
            slotIndex = -1;
            propName = null;
            const string prefix = "m_Materials.Array.data[";
            if (propertyName.StartsWith(prefix, StringComparison.Ordinal))
            {
                int s = prefix.Length;
                int e = propertyName.IndexOf(']', s);
                if (e > s && int.TryParse(propertyName.Substring(s, e - s), out slotIndex))
                {
                    if (e + 1 < propertyName.Length && propertyName[e + 1] == '.')
                    {
                        propName = propertyName.Substring(e + 2);
                        return true;
                    }
                }
            }
            return false;
        }

        internal static string ReplaceSlotIndex(string propertyName, int oldSlot, int newSlot)
        {
            const string prefix = "m_Materials.Array.data[";
            if (propertyName.StartsWith(prefix, StringComparison.Ordinal))
            {
                int s = prefix.Length;
                int e = propertyName.IndexOf(']', s);
                if (e > s)
                {
                    return prefix + newSlot + propertyName.Substring(e);
                }
            }
            return propertyName;
        }
    }
}
