// ATO — Avatar Texture Optimizer
// Pass 4 — reassign: rewrites mesh UVs to atlas positions (or scaled-in-place positions),
// updates material texture references to the generated atlases / scaled textures, and
// remaps texture references inside animation clips.
// Pass 4——回写：把网格 UV 重写到图集位置（或原地缩放位置），把材质贴图引用更新为生成的
// 图集 / 缩放贴图，并重映射动画片段内的贴图引用。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Pass 4 — reassign. Pass 4——回写。
    /// </summary>
    public class Pass4Reassign : ATOBasePass<Pass4Reassign>
    {
        protected override void Process(ATOBuildContext bc, nadena.dev.ndmf.BuildContext context)
        {
            var result = bc.Result;
            if (result == null || !result.didAnything) return;

            RunStage(bc, ATOI18nKeys.StageReassign, 3, () =>
            {
                // 1. Compute replacements. 1. 计算替代贴图。
                if (result.settings.generateAtlas)
                {
                    foreach (var atlas in result.atlases)
                    {
                        foreach (var group in atlas.units)
                        foreach (var usage in group.usages)
                        {
                            if (usage.texture == null) continue;
                            if (ATOKindUtil.Normalize(usage.kind) == atlas.kind)
                                usage.replacement = atlas.texture;
                        }
                    }
                }

                // Whole-texture scaling (both for the no-atlas path and for partial-whitelist
                // groups in the atlas path): resample and save scaled copies.
                // 整图缩放（无图集路径与图集路径下部分白名单组）：重采样并保存缩放副本。
                foreach (var tr in result.textures)
                {
                    bc.ThrowIfCancelled();
                    if (tr.whitelisted || tr.wholeTextureScale >= 1f - 1e-4f) continue;
                    var scaled = ResampleWhole(bc, tr);
                    if (scaled != null)
                        foreach (var u in tr.usages)
                            if (u.replacement == null) u.replacement = scaled; // don't clobber atlas assignments 不覆盖图集赋值
                }

                // Apply import parameters (Mipmap + MipStreaming) to kept, non-whitelisted textures
                // that were not regenerated. Clones only when settings differ.
                // 对未重新生成的保留非白名单贴图应用导入参数（Mipmap + MipStreaming），仅在设置不同时克隆。
                OriginalTextureSettingsApplier.Apply(bc, result);

                // 2. Write material texture references (replacement, or the remapped kept texture).
                // 2. 写回材质贴图引用（替代贴图，或被重映射的保留贴图）。
                var touchedMaterials = new HashSet<Material>();
                foreach (var usage in result.allUsages)
                {
                    if (usage.material == null || usage.texture == null) continue;
                    var target = usage.replacement != null ? usage.replacement : usage.texture;
                    if (usage.material.GetTexture(usage.propertyName) == target) continue;
                    usage.material.SetTexture(usage.propertyName, target);
                    touchedMaterials.Add(usage.material);
                }
                foreach (var m in touchedMaterials) EditorUtility.SetDirty(m);

                // 3. Rewrite mesh UVs. 3. 重写网格 UV。
                MeshRewriter.Rewrite(bc, result);

                // 4. Remap texture references inside animation clips. 4. 重映射动画片段内的贴图引用。
                RemapAnimationTextures(bc, result);
            });

            bc.ClearCaches();
        }

        private static Texture2D ResampleWhole(ATOBuildContext bc, ATOTextureRef tr)
        {
            var tex = tr.texture;
            if (tex == null) return null;
            int w = tex.width, h = tex.height;
            int newW = Mathf.Max(1, Mathf.RoundToInt(w * tr.wholeTextureScale));
            int newH = Mathf.Max(1, Mathf.RoundToInt(h * tr.wholeTextureScale));
            if (newW == w && newH == h) return null;

            var linear = UVIslandScaler.GetLinearRegion(bc, tex, 0, 0, w, h);
            var resampled = QualityMath.AreaResample(linear, w, h, newW, newH);
            ATOLog.Verbose($"[Resample] '{tex.name}' {w}x{h} → {newW}x{newH}.");
            return TextureSettingsApplier.SaveScaled(bc, tr, resampled, newW, newH);
        }

        private static void RemapAnimationTextures(ATOBuildContext bc, ATOAnalysisResult result)
        {
            // Texture2D → replacement map from usages. 用途构建的 Texture2D → 替代映射。
            var remap = new Dictionary<Texture2D, Texture2D>();
            foreach (var usage in result.allUsages)
            {
                if (usage.texture != null && usage.replacement != null && !remap.ContainsKey(usage.texture))
                    remap[usage.texture] = usage.replacement;
            }
            if (remap.Count == 0) return;

            var clips = CollectClips(context: null, result);
            foreach (var clip in clips)
            {
                if (clip == null) continue;
                bool changed = false;
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (curve == null) continue;
                    bool curveChanged = false;
                    for (int i = 0; i < curve.Length; i++)
                    {
                        if (curve[i].value is Texture2D t && remap.TryGetValue(t, out var rep))
                        {
                            curve[i].value = rep;
                            curveChanged = true;
                        }
                    }
                    if (curveChanged)
                    {
                        AnimationUtility.SetObjectReferenceCurve(clip, binding, curve);
                        changed = true;
                    }
                }
                if (changed)
                {
                    EditorUtility.SetDirty(clip);
                    ATOLog.Verbose($"[Animation] remapped texture references in '{clip.name}'.");
                }
            }
        }

        private static List<AnimationClip> CollectClips(nadena.dev.ndmf.BuildContext context, ATOAnalysisResult result)
        {
            var clips = new List<AnimationClip>();
            var seen = new HashSet<AnimationClip>();
            if (result.component != null)
            {
                var root = result.component.gameObject;
#if ATO_VRCSDK3
                var descriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
                if (descriptor != null)
                {
                    foreach (var l in descriptor.baseAnimationLayers) Collect(l.animatorController, clips, seen);
                    foreach (var l in descriptor.specialAnimationLayers) Collect(l.animatorController, clips, seen);
                }
#endif
                foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                    Collect(animator.runtimeAnimatorController, clips, seen);
            }
            return clips;
        }

        private static void Collect(RuntimeAnimatorController c, List<AnimationClip> clips, HashSet<AnimationClip> seen)
        {
            if (c == null) return;
            foreach (var clip in c.animationClips)
                if (clip != null && seen.Add(clip)) clips.Add(clip);
        }

        protected override void ReleaseResources(ATOBuildContext bc)
        {
            bc.ClearCaches();
        }
    }
}
