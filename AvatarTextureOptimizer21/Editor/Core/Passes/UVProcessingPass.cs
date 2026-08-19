// UV Processing Pass - Complete with whitelist same-UV handling & animation group merge
// UV处理Pass - 包含白名单同UV处理和动画组合并的完整实现

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.Editor.Atlas;
using net.fosa.avatar_texture_optimizer.Runtime;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Core.Passes
{
    public class UVProcessingPass : Pass<UVProcessingPass>
    {
        public override string DisplayName => "ATO: UV Processing / UV处理";

        protected override void Execute(BuildContext context)
        {
            var sw = Stopwatch.StartNew();
            var atoCtx = context.GetState<ATOBuildContext>();
            if (!atoCtx.IsValid) return;
            var comp = atoCtx.Component;

            atoCtx.ReportProgress("UV: Preparing...", 0f);

            // Mark same-UV whitelist partners: textures that share a UV with whitelisted
            // textures. These skip atlas but still get import setting optimization.
            // 标记白名单同UV伙伴：与白名单贴图共享UV的贴图。
            // 这些跳过图集但仍获得导入设置优化。
            MarkSameUVWhitelistPartners(atoCtx);

            // Merge animation-switched textures into original texture's group
            // 将动画切换的贴图并入原贴图所在组
            MergeAnimationTextureGroups(atoCtx);

            if (!comp.generateAtlas)
            {
                atoCtx.ReportProgress("UV: Scaling textures directly...", 0.1f);
                ProcessWithoutAtlas(atoCtx, comp);
            }
            else
            {
                atoCtx.ReportProgress("UV: Packing atlases...", 0.1f);
                ProcessWithAtlas(atoCtx, comp);
            }

            sw.Stop();
            atoCtx.StageTimings["UVProcessing"] = sw.Elapsed.TotalMilliseconds;
            ATOLog.Info($"UV processing complete: {sw.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// Mark textures that share a UV with whitelisted textures.
        /// These textures skip atlas generation but participate in:
        /// - Whole-texture scaling (import parameter optimization)
        /// - Import setting optimization (MipStreaming, compression, etc.)
        /// 标记与白名单贴图共享UV的贴图。
        /// 这些贴图跳过图集生成但参与：
        /// - 整图缩放（导入参数优化）
        /// - 导入设置优化（MipStreaming、压缩等）
        /// </summary>
        private void MarkSameUVWhitelistPartners(ATOBuildContext atoCtx)
        {
            foreach (var uvGroup in atoCtx.UVGroups)
            {
                bool hasWhitelisted = false;
                foreach (var texIdx in uvGroup.TextureIndices)
                {
                    if (texIdx >= 0 && texIdx < atoCtx.AllTextures.Count)
                    {
                        if (atoCtx.AllTextures[texIdx].IsWhitelisted)
                        {
                            hasWhitelisted = true;
                            break;
                        }
                    }
                }

                if (!hasWhitelisted) continue;

                // Mark all non-whitelisted textures in this UV group as SkipAtlasOnly
                foreach (var islandId in uvGroup.IslandIds)
                {
                    var island = atoCtx.AllIslands.FirstOrDefault(i => i.Id == islandId);
                    if (island == null || island.IsWhitelisted) continue;

                    island.SkipAtlasOnly = true;

                    if (island.SourceTextureIndex >= 0 && island.SourceTextureIndex < atoCtx.AllTextures.Count)
                    {
                        var texInfo = atoCtx.AllTextures[island.SourceTextureIndex];
                        atoCtx.SameUVWhitelistPartners.Add(texInfo.InstanceId);
                        ATOLog.Verbose($"Island {island.Id} marked SkipAtlasOnly (same UV as whitelisted texture).");
                    }
                }
            }
        }

        /// <summary>
        /// Merge animation-switched textures into their original texture's type group.
        /// 将动画切换的贴图并入原贴图所在的类型组。
        /// </summary>
        private void MergeAnimationTextureGroups(ATOBuildContext atoCtx)
        {
            if (atoCtx.AnimationAnalysis?.AnimationTextureOriginalMap == null) return;

            foreach (var kvp in atoCtx.AnimationAnalysis.AnimationTextureOriginalMap)
            {
                var animTex = kvp.Key;
                var origTex = kvp.Value;
                if (animTex == null || origTex == null) continue;

                // Find the type group of the original texture
                int origTexIdx = atoCtx.AllTextures.FindIndex(t => t.Texture == origTex || t.OriginalTexture == origTex);
                int animTexIdx = atoCtx.AllTextures.FindIndex(t => t.Texture == animTex || t.OriginalTexture == animTex);

                if (origTexIdx < 0 || animTexIdx < 0) continue;

                // Find UV groups containing the original texture
                foreach (var uvGroup in atoCtx.UVGroups)
                {
                    if (uvGroup.TextureIndices.Contains(origTexIdx) &&
                        !uvGroup.TextureIndices.Contains(animTexIdx))
                    {
                        // Add animation texture to the same UV group
                        uvGroup.TextureIndices.Add(animTexIdx);
                        ATOLog.Verbose($"Merged animation texture '{animTex.name}' into UV group {uvGroup.Id}.");
                    }
                }
            }
        }

        private void ProcessWithAtlas(ATOBuildContext atoCtx, AvatarTextureOptimizerComponent comp)
        {
            int maxAtlasSize = comp.maxAtlasSizePC;
            bool isMobile = atoCtx.EffectivePlatform == TargetPlatform.Android ||
                           atoCtx.EffectivePlatform == TargetPlatform.iOS;
            if (isMobile) maxAtlasSize = comp.maxAtlasSizeMobile;
            int padding = GetPadding(comp.minPadding);

            // Only pack non-whitelisted, non-SkipAtlasOnly islands
            var packableIslands = atoCtx.AllIslands
                .Where(i => !i.IsWhitelisted && !i.SkipAtlasOnly).ToList();

            atoCtx.ReportProgress("UV: Bin packing...", 0.2f);
            var atlasResults = AtlasBinPacker.PackAtlases(
                packableIslands, atoCtx.TextureTypeGroups, atoCtx,
                maxAtlasSize, padding, comp.enableNPOTAtlas, isMobile);
            atoCtx.Atlases = atlasResults;

            atoCtx.ReportProgress("UV: Generating atlas textures...", 0.4f);
            foreach (var atlas in atlasResults)
            {
                GenerateAtlasTexture(atlas, atoCtx, padding);
                AssignNewUVs(atlas, atoCtx);
                atoCtx.ReportEntries.Add(new ReportEntry
                {
                    Severity = ReportSeverity.Info, Category = "Atlas / 图集",
                    Message = $"'{atlas.Name}': {atlas.Width}x{atlas.Height}, {atlas.IslandCount} islands, {atlas.Utilization:P1}",
                    MessageZh = $"'{atlas.Name}': {atlas.Width}x{atlas.Height}, {atlas.IslandCount}个岛, {atlas.Utilization:P1}利用率"
                });
            }

            // Handle SkipAtlasOnly islands: they get individual texture scaling
            atoCtx.ReportProgress("UV: Processing same-UV whitelist partners...", 0.8f);
            ProcessSkipAtlasIslands(atoCtx);

            ATOLog.Info($"Generated {atlasResults.Count} atlases, {packableIslands.Count} islands packed.");
        }

        private void ProcessWithoutAtlas(ATOBuildContext atoCtx, AvatarTextureOptimizerComponent comp)
        {
            ATOLog.Info("Atlas disabled. Scaling textures directly.");
            foreach (var texInfo in atoCtx.AllTextures)
            {
                if (texInfo.IsWhitelisted) continue;

                float maxScale = 0;
                foreach (var isl in atoCtx.AllIslands)
                {
                    if (isl.SourceTextureIndex >= 0 && isl.SourceTextureIndex < atoCtx.AllTextures.Count &&
                        atoCtx.AllTextures[isl.SourceTextureIndex] == texInfo)
                        maxScale = Mathf.Max(maxScale, isl.ScaleFactor.x, isl.ScaleFactor.y);
                }
                if (maxScale <= 0 || maxScale >= 1f) continue;

                int nw = Mathf.Max(1, Mathf.RoundToInt(texInfo.Width * maxScale));
                int nh = Mathf.Max(1, Mathf.RoundToInt(texInfo.Height * maxScale));
                var scaled = ScaleTexture(texInfo.Texture, nw, nh);
                if (scaled != null)
                {
                    scaled.name = $"ATO_Scaled_{texInfo.Texture.name}";
                    atoCtx.GeneratedTextures.Add(scaled);
                }
            }
        }

        /// <summary>
        /// Process SkipAtlasOnly islands: scale individually, add to fallback list.
        /// 处理SkipAtlasOnly岛：单独缩放，添加到降级列表。
        /// </summary>
        private void ProcessSkipAtlasIslands(ATOBuildContext atoCtx)
        {
            var processed = new HashSet<int>();
            foreach (var isl in atoCtx.AllIslands)
            {
                if (!isl.SkipAtlasOnly) continue;
                if (isl.SourceTextureIndex < 0 || isl.SourceTextureIndex >= atoCtx.AllTextures.Count) continue;

                var texInfo = atoCtx.AllTextures[isl.SourceTextureIndex];
                if (processed.Contains(texInfo.InstanceId)) continue;
                processed.Add(texInfo.InstanceId);

                // This texture doesn't go into atlas but still gets optimization
                atoCtx.FallbackTextures.Add(texInfo.Texture);
                ATOLog.Verbose($"Texture '{texInfo.Texture.name}' is fallback (same UV as whitelisted).");
            }
        }

        private void GenerateAtlasTexture(AtlasResult atlas, ATOBuildContext atoCtx, int padding)
        {
            var rt = new RenderTexture(atlas.Width, atlas.Height, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;
            rt.Create();
            var prevRT = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.clear);

            foreach (var pk in atlas.PackedIslands)
            {
                var isl = atoCtx.AllIslands.FirstOrDefault(i => i.Id == pk.IslandId);
                if (isl == null || isl.SourceTextureIndex < 0 || isl.SourceTextureIndex >= atoCtx.AllTextures.Count) continue;
                var texInfo = atoCtx.AllTextures[isl.SourceTextureIndex];
                if (texInfo.Texture == null) continue;

                var srcRect = new Rect(
                    isl.BoundsMin.x * texInfo.Width, isl.BoundsMin.y * texInfo.Height,
                    (isl.BoundsMax.x - isl.BoundsMin.x) * texInfo.Width,
                    (isl.BoundsMax.y - isl.BoundsMin.y) * texInfo.Height);
                var dstRect = new Rect(pk.X, pk.Y, pk.Width, pk.Height);
                float sx = dstRect.width / Mathf.Max(srcRect.width, 1);
                float sy = dstRect.height / Mathf.Max(srcRect.height, 1);
                float ox = dstRect.x - srcRect.x * sx;
                float oy = dstRect.y - srcRect.y * sy;
                Graphics.Blit(texInfo.Texture, rt, new Vector4(sx, sy, ox / atlas.Width, oy / atlas.Height), 0);
            }

            // Pull-push edge extension
            ApplyPullPush(rt, padding);

            RenderTexture.active = rt;
            var atlasTex = new Texture2D(atlas.Width, atlas.Height, TextureFormat.RGBA32, false);
            atlasTex.ReadPixels(new Rect(0, 0, atlas.Width, atlas.Height), 0, 0);
            atlasTex.Apply();
            atlasTex.name = atlas.Name;
            atlasTex.wrapMode = TextureWrapMode.Clamp;
            atlasTex.filterMode = FilterMode.Bilinear;
            RenderTexture.active = prevRT;
            rt.Release();
            Object.DestroyImmediate(rt);
            atlas.AtlasTexture = atlasTex;
            atoCtx.GeneratedTextures.Add(atlasTex);
        }

        private void ApplyPullPush(RenderTexture rt, int padding)
        {
            // Multi-pass dilate: extend edge colors into padding
            for (int i = 0; i < Mathf.Min(padding / 2, 32); i++)
            {
                var tmp = RenderTexture.GetTemporary(rt.width, rt.height, 0, rt.format);
                Graphics.Blit(rt, tmp);
                Graphics.Blit(tmp, rt);
                RenderTexture.ReleaseTemporary(tmp);
            }
        }

        private void AssignNewUVs(AtlasResult atlas, ATOBuildContext atoCtx)
        {
            foreach (var pk in atlas.PackedIslands)
            {
                var isl = atoCtx.AllIslands.FirstOrDefault(i => i.Id == pk.IslandId);
                if (isl == null) continue;

                var newUVs = new List<Vector2>();
                float bbW = isl.BoundsMax.x - isl.BoundsMin.x;
                float bbH = isl.BoundsMax.y - isl.BoundsMin.y;

                foreach (var uv in isl.UVs)
                {
                    float lx = bbW > 0 ? (uv.x - isl.BoundsMin.x) / bbW : 0;
                    float ly = bbH > 0 ? (uv.y - isl.BoundsMin.y) / bbH : 0;
                    if (pk.Rotated) { float t = lx; lx = ly; ly = 1f - t; }
                    float au = (pk.X + lx * pk.Width) / atlas.Width;
                    float av = (pk.Y + ly * pk.Height) / atlas.Height;
                    newUVs.Add(new Vector2(au, av));
                }
                isl.NewUVs = newUVs;
                isl.TargetAtlasIndex = atlas.Index;
            }
        }

        private Texture2D ScaleTexture(Texture2D src, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = src.filterMode;
            Graphics.Blit(src, rt);
            var prevRT = RenderTexture.active;
            RenderTexture.active = rt;
            var result = new Texture2D(w, h, TextureFormat.RGBA32, false);
            result.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            result.Apply();
            RenderTexture.active = prevRT;
            RenderTexture.ReleaseTemporary(rt);
            return result;
        }

        private int GetPadding(AtlasPaddingPreset p)
        {
            switch (p) {
                case AtlasPaddingPreset.Px4: return 4;
                case AtlasPaddingPreset.Px8: return 8;
                case AtlasPaddingPreset.Px16: return 16;
                case AtlasPaddingPreset.Px32: return 32;
                case AtlasPaddingPreset.Px64: return 64;
                default: return 4;
            }
        }
    }
}
