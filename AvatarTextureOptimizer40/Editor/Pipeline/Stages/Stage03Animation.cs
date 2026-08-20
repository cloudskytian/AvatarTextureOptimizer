using System.Collections.Generic;
using Fosa.Ato.Editor.Analysis;
using Fosa.Ato.Editor.Util;
using UnityEngine;
using UnityEditor;

namespace Fosa.Ato.Editor.Pipeline.Stages
{
    /// <summary>
    /// Stage 03: Analyze all animators/animation clips reachable from the avatar. We care about:
    ///  (a) object activation (a renderer disabled now but animated on must be included),
    ///  (b) material swaps on a slot (new material => its textures map to the same UV),
    ///  (c) texture swaps inside a material (new texture => same UV, dedup),
    ///  (d) animated ST/tiling/offset/rotation or renderMode/cutoff => strictest requirement / skip,
    ///  (e) object scale affecting world area (take max scale),
    ///  (f) material._Mode / _Cutoff changes => strictest transparency.
    /// 阶段 03：分析所有 Animator/动画片段：物体启用、材质切换、贴图切换、ST/渲染模式/cutoff 动画、
    ///  物体缩放对面积的影响等；新增贴图并入同一 UV（去重），有变换则按最严格要求处理。
    /// </summary>
    internal sealed class Stage03Animation : IStage
    {
        public string Name => "ATO/03 Analyzing animations";
        public float Weight => 2f;

        public void Run(AtoPipeline p)
        {
            var root = p.Ctx.AvatarRootObject;
            var state = p.GetState<AnimationState>();
            var clips = new HashSet<AnimationClip>();
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
                CollectClips(a.runtimeAnimatorController, clips, state);
            foreach (var a in root.GetComponentsInChildren<Animation>(true))
            {
                foreach (AnimationState s in a) if (s.clip != null) clips.Add(s.clip);
            }

            // Resolve bindings relative to avatar root / 按 Avatar 根解析绑定
            foreach (var clip in clips)
            {
                p.Progress.ThrowIfCancelled();
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var frames = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (frames == null) continue;
                    foreach (var f in frames)
                    {
                        switch (f.value)
                        {
                            case Material mat:
                                state.SwappedMaterials.Add((b.path, b.propertyName, mat));
                                break;
                            case Texture2D tex:
                                state.SwappedTextures.Add((b.path, b.propertyName, tex));
                                break;
                        }
                    }
                }
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    if (curve == null) continue;
                    string pn = b.propertyName;
                    if (pn.Contains("m_LocalScale")) state.TouchesScale = true;
                    if (pn.Contains("m_IsActive")) state.TouchesActivation = true;
                    // Tiling/offset or texture matrix / ST 变换
                    if (pn.Contains("_ST") || pn.Contains("textureRotation") || pn.Contains("_Tex_ST"))
                        state.TouchesSt = true;
                    if (pn.EndsWith("_Cutoff") || pn.EndsWith("_Mode")) state.TouchesRenderMode = true;
                    // track max scale / 记录最大缩放
                    if (pn == "m_LocalScale.x" || pn == "m_LocalScale.y" || pn == "m_LocalScale.z")
                    {
                        for (int k = 0; k < curve.length; k++)
                            state.MaxScale = Mathf.Max(state.MaxScale, Mathf.Abs(curve.keys[k].value));
                    }
                }
            }

            // Merge swapped materials' textures into slot mapping / 把切换材质的贴图并入槽映射
            var channel = p.GetState<ChannelState>();
            foreach (var (path, propName, mat) in state.SwappedMaterials)
            {
                if (mat?.shader == null || !ShaderPropertyAnalyzer.TryGetProperties(mat.shader, out var props)) continue;
                // Locate renderer at path / 按路径定位渲染器
                var t = root.transform.Find(path);
                if (t == null || !t.TryGetComponent<Renderer>(out var r)) continue;
                for (int slot = 0; slot < r.sharedMaterials.Length; slot++)
                {
                    var key = new MaterialSlotRef(r, slot);
                    if (!p.SlotTextures.TryGetValue(key, out var list)) list = new List<TextureUsage>();
                    foreach (var pr in props)
                    {
                        if (mat.GetTexture(pr.Name) is not Texture2D tex) continue;
                        if (list.Exists(u => u.Texture == tex && u.ShaderPropertyName == pr.Name)) continue;
                        var alphaMode = MaterialTransparency.Detect(mat);
                        var u = new TextureUsage
                        {
                            Texture = tex, ImportHash = TextureIO.ImportHash(tex),
                            Kind = pr.Kind, SRGB = pr.Kind == TextureKind.Color || pr.Kind == TextureKind.Emission,
                            Filter = tex.filterMode, ShaderPropertyName = pr.Name,
                            HasAlphaChannel = TextureUtil.HasAlpha(tex),
                            Whitelisted = p.Whitelist.Contains(tex) || ShaderPropertyAnalyzer.HasStTransform(mat, pr),
                            Alpha = alphaMode, Cutoff = MaterialTransparency.Cutoff(mat),
                            IsAnimated = true,
                        };
                        list.Add(u);
                        channel.Record(tex, pr.Name, ShaderPropertyAnalyzer.GetUvChannel(mat, pr));
                    }
                    p.SlotTextures[key] = list;
                }
            }

