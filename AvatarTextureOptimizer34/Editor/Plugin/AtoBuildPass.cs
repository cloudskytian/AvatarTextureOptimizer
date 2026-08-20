// AvatarTextureOptimizer - AtoBuildPass
// EN: The NDMF pass: validation → scan → UV analysis → animation analysis → texture classification → grouping →
// quality evaluation → packing → baking → remapping → dedup/merge → AAO compatibility → report. Progress bar with
// cancel support; on cancel, temp assets are kept and CPU/GPU resources are released.
// CN: NDMF pass：校验 → 扫描 → UV 分析 → 动画分析 → 贴图分类 → 分组 → 质量评估 → 装箱 → 烘焙 →
//     重映射 → 去重/合并 → AAO 兼容 → 报告。进度条支持取消；取消时保留临时资产并释放 CPU/GPU 资源。
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Plugin
{
    public class AtoBuildPass
    {
        public void Execute(BuildContext ctx)
        {
            var state = ctx.GetState<AtoBuildState>();
            state.Ctx = ctx;
            state.Component = AvatarTextureOptimizer.FindOnAvatar(ctx.AvatarRootObject);
            if (state.Component == null) return; // 未挂载组件则不处理

            var report = new BuildReport(state.Component);
            bool cancelled = false;
            try
            {
                Run(ctx, state, report);
            }
            catch (AtoAbortException e)
            {
                if (e.Message == I18n.T("report.cancelled"))
                {
                    cancelled = true;
                    AtoLog.Warn(I18n.T("warn.canceled"));
                }
                else
                {
                    AtoLog.Error(I18n.T("error.abort", e.Message));
                    ErrorReport.ReportError(new AtoSimpleError(ErrorSeverity.Error,
                        I18n.T("error.abort", e.Message)));
                }
            }
            catch (Exception e)
            {
                AtoLog.Error("Unexpected error: " + e);
                ErrorReport.ReportError(new AtoSimpleError(ErrorSeverity.Error, e.ToString()));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                state.Dispose();
                if (cancelled)
                {
                    AtoLog.Warn(I18n.T("warn.canceled"));
                }
                report.Write();
            }
        }

        private void Run(BuildContext ctx, AtoBuildState state, BuildReport report)
        {
            using (AtoLog.Time("ATO total"))
            {
                // EN: Reset per-build static state (multiple avatars per build session).
                // CN: 重置每次构建的静态状态（一次构建会话可能处理多个 Avatar）。
                ReferenceRewriter.Clear();

                // ------------------------------------------------------------ 校验
                Stage(state, "stage.validate", 0f, () =>
                {
                    string err = state.Component.ValidateMounting();
                    if (err != null) throw new AtoAbortException(err);
                });

                // ------------------------------------------------------------ 平台
                state.Platform = DetectPlatform();
                state.Profile = state.Component.EffectiveProfile(state.Platform, out bool overridden);
                AtoLog.Info($"Platform: {state.Platform}{(overridden ? " (overridden)" : "")}, " +
                            $"preset: {state.Profile.preset}");

                // ------------------------------------------------------------ 扫描（动画分析先行：动画启用的渲染器才可入选）
                AnimationData anim = null;
                Stage(state, "stage.anim", 0.05f, () =>
                {
                    anim = AnimationAnalyzer.Analyze(ctx.AvatarRootObject, state.WhitelistObjects,
                        (p, info) => Progress(state, "stage.anim", 0.05f + 0.05f * p, info));
                    report.AnimationClipCount = anim.clips.Count;
                });

                Stage(state, "stage.scan", 0.12f, () =>
                {
                    state.Renderers = AvatarScanner.Scan(ctx.AvatarRootObject, state.Component, anim, state);
                });

                Stage(state, "stage.uv", 0.22f, () =>
                {
                    int i = 0;
                    foreach (var r in state.Renderers)
                    {
                        if (CancelRequested(state)) throw Cancel();
                        bool skip = AvatarScanner.IsWhitelisted(r, state.WhitelistObjects);
                        MeshUvAnalyzer.Analyze(state, r, anim, skip);
                        Progress(state, "stage.uv", 0.22f + 0.10f * (i++ / (float)Mathf.Max(1, state.Renderers.Count)),
                            r.name);
                    }
                });

                // ------------------------------------------------------------ 贴图登记/分类
                Stage(state, "stage.texreg", 0.34f, () =>
                {
                    state.Decoder = new TextureDecoder();
                    state.Textures = TextureClassifier.BuildTextureRefs(state, state.Renderers, anim);
                    report.TextureCount = state.Textures.Count;
                    AtoExtensions.InvokeBeforeAnalyze(state);
                });

                // ------------------------------------------------------------ 分组
                Stage(state, "stage.group", 0.44f, () => GroupBuilder.Build(state));

                // ------------------------------------------------------------ 质量
                Stage(state, "stage.quality", 0.52f, () =>
                {
                    MetricsGpu.ResetCache();
                    QualityEvaluator.GpuEnabled = MetricsGpu.IsUsable(state);
                    QualityEvaluator.Evaluate(state);
                });

                // ------------------------------------------------------------ 装箱
                PackingResult packing = null;
                Stage(state, "stage.pack", 0.66f, () =>
                {
                    if (state.Component.generateAtlases)
                        packing = AtlasPacker.Pack(state);
                    else
                        packing = new PackingResult();
                    MeshRemapBuilder.AssignRemaps(state, packing);
                });

                // ------------------------------------------------------------ 烘焙
                var generated = new List<Texture2D>();
                var texSession = new ContentDeduper.GeneratedTextureSession();
                Stage(state, "stage.bake", 0.74f, () =>
                {
                    var pool = new RenderTexturePool();
                    var blitMat = TextureBaker.FindBlitMaterial();
                    int i = 0;
                    foreach (var atlas in packing.atlases)
                    {
                        if (CancelRequested(state)) throw Cancel();
                        Progress(state, "stage.bake", 0.74f + 0.10f * (i / (float)Mathf.Max(1, packing.atlases.Count)),
                            atlas.Name);
                        var tex = TextureBaker.BakeAtlas(state, atlas, pool, blitMat,
                            state.Component.useGpuMetrics, null);
                        if (tex == null) continue;
                        bool hasAlpha = TextureBaker.HasAnyAlpha(atlas);
                        var cat = CategoryFor(atlas.usage, hasAlpha);
                        bool srgb = atlas.usage == TextureUsage.Albedo;
                        bool isNew = true;
                        var canonical = texSession.Resolve(tex, cat, atlas.usage, srgb, out isNew);
                        Texture2D asset;
                        if (isNew)
                        {
                            asset = TextureAssetWriter.CreateTextureAsset(state, tex,
                                $"ATO_{state.Ctx.AvatarRootObject.name}_{atlas.group.Name}_{i}",
                                cat, atlas.usage, srgb,
                                TextureAssetWriter.ResolveFilterMode(atlas),
                                TextureAssetWriter.ResolveAniso(atlas));
                            if (asset != null) texSession.RegisterAsset(tex, asset);
                        }
                        else
                        {
                            texSession.TryGetAsset(canonical, out asset);
                            AtoLog.Detail($"Generated atlas dedup: {tex.name} -> {asset != null ? asset.name : "?"}");
                        }
                        UnityEngine.Object.DestroyImmediate(tex);
                        if (asset == null) continue;
                        atlas.asset = asset;
                        atlas.Name = asset.name;
                        generated.Add(asset);
                        foreach (var pi in atlas.islands)
                        {
                            ReferenceRewriter.RegisterTexture(pi.tex.texture, asset);
                        }
                        i++;
                    }

                    // EN: Whole-texture scaling (atlas off / skipAtlas textures).
                    // CN: 整图缩放（图集关闭 / 跳图集贴图）。
                    foreach (var tref in state.Textures)
                    {
                        if (CancelRequested(state)) throw Cancel();
                        if (tref.whitelisted || tref.specialUv) continue;
                        bool needsScale = tref.wholeScale < 0.999f;
                        bool needsParams = state.Profile.mipmaps != (tref.texture.mipmapCount > 1);
                        if (!needsScale && !needsParams) continue;
                        var scaled = ScaleWholeTexture(state, tref, pool, blitMat);
                        if (scaled != null)
                        {
                            var cat = CategoryFor(tref.usage, tref.HasAlphaRequirement);
                            bool isNew;
                            var canonical = texSession.Resolve(scaled, cat, tref.usage, tref.sRGB, out isNew);
                            Texture2D asset = null;
                            if (isNew)
                            {
                                asset = TextureAssetWriter.CreateTextureAsset(state, scaled,
                                    $"ATO_{state.Ctx.AvatarRootObject.name}_scale_{tref.texture.name}",
                                    cat, tref.usage, tref.sRGB);
                                if (asset != null) texSession.RegisterAsset(scaled, asset);
                            }
                            else
                            {
                                texSession.TryGetAsset(canonical, out asset);
                            }
                            UnityEngine.Object.DestroyImmediate(scaled);
                            if (asset != null)
                            {
                                generated.Add(asset);
                                ReferenceRewriter.RegisterTexture(tref.texture, asset);
                            }
                        }
                    }
                    pool.Dispose();
                    if (blitMat != null) UnityEngine.Object.DestroyImmediate(blitMat);
                });

                // ------------------------------------------------------------ 网格重映射
                Stage(state, "stage.remap", 0.86f, () =>
                {
                    // EN: Free packing bitmasks (Persistent allocations) before baking.
                    // CN: 释放装箱位掩码（Persistent 分配）后再烘焙。
                    foreach (var a in packing.atlases) a.Dispose();
                    MeshWriter.RebuildAll(state);
                });

                // ------------------------------------------------------------ 材质应用
                Stage(state, "stage.remap", 0.90f, () =>
                {
                    ApplyMaterials(state);
                });

                // ------------------------------------------------------------ 去重/合并
                Dictionary<(Renderer, int), int> slotRemap = null;
                Stage(state, "stage.dedup", 0.93f, () =>
                {
                    // EN: Texture dedup already happened pre-write (GeneratedTextureSession).
                    // CN: 贴图去重已在写入前完成（GeneratedTextureSession）。
                    if (state.Component.enableDedup && state.Component.enableSlotMerge)
                        ContentDeduper.DedupMaterials(state, anim, out slotRemap);
                    else
                        slotRemap = new Dictionary<(Renderer, int), int>();
                });

                // ------------------------------------------------------------ 动画重写
                Stage(state, "stage.remap", 0.96f, () =>
                {
                    ReferenceRewriter.RewriteAnimations(state, anim, slotRemap);
                });

                // ------------------------------------------------------------ AAO 兼容
                Stage(state, "stage.aao", 0.98f, () =>
                {
                    AaoCompat.EvacuateModifiedChannels(state);
                });

                // ------------------------------------------------------------ 移除自身
                UnityEngine.Object.DestroyImmediate(state.Component);
                state.Component = null;

                // ------------------------------------------------------------ 报告数据
                AtoExtensions.InvokeAfterBake(state, packing);
                report.Fill(state, packing, generated);
                AtoExtensions.InvokeAfterAll(state);
            }
        }

        // ===================================================================== 工具

        private void Stage(AtoBuildState state, string key, float progress, Action action)
        {
            if (CancelRequested(state)) throw Cancel();
            Progress(state, key, progress, null);
            using (AtoLog.Time(I18n.T(key)))
            {
                action();
            }
        }

        private bool CancelRequested(AtoBuildState state)
        {
            if (state.Cancelled) return true;
            return false;
        }

        private AtoAbortException Cancel() => new AtoAbortException(I18n.T("report.cancelled"));

        private void Progress(AtoBuildState state, string stageKey, float p, string info)
        {
            string text = I18n.T(stageKey) + (string.IsNullOrEmpty(info) ? "" : " — " + info);
            bool cancelled = EditorUtility.DisplayCancelableProgressBar("AvatarTextureOptimizer", text, p);
            if (cancelled) state.Cancelled = true;
        }

        private static AtoPlatform DetectPlatform()
        {
#if UNITY_ANDROID
            return AtoPlatform.Android;
#elif UNITY_IOS
            return AtoPlatform.iOS;
#else
            return AtoPlatform.PC;
#endif
        }

        private static TextureCategory CategoryFor(TextureUsage usage, bool hasAlpha)
        {
            switch (usage)
            {
                case TextureUsage.Normal: return TextureCategory.Normal;
                case TextureUsage.GrayMask: return TextureCategory.Gray;
                default: return hasAlpha ? TextureCategory.OpaqueAlpha : TextureCategory.Opaque;
            }
        }

        /// <summary>EN: Resizes a whole texture by tref.wholeScale (RT bilinear, then readback). / CN: 按 tref.wholeScale 缩放整张贴图。</summary>
        private static Texture2D ScaleWholeTexture(AtoBuildState state, TextureRef tref,
            RenderTexturePool pool, Material blitMat)
        {
            var src = state.Decoder != null ? state.Decoder.Decode(tref.texture) : null;
            if (src == null) return null;
            int w = Mathf.Max(1, Mathf.RoundToInt(src.width * tref.wholeScale));
            int h = Mathf.Max(1, Mathf.RoundToInt(src.height * tref.wholeScale));
            if (w >= src.width && h >= src.height)
            {
                // EN: No shrink needed; still re-encode so import params apply (mipmap/streaming binding).
                // CN: 无需缩小；仍重编码以应用导入参数（mipmap/streaming 绑定）。
                return DuplicateTexture(state, src, tref.sRGB);
            }
            var rt = pool.Get(w, h, RenderTextureFormat.ARGB32, 0, tref.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false, tref.sRGB);
            outTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            outTex.Apply();
            RenderTexture.active = prev;
            pool.Release(rt);
            AtoLog.Detail($"Whole texture {tref.texture.name}: {src.width}x{src.height} -> {w}x{h} (scale {tref.wholeScale:F3})");
            return outTex;
        }

        private static Texture2D DuplicateTexture(AtoBuildState state, Texture2D src, bool srgb)
        {
            var copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, srgb);
            copy.SetPixels32(src.GetPixels32());
            copy.Apply();
            return copy;
        }

        /// <summary>EN: Applies the new textures to renderer slots (cloned materials; nothing but textures changes).
        /// CN: 把新贴图应用到渲染器槽位（克隆材质；除贴图外什么都不改）。</summary>
        private static void ApplyMaterials(AtoBuildState state)
        {
            foreach (var tref in state.Textures)
            {
                if (!ReferenceRewriter.TextureMap.TryGetValue(tref.texture, out var newTex)) continue;
                if (newTex == null || newTex == tref.texture) continue;
                int propId = Shader.PropertyToID(tref.propertyName);
                foreach (var mu2 in tref.meshUsages)
                {
                    var mats = mu2.renderer.sharedMaterials;
                    if (mu2.slot < 0 || mu2.slot >= mats.Length || mats[mu2.slot] == null) continue;
                    var oldMat = mats[mu2.slot];
                    var dict = new Dictionary<int, Texture2D> { [propId] = newTex };
                    var clone = ReferenceRewriter.CloneWithTextures(state, oldMat, dict);
                    mats[mu2.slot] = clone;
                    mu2.renderer.sharedMaterials = mats;
                }
            }
        }
    }
}
