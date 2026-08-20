// SPDX-License-Identifier: MIT
// EN: Walks the avatar, collects renderers / material slots / textures and builds the UV <-> texture map.
// ZH: 遍历 Avatar，收集渲染器、材质槽与贴图，并建立 UV 与贴图的对应关系。

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: One renderer that takes part in the optimisation.
    /// ZH: 参与优化的一个渲染器。
    /// </summary>
    public sealed class ATORendererInfo
    {
        public Renderer Renderer;
        public string Path;
        public Mesh Mesh;
        public Vector3 MaxLossyScale = Vector3.one;

        /// <summary>EN: All materials that can ever sit in a slot (static + animated). ZH: 每个槽位可能出现的全部材质（静态+动画）。</summary>
        public readonly List<List<Material>> SlotMaterials = new List<List<Material>>();

        /// <summary>EN: Slots that animations can swap. ZH: 会被动画切换的槽位。</summary>
        public readonly HashSet<int> AnimatedSlots = new HashSet<int>();

        public override string ToString() => $"{Path} ({(Mesh ? Mesh.name : "no mesh")})";
    }

    /// <summary>
    /// EN: Result of the avatar scan.
    /// ZH: Avatar 扫描的结果。
    /// </summary>
    public sealed class ATOScanResult
    {
        public readonly List<ATORendererInfo> Renderers = new List<ATORendererInfo>();

        /// <summary>EN: Canonical texture -&gt; info. ZH: 规范贴图 -&gt; 信息。</summary>
        public readonly Dictionary<Texture2D, ATOTextureInfo> Textures = new Dictionary<Texture2D, ATOTextureInfo>();

        /// <summary>EN: Duplicate source texture -&gt; canonical texture. ZH: 重复的源贴图 -&gt; 规范贴图。</summary>
        public readonly Dictionary<Texture2D, Texture2D> Deduplication = new Dictionary<Texture2D, Texture2D>();

        /// <summary>EN: Materials seen anywhere on the avatar. ZH: Avatar 上出现过的所有材质。</summary>
        public readonly HashSet<Material> Materials = new HashSet<Material>();

        /// <summary>EN: Analysis result per material. ZH: 每个材质的分析结果。</summary>
        public readonly Dictionary<Material, ATOMaterialAnalysis> MaterialAnalysis =
            new Dictionary<Material, ATOMaterialAnalysis>();

        /// <summary>EN: Objects resolved from the user whitelist. ZH: 从用户白名单解析出的对象。</summary>
        public readonly HashSet<UnityEngine.Object> WhitelistedObjects = new HashSet<UnityEngine.Object>();
    }

    /// <summary>
    /// EN: The scanner itself.
    /// ZH: 扫描器本体。
    /// </summary>
    public sealed class ATOAvatarScanner
    {
        private readonly ATOLog _log;
        private readonly ATOReporter _reporter;
        private readonly ATOShaderAnalyzer _shaderAnalyzer;
        private readonly ATOSettings _settings;
        private readonly ATOAnimationInfo _anim;

        public ATOAvatarScanner(ATOLog log, ATOReporter reporter, ATOShaderAnalyzer shaderAnalyzer,
            ATOSettings settings, ATOAnimationInfo anim)
        {
            _log = log;
            _reporter = reporter;
            _shaderAnalyzer = shaderAnalyzer;
            _settings = settings;
            _anim = anim;
        }

        public ATOScanResult Scan(GameObject avatarRoot, ATOTextureCache cache)
        {
            var result = new ATOScanResult();

            ResolveWhitelist(avatarRoot, result);
            CollectRenderers(avatarRoot, result);
            CollectMaterialsAndTextures(result);
            DeduplicateSourceTextures(result, cache);

            _log.Info("scan",
                $"renderers={result.Renderers.Count}, materials={result.Materials.Count}, " +
                $"textures={result.Textures.Count}, dedup={result.Deduplication.Count}");
            return result;
        }

        // ------------------------------------------------------------------ whitelist

        private void ResolveWhitelist(GameObject avatarRoot, ATOScanResult result)
        {
            if (_settings.whitelist == null) return;

            foreach (var obj in _settings.whitelist)
            {
                if (obj == null) continue;
                result.WhitelistedObjects.Add(obj);

                switch (obj)
                {
                    case GameObject go:
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        {
                            result.WhitelistedObjects.Add(r);
                            foreach (var m in r.sharedMaterials)
                                if (m != null)
                                    AddMaterialToWhitelist(m, result);
                        }

                        break;
                    case Renderer renderer:
                        foreach (var m in renderer.sharedMaterials)
                            if (m != null)
                                AddMaterialToWhitelist(m, result);
                        break;
                    case Material material:
                        AddMaterialToWhitelist(material, result);
                        break;
                    case AnimationClip clip:
                        foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        {
                            foreach (var kf in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                            {
                                if (kf.value is Material m) AddMaterialToWhitelist(m, result);
                                else if (kf.value != null) result.WhitelistedObjects.Add(kf.value);
                            }
                        }

                        break;
                    case RuntimeAnimatorController controller:
                        foreach (var clip2 in controller.animationClips)
                        {
                            if (clip2 == null) continue;
                            result.WhitelistedObjects.Add(clip2);
                            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip2))
                            foreach (var kf in AnimationUtility.GetObjectReferenceCurve(clip2, binding))
                                if (kf.value is Material m)
                                    AddMaterialToWhitelist(m, result);
                        }

                        break;
                }
            }

            _log.Info("whitelist", $"resolved {result.WhitelistedObjects.Count} objects from the user whitelist");
        }

        private void AddMaterialToWhitelist(Material material, ATOScanResult result)
        {
            result.WhitelistedObjects.Add(material);
            if (material.shader == null) return;

            var analysis = GetAnalysis(material, result);
            foreach (var p in analysis.Properties)
                if (p.Texture != null)
                    result.WhitelistedObjects.Add(p.Texture);
        }

        private ATOMaterialAnalysis GetAnalysis(Material material, ATOScanResult result)
        {
            if (result.MaterialAnalysis.TryGetValue(material, out var a)) return a;
            a = _shaderAnalyzer.Analyze(material);
            result.MaterialAnalysis[material] = a;
            return a;
        }

        // ------------------------------------------------------------------ renderers

        private void CollectRenderers(GameObject avatarRoot, ATOScanResult result)
        {
            foreach (var renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                if (IsEditorOnly(renderer.transform, avatarRoot.transform)) continue;

                var path = ATOPathUtil.RelativePath(avatarRoot.transform, renderer.transform);

                if (!IsPotentiallyActive(renderer, avatarRoot.transform, path))
                {
                    _log.Trace("scan", $"skipping '{path}': never enabled");
                    continue;
                }

                var mesh = GetMesh(renderer);
                if (mesh == null) continue;

                var info = new ATORendererInfo
                {
                    Renderer = renderer,
                    Path = path,
                    Mesh = mesh,
                    MaxLossyScale = ComputeMaxScale(avatarRoot.transform, renderer.transform),
                };

                var mats = renderer.sharedMaterials;
                for (var slot = 0; slot < mats.Length; slot++)
                {
                    var list = new List<Material>();
                    if (mats[slot] != null) list.Add(mats[slot]);

                    if (_anim.MaterialSwaps.TryGetValue((path, slot), out var swaps))
                    {
                        info.AnimatedSlots.Add(slot);
                        foreach (var m in swaps)
                            if (m != null && !list.Contains(m))
                                list.Add(m);
                    }

                    info.SlotMaterials.Add(list);
                }

                result.Renderers.Add(info);
            }
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        private bool IsPotentiallyActive(Renderer renderer, Transform root, string path)
        {
            if (renderer.enabled && renderer.gameObject.activeInHierarchy) return true;
            if (_anim.RendererEnabledPaths.Contains(path)) return true;

            // EN: Any ancestor (including itself) that an animation can switch on makes it potentially active.
            // ZH: 任意祖先（含自身）能被动画启用，就视为可能被启用。
            var t = renderer.transform;
            while (t != null)
            {
                var p = ATOPathUtil.RelativePath(root, t);
                if (_anim.ActivatablePaths.Contains(p) && renderer.enabled) return true;
                if (t == root) break;
                t = t.parent;
            }

            return false;
        }

        private static bool IsEditorOnly(Transform t, Transform root)
        {
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                if (t == root) break;
                t = t.parent;
            }

            return false;
        }

        private Vector3 ComputeMaxScale(Transform root, Transform target)
        {
            // EN: Multiply the largest reachable local scale of every transform up to the avatar root.
            // ZH: 从对象一路到 Avatar 根节点，逐级相乘每个 transform 可达的最大局部缩放。
            var scale = Vector3.one;
            var t = target;
            while (t != null)
            {
                var path = ATOPathUtil.RelativePath(root, t);
                var local = _anim.GetMaxScale(path, t.localScale);
                scale = new Vector3(scale.x * local.x, scale.y * local.y, scale.z * local.z);
                if (t == root) break;
                t = t.parent;
            }

            return scale;
        }

        // ------------------------------------------------------------------ textures

        private void CollectMaterialsAndTextures(ATOScanResult result)
        {
            foreach (var rendererInfo in result.Renderers)
            {
                var mesh = rendererInfo.Mesh;
                var subMeshCount = mesh.subMeshCount;

                for (var slot = 0; slot < rendererInfo.SlotMaterials.Count; slot++)
                {
                    // EN: Unity wraps material slots around the sub mesh count. ZH: Unity 会按子网格数量循环使用材质槽。
                    var subMesh = subMeshCount == 0 ? 0 : slot % subMeshCount;
                    if (slot >= subMeshCount && subMeshCount > 0)
                    {
                        // EN: Extra slots re-render sub mesh 0..n; they still sample the same UVs.
                        // ZH: 多出来的槽位会重复渲染子网格；采样的仍是同一套 UV。
                        subMesh = slot % subMeshCount;
                    }

                    foreach (var material in rendererInfo.SlotMaterials[slot])
                    {
                        if (material == null) continue;
                        result.Materials.Add(material);
                        var analysis = GetAnalysis(material, result);

                        var materialWhitelisted = result.WhitelistedObjects.Contains(material) ||
                                                  result.WhitelistedObjects.Contains(rendererInfo.Renderer) ||
                                                  result.WhitelistedObjects.Contains(rendererInfo.Renderer.gameObject) ||
                                                  result.WhitelistedObjects.Contains(mesh);

                        if (analysis.ShaderUnknown)
                        {
                            _reporter.Warn("ato:warn:shaderUnknown", material,
                                material.shader != null ? material.shader.name : "<null>");
                        }

                        var animatedProps = _anim.AnimatedMaterialProperties.TryGetValue(rendererInfo.Path, out var ap)
                            ? ap
                            : null;

                        foreach (var prop in analysis.Properties)
                        {
                            RegisterTexture(result, rendererInfo, mesh, subMesh, material, analysis, prop,
                                materialWhitelisted, animatedProps);
                        }
                    }
                }
            }
        }

        private void RegisterTexture(ATOScanResult result, ATORendererInfo rendererInfo, Mesh mesh, int subMesh,
            Material material, ATOMaterialAnalysis analysis, ATOPropertyAnalysis prop, bool materialWhitelisted,
            HashSet<string> animatedProps)
        {
            var texture = prop.Texture;
            if (texture == null) return;

            var info = GetOrCreateTextureInfo(result, texture);

            var unsafeReason = prop.Safe ? null : prop.UnsafeReason;

            // EN: An animation touching a transform sensitive property invalidates the whole property.
            // ZH: 动画一旦修改了与变换相关的属性，该属性就不再安全。
            if (unsafeReason == null && animatedProps != null)
            {
                foreach (var sensitive in _shaderAnalyzer.GetTransformSensitiveProperties(material))
                {
                    if (!animatedProps.Contains(sensitive)) continue;
                    if (sensitive.StartsWith("_Cutoff", StringComparison.Ordinal) ||
                        sensitive.StartsWith("_AlphaCutoff", StringComparison.Ordinal) ||
                        sensitive.StartsWith("_Cutout", StringComparison.Ordinal) ||
                        sensitive == "_Mode")
                        continue; // handled separately, does not invalidate UVs

                    if (!sensitive.StartsWith(prop.PropertyName, StringComparison.Ordinal)) continue;
                    unsafeReason = $"animation modifies {sensitive}";
                    break;
                }
            }

            if (!prop.Safe || unsafeReason != null)
            {
                info.Whitelisted = true;
                info.BlockReason = unsafeReason;
                _reporter.Warn("ato:warn:transformedUV", material, material.name, prop.PropertyName);
            }

            if (materialWhitelisted) info.Whitelisted = true;
            if (result.WhitelistedObjects.Contains(texture)) info.Whitelisted = true;

            // EN: Strictest role wins: normal > grayscale > transparent colour > opaque colour.
            // ZH: 取最严格的角色：法线 > 灰度 > 透明颜色 > 不透明颜色。
            var role = prop.Role;
            if (role == ATOTextureRole.ColorOpaque && analysis.AlphaMode != ATOAlphaMode.Opaque)
                role = ATOTextureRole.ColorTransparent;
            info.Role = StrictestRole(info.Role, role);

            info.AlphaMode = StrictestAlpha(info.AlphaMode, analysis.AlphaMode);

            var cutoff = analysis.Cutoff;
            if (_anim.AnimatedCutoffs.TryGetValue(rendererInfo.Path, out var cutoffs))
                foreach (var c in cutoffs)
                    if (!info.Cutoffs.Contains(c))
                        info.Cutoffs.Add(c);
            if (!info.Cutoffs.Contains(cutoff)) info.Cutoffs.Add(cutoff);

            for (var i = 0; i < 4; i++) info.UsedChannels[i] |= prop.UsedChannels[i];

            info.Usages.Add(new ATOTextureUsage
            {
                Material = material,
                PropertyName = prop.PropertyName,
                Role = role,
                AlphaMode = analysis.AlphaMode,
                Cutoff = cutoff,
                UVChannel = prop.UVChannel,
                UsedChannels = prop.UsedChannels,
            });

            if (mesh != null && HasUVChannel(mesh, prop.UVChannel))
            {
                info.UVKeys.Add(new ATOUVKey(mesh, subMesh, prop.UVChannel));
            }
            else
            {
                info.AtlasBlocked = true;
                info.BlockReason = $"mesh '{(mesh ? mesh.name : "<null>")}' has no UV{prop.UVChannel}";
            }
        }

        private static bool HasUVChannel(Mesh mesh, int channel)
        {
            if (channel < 0 || channel > 7) return false;
            var list = new List<Vector2>();
            mesh.GetUVs(channel, list);
            return list.Count > 0;
        }

        private static ATOTextureRole StrictestRole(ATOTextureRole a, ATOTextureRole b)
        {
            if (a == ATOTextureRole.Normal || b == ATOTextureRole.Normal) return ATOTextureRole.Normal;
            if (a == ATOTextureRole.ColorTransparent || b == ATOTextureRole.ColorTransparent)
                return ATOTextureRole.ColorTransparent;
            if (a == ATOTextureRole.Grayscale && b == ATOTextureRole.Grayscale) return ATOTextureRole.Grayscale;
            if (a == ATOTextureRole.Grayscale || b == ATOTextureRole.Grayscale)
                return a == ATOTextureRole.Grayscale ? b : a;
            return ATOTextureRole.ColorOpaque;
        }

        private static ATOAlphaMode StrictestAlpha(ATOAlphaMode a, ATOAlphaMode b)
        {
            if (a == ATOAlphaMode.Blend || b == ATOAlphaMode.Blend) return ATOAlphaMode.Blend;
            if (a == ATOAlphaMode.Cutout || b == ATOAlphaMode.Cutout) return ATOAlphaMode.Cutout;
            return ATOAlphaMode.Opaque;
        }

        private ATOTextureInfo GetOrCreateTextureInfo(ATOScanResult result, Texture2D texture)
        {
            if (result.Textures.TryGetValue(texture, out var info)) return info;

            info = new ATOTextureInfo
            {
                Source = texture,
                Width = texture.width,
                Height = texture.height,
                SRGB = ATOTextureCache.IsSRGB(texture),
                Filter = texture.filterMode,
                Wrap = texture.wrapMode,
                AnisoLevel = texture.anisoLevel,
                Whitelisted = result.WhitelistedObjects.Contains(texture),
            };

            result.Textures[texture] = info;
            return info;
        }

        // ------------------------------------------------------------------ deduplication

        private void DeduplicateSourceTextures(ATOScanResult result, ATOTextureCache cache)
        {
            if (result.Textures.Count < 2) return;

            var buckets = new Dictionary<string, List<ATOTextureInfo>>();

            foreach (var info in result.Textures.Values.ToList())
            {
                string hash;
                try
                {
                    hash = ComputeSignature(info, cache);
                }
                catch (Exception e)
                {
                    _log.Warning("dedup", $"could not hash '{info}': {e.Message}");
                    continue;
                }

                if (!buckets.TryGetValue(hash, out var list))
                {
                    list = new List<ATOTextureInfo>();
                    buckets[hash] = list;
                }

                list.Add(info);
            }

            foreach (var bucket in buckets.Values)
            {
                if (bucket.Count < 2) continue;

                var canonical = bucket[0];
                for (var i = 1; i < bucket.Count; i++)
                {
                    var dup = bucket[i];

                    // EN: A whitelisted duplicate makes the merged result whitelisted as well.
                    // ZH: 只要有一个副本在白名单内，合并结果也视为白名单。
                    canonical.Whitelisted |= dup.Whitelisted;
                    canonical.AtlasBlocked |= dup.AtlasBlocked;
                    canonical.Role = StrictestRole(canonical.Role, dup.Role);
                    canonical.AlphaMode = StrictestAlpha(canonical.AlphaMode, dup.AlphaMode);
                    for (var c = 0; c < 4; c++) canonical.UsedChannels[c] |= dup.UsedChannels[c];
                    foreach (var cutoff in dup.Cutoffs)
                        if (!canonical.Cutoffs.Contains(cutoff))
                            canonical.Cutoffs.Add(cutoff);
                    canonical.Usages.AddRange(dup.Usages);
                    foreach (var key in dup.UVKeys) canonical.UVKeys.Add(key);

                    result.Textures.Remove(dup.Source);
                    result.Deduplication[dup.Source] = canonical.Source;
                    _log.Trace("dedup", $"'{dup.Source.name}' -> '{canonical.Source.name}'");
                }
            }

            if (result.Deduplication.Count > 0)
                _log.Info("dedup", $"merged {result.Deduplication.Count} duplicated source textures");
        }

        private static string ComputeSignature(ATOTextureInfo info, ATOTextureCache cache)
        {
            var decoded = cache.Get(info.Source, false);
            ulong h1 = 1469598103934665603UL;
            var pixels = decoded.Pixels;
            var step = Math.Max(1, pixels.Length / (1 << 20)); // EN: cap the hashing cost. ZH: 限制哈希开销。

            for (var i = 0; i < pixels.Length; i += step)
            {
                var p = pixels[i];
                h1 = Mix(h1, (ulong)p.x.value | ((ulong)p.y.value << 16) | ((ulong)p.z.value << 32) |
                             ((ulong)p.w.value << 48));
            }

            // EN: Import settings are part of the identity: different settings => different texture.
            // ZH: 导入设置属于身份的一部分：设置不同即视为不同贴图。
            var importSignature =
                $"{info.Width}x{info.Height}|{info.SRGB}|{info.Filter}|{info.Wrap}|{info.AnisoLevel}|" +
                $"{info.Source.mipmapCount}|{info.Source.format}";

            return h1.ToString("x16") + "|" + importSignature;
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            hash ^= value;
            hash *= 1099511628211UL;
            return hash;
        }
    }

    /// <summary>
    /// EN: Path helpers shared across the pipeline.
    /// ZH: 管线共用的路径辅助方法。
    /// </summary>
    public static class ATOPathUtil
    {
        /// <summary>
        /// EN: Returns the animation-style relative path from root to target ("" for the root itself).
        /// ZH: 返回动画风格的相对路径（根节点本身返回空串）。
        /// </summary>
        public static string RelativePath(Transform root, Transform target)
        {
            if (target == root) return "";
            var stack = new List<string>();
            var t = target;
            while (t != null && t != root)
            {
                stack.Add(t.name);
                t = t.parent;
            }

            stack.Reverse();
            return string.Join("/", stack);
        }
    }
}