            // Merge swapped textures into existing same-property usages (same UV identity) / 并入同属性使用
            foreach (var (path, propName, tex) in state.SwappedTextures)
            {
                if (tex == null) continue;
                var t = root.transform.Find(path);
                if (t == null) continue;
                if (t.TryGetComponent<Renderer>(out var r))
                {
                    for (int slot = 0; slot < r.sharedMaterials.Length; slot++)
                    {
                        var key = new MaterialSlotRef(r, slot);
                        if (!p.SlotTextures.TryGetValue(key, out var list)) continue;
                        // If a same-role texture exists on this slot's material, the swapped texture
                        // shares that UV identity. / 同角色的贴图共享该 UV 身份。
                        var mat = r.sharedMaterials[slot];
                        if (mat != null && ShaderPropertyAnalyzer.TryGetProperties(mat.shader, out var props))
                        {
                            var alphaMode = MaterialTransparency.Detect(mat);
                            foreach (var pr in props)
                            {
                                if (pr.Name != propName) continue;
                                if (list.Exists(u => u.Texture == tex)) continue;
                                list.Add(new TextureUsage
                                {
                                    Texture = tex, ImportHash = TextureIO.ImportHash(tex),
                                    Kind = pr.Kind, SRGB = pr.Kind == TextureKind.Color || pr.Kind == TextureKind.Emission,
                                    Filter = tex.filterMode, ShaderPropertyName = pr.Name,
                                    HasAlphaChannel = TextureUtil.HasAlpha(tex), IsAnimated = true,
                                    Alpha = alphaMode, Cutoff = MaterialTransparency.Cutoff(mat),
                                    Whitelisted = p.Whitelist.Contains(tex),
                                });
                            }
                        }
                    }
                }
            }

            if (state.TouchesSt)
            {
                // Animated ST makes any transform-sensitive texture unsafe; mark them whitelisted.
                // 有 ST 动画时，所有 transform-sensitive 贴图不安全，标记白名单
                AtoLog.Warn("Animated UV tiling/offset detected; affected textures will be skipped. / 检测到动画中的 UV 平铺/偏移，相关贴图将跳过。");
                foreach (var u in p.Usages.Values)
                    if (u.Kind == TextureKind.Color) u.Whitelisted = true;
            }

            AtoLog.VIf(p.Settings.VerboseLogging,
                $"Animations: clips={clips.Count} scaleTouched={state.TouchesScale} maxScale={state.MaxScale:F2}");
            p.Report.TextureCount = p.Usages.Count;
        }

        private static void CollectClips(RuntimeAnimatorController rc, HashSet<AnimationClip> clips, AnimationState state)
        {
            if (rc == null) return;
            foreach (var clip in rc.animationClips)
            {
                if (clip != null) clips.Add(clip);
            }
            // Override controllers / 处理 Override Controller
            if (rc is AnimatorOverrideController aoc)
            {
                var list = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                aoc.GetOverrides(list);
                foreach (var kv in list) if (kv.Value != null) clips.Add(kv.Value);
            }
        }
    }

    internal sealed class AnimationState
    {
        public bool TouchesScale, TouchesActivation, TouchesSt, TouchesRenderMode;
        public float MaxScale = 1f;
        public readonly HashSet<(string path, string prop, Material)> SwappedMaterials = new();
        public readonly HashSet<(string path, string prop, Texture2D)> SwappedTextures = new();
    }
}
