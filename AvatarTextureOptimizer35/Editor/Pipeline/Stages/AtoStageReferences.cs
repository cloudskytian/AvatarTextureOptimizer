using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Stage: apply all object remaps. / 阶段：应用全部对象重映射。
    ///
    /// 1. Clones materials whose texture properties must point at atlases (the user's original
    ///    material assets are NEVER modified; direct animation targets are excluded from cloning
    ///    and their textures were whitelisted earlier). / 克隆需要指向图集的材质（绝不修改用户原始材质
    ///    资产；直接动画目标不克隆，其贴图此前已白名单）。
    /// 2. Updates renderer material slots. / 更新渲染器材质槽。
    /// 3. Remaps animation curve references (object reference curves + material slot indices),
    ///    only for editable clips. / 重映射动画曲线引用（对象引用曲线 + 材质槽索引），仅限可编辑剪辑。
    /// </summary>
    internal sealed class AtoStageReferences : IAtoStage
    {
        public string I18nKey => "references";

        public void Run(AtoContext ctx)
        {
            // ---- 1. clone materials that need texture remaps ----
            var materials = new HashSet<Material>();
            foreach (var data in ctx.Renderers)
            {
                foreach (var slot in data.Slots)
                {
                    foreach (var material in slot.AnimatedOptions)
                    {
                        if (material != null) materials.Add(material);
                    }
                }
            }

            var clones = new Dictionary<Material, Material>();
            foreach (var material in materials)
            {
                if (ctx.WhitelistObjects.Contains(material)) continue;
                if (clones.ContainsKey(material) || ctx.Remapper.Has(material)) continue;

                var shader = material.shader;
                if (shader == null) continue;

                var needsClone = false;
                for (var i = 0; i < shader.GetPropertyCount(); i++)
                {
                    if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    if (material.GetTexture(shader.GetPropertyName(i)) is Texture2D texture &&
                        ctx.Remapper.Has(texture))
                    {
                        needsClone = true;
                        break;
                    }
                }
                if (!needsClone) continue;

                // Clone with all properties copied; only texture references change. /
                // 克隆并复制全部属性；仅贴图引用变化。
                var clone = new Material(material) { name = material.name + "_ATO" };
                for (var i = 0; i < shader.GetPropertyCount(); i++)
                {
                    if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    var propertyName = shader.GetPropertyName(i);
                    if (material.GetTexture(propertyName) is Texture2D texture)
                    {
                        clone.SetTexture(propertyName, (Texture)ctx.Remapper.Resolve(texture));
                    }
                }
                clones[material] = clone;
                ctx.Remapper.Register(material, clone);
                ObjectRegistry.RegisterReplacedObject(material, clone);
                AtoLog.Verbose($"[ATO] material cloned for texture remap: {material.name} -> {clone.name}");
            }
            AtoLog.Info($"[ATO] references: {clones.Count} material(s) cloned for atlas references.");

            // ---- 2. renderer material slots ----
            foreach (var data in ctx.Renderers)
            {
                var shared = data.Renderer.sharedMaterials;
                var changed = false;
                for (var i = 0; i < shared.Length; i++)
                {
                    var resolved = (Material)ctx.Remapper.Resolve(shared[i]);
                    if (resolved != shared[i])
                    {
                        shared[i] = resolved;
                        changed = true;
                    }
                }
                if (changed) data.Renderer.sharedMaterials = shared;
            }

            // ---- 3. animation curve remaps (editable clips only) ----
            var containerPath = ctx.Ndmf.AssetContainer != null
                ? AssetDatabase.GetAssetPath(ctx.Ndmf.AssetContainer)
                : "";
            var remappedCount = 0;

            foreach (var clip in ctx.Animations.Clips)
            {
                ctx.State.ThrowIfCancelled();
                var path = AssetDatabase.GetAssetPath(clip);
                if (!string.IsNullOrEmpty(path) &&
                    (string.IsNullOrEmpty(containerPath) || !path.StartsWith(containerPath + "/")))
                {
                    continue; // readonly (not cloned): skipped. / 只读（未被克隆）：跳过。
                }

                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var refs = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (refs == null || refs.Length == 0) continue;

                    var newBinding = binding;
                    var bindingChanged = false;

                    // Material slot index remap after slot merging. / 槽合并后的材质槽索引重映射。
                    var slotIndex = ParseSlotIndex(binding.propertyName);
                    if (slotIndex >= 0 && IsRendererBinding(binding))
                    {
                        var renderer = ResolveRendererByPath(ctx, binding.path);
                        var data = ctx.Renderers.FirstOrDefault(d => d.Renderer == renderer);
                        if (data != null && data.SlotMap.TryGetValue(slotIndex, out var newIndex) &&
                            newIndex != slotIndex)
                        {
                            newBinding = new EditorCurveBinding
                            {
                                path = binding.path,
                                type = binding.type,
                                propertyName = $"m_Materials.Array.data[{newIndex}]",
                            };
                            bindingChanged = true;
                        }
                    }

                    var valuesChanged = false;
                    var newRefs = new ObjectReferenceKeyframe[refs.Length];
                    for (var i = 0; i < refs.Length; i++)
                    {
                        var resolved = ctx.Remapper.Resolve(refs[i].value);
                        newRefs[i] = new ObjectReferenceKeyframe
                        {
                            time = refs[i].time,
                            value = resolved,
                        };
                        if (resolved != refs[i].value) valuesChanged = true;
                    }

                    if (bindingChanged || valuesChanged)
                    {
                        if (bindingChanged)
                        {
                            // Remove the old binding's curve (the new binding replaces it). /
                            // 移除旧绑定的曲线（新绑定替换之）。
                            AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                        }
                        AnimationUtility.SetObjectReferenceCurve(clip, newBinding, newRefs);
                        remappedCount++;
                    }
                }
            }

            AtoLog.Info($"[ATO] references: {remappedCount} animation curve(s) remapped.");
        }

        private static int ParseSlotIndex(string propertyName)
        {
            const string prefix = "m_Materials.Array.data[";
            if (!propertyName.StartsWith(prefix, StringComparison.Ordinal)) return -1;
            var close = propertyName.IndexOf(']', prefix.Length);
            if (close < 0) return -1;
            var num = propertyName.Substring(prefix.Length, close - prefix.Length);
            return int.TryParse(num, out var index) ? index : -1;
        }

        private static bool IsRendererBinding(EditorCurveBinding binding) =>
            binding.type == typeof(SkinnedMeshRenderer) || binding.type == typeof(MeshRenderer);

        /// <summary>
        /// Resolve a renderer by animation path (avatar-root relative). / 按动画路径（相对 Avatar 根）解析渲染器。
        /// </summary>
        private static Renderer ResolveRendererByPath(AtoContext ctx, string path)
        {
            Transform target;
            if (string.IsNullOrEmpty(path))
            {
                target = ctx.AvatarRoot.transform;
            }
            else
            {
                target = ctx.AvatarRoot.transform.Find(path);
            }
            if (target == null) return null;
            return target.GetComponent<Renderer>();
        }
    }
}
