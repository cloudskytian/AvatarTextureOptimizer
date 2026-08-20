// ============================================================================
// ATO - apply stage (stage 7): the single atomic mutation point
// ATO - Apply 阶段（阶段7）：唯一原子改动点
//
// Shared-asset safety  共享资产安全：
//   the NDMF build clones GameObjects, but MESH / MATERIAL / TEXTURE assets
//   are still shared with the user's source project. ATO therefore CLONES
//   any shared asset it must mutate (mesh UVs, material texture slots,
//   importer settings) and registers the replacement with the NDMF
//   ObjectRegistry so every reference in the build is rebound. User source
//   assets are never modified.
//   NDMF 构建克隆了 GameObject，但网格/材质/贴图资产仍与用户源工程共享。因此
//   ATO 会克隆所有需要修改的共享资产（网格 UV、材质贴图槽、导入设置），并经
//   NDMF ObjectRegistry 注册替换，使构建内所有引用被重绑定。用户源资产绝不被
//   修改。
//
// Order 顺序：
//   1. save new textures (atlas pages / scaled copies) 保存新贴图
//   2. prepare shared assets (clone meshes/materials/textures as needed)
//      准备共享资产（按需克隆）
//   3. UV remap (AAO channel evacuation included) UV 重映射（含 AAO 撤离）
//   4. material dedup registration + renderer plans 材质去重注册 + 渲染器计划
//   5. final texture assignment (texture slots ONLY) 最终贴图赋值（仅贴图槽）
//   6. import settings 导入设置
//   7. animation rewrite 动画改写
//   8. ATO component self-removal 组件自移除
// ============================================================================

#region

using System.Collections.Generic;
using nadena.dev.ndmf;
using net.fosa.AvatarTextureOptimizer.Editor.Analysis;
using net.fosa.AvatarTextureOptimizer.Editor.Atlas;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using net.fosa.AvatarTextureOptimizer.Editor.Import;
using UnityEditor;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Apply
{
    public static class ApplyStageImpl
    {
        public static void Execute(ATOContext ctx, BuildContext context)
        {
            var an = ctx.Analysis;
            var log = ctx.Log;
            if (an == null) return;

            ctx.Session.Check("Apply 应用");

            // 1. save new textures  1. 保存新贴图
            var saved = new List<Object>();
            if (an.PackedResult != null)
            {
                foreach (var page in an.PackedResult.Pages)
                {
                    if (page.Texture == null) continue;
                    context.AssetSaver.SaveAsset(page.Texture);
                    saved.Add(page.Texture);
                }
            }
            foreach (var (tid, scaled) in an.ScaledTextures)
            {
                if (scaled == null) continue;
                context.AssetSaver.SaveAsset(scaled);
                saved.Add(scaled);
            }
            log.V(ATOLogMask.Atlas, $"saved {saved.Count} new textures. 已保存新贴图。");

            // 2. prepare shared assets  2. 准备共享资产
            var meshMap = PrepareMeshes(ctx, context, an);
            var matMap = PrepareMaterials(ctx, context, an);

            // 3. UV remap  3. UV 重映射
            UVRemapper.Remap(ctx);

            // 4. material dedup + renderer plans  4. 材质去重 + 渲染器计划
            foreach (var (mat, rep) in an.MaterialDedupMap)
            {
                if (mat == null || rep == null) continue;
                ObjectRegistry.RegisterReplacedObject(mat, rep);
            }
            foreach (var (r, plan) in an.RendererPlans)
            {
                if (r == null) continue;
                ctx.Session.Check("Apply 应用");
                if (plan.Mesh != null)
                {
                    if (meshMap.TryGetValue(plan.Mesh, out var ensuredMesh))
                    {
                        plan.Mesh = ensuredMesh;
                    }
                    r.sharedMesh = plan.Mesh;
                    if (plan.Modified)
                    {
                        context.AssetSaver.SaveAsset(plan.Mesh);
                    }
                }
                if (plan.Materials != null)
                {
                    var cur = r.sharedMaterials;
                    var mats = new Material[plan.Materials.Length];
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = plan.Materials[i];
                        if (m == null)
                        {
                            mats[i] = i < cur.Length ? cur[i] : null;
                        }
                        else
                        {
                            mats[i] = matMap.TryGetValue(m, out var mc) ? mc : m;
                        }
                    }
                    r.sharedMaterials = mats;
                }
            }

            // 5. final texture assignment (on the cloned materials)
            //    5. 最终贴图赋值（在克隆材质上）
            foreach (var (mat, info) in an.Materials)
            {
                if (mat == null) continue;
                if (!matMap.TryGetValue(mat, out var matClone)) matClone = mat;
                foreach (var (prop, tex) in info.Textures)
                {
                    if (!an.FinalTextures.TryGetValue((mat, prop), out var final)) continue;
                    if (final == null || final == tex) continue;
                    matClone.SetTexture(prop, final); // texture slot ONLY 仅贴图槽
                }
            }

            // 6. import settings  6. 导入设置
            foreach (var (tex, plan) in an.ImportPlans)
            {
                ctx.Session.Check("Apply 应用");
                if (tex == null) continue;
                // user-owned textures: clone before changing importer settings
                // 用户拥有的贴图：修改导入设置前先克隆
                Texture2D target = tex;
                bool owned = !context.IsTemporaryAsset(tex);
                bool isAtlas = tex.name.StartsWith("ATO_", System.StringComparison.Ordinal);
                if (owned && !isAtlas && ImportDiffers(context, tex, plan))
                {
                    target = CloneTexture(ctx, context, tex, log);
                }
                ApplyImport(ctx, target, plan);
            }

            // 7. animation rewrite  7. 动画改写
            var rewriter = new AnimationRewriter.Rewriter(an);
            AnimationRewriter.Rewrite(ctx, rewriter);

            // 8. self-removal  8. 自移除
            if (ctx.Component != null)
            {
                Object.DestroyImmediate(ctx.Component);
                log.V(ATOLogMask.Verbose, "ATO component removed from processed avatar. ATO 组件已移除。");
            }

            log.Info(ATOLogMask.Atlas,
                $"apply done: {meshMap.Count} meshes ensured, {matMap.Count} materials ensured. " +
                "应用完成。");
        }

        // ------------------------------------------------------------------
        /// <summary>Clones shared meshes that receive UV remaps; updates all
        /// ATOMeshUVSet references. 克隆接收 UV 重映射的共享网格；更新全部
        /// ATOMeshUVSet 引用。</summary>
        private static Dictionary<Mesh, Mesh> PrepareMeshes(
            ATOContext ctx, BuildContext context, ATOAnalysis an)
        {
            var map = new Dictionary<Mesh, Mesh>(new ObjectIdentityEqualityComparer());
            var needed = new HashSet<Mesh>(new ObjectIdentityEqualityComparer());
            foreach (var island in an.Islands)
            {
                if (island.AtlasPage >= 0 && island.UVSet.Mesh != null)
                {
                    needed.Add(island.UVSet.Mesh);
                }
            }
            foreach (var m in needed)
            {
                if (context.IsTemporaryAsset(m))
                {
                    map[m] = m;
                    continue;
                }
                var copy = (Mesh) Object.Instantiate(m);
                copy.name = m.name + "_ATO";
                ObjectRegistry.RegisterReplacedObject(m, copy);
                context.AssetSaver.SaveAsset(copy);
                map[m] = copy;
            }
            foreach (var set in an.MeshUVSets)
            {
                if (set.Mesh != null && map.TryGetValue(set.Mesh, out var nm))
                {
                    set.Mesh = nm;
                }
            }
            return map;
        }

        /// <summary>Clones shared materials (they receive texture slot
        /// updates). 克隆共享材质（它们接收贴图槽更新）。</summary>
        private static Dictionary<Material, Material> PrepareMaterials(
            ATOContext ctx, BuildContext context, ATOAnalysis an)
        {
            var map = new Dictionary<Material, Material>(new ObjectIdentityEqualityComparer());
            var list = new List<Material>(an.Materials.Keys);
            foreach (var m in list)
            {
                if (m == null) continue;
                if (map.ContainsKey(m)) continue;
                if (context.IsTemporaryAsset(m))
                {
                    map[m] = m;
                    continue;
                }
                var copy = (Material) Object.Instantiate(m);
                copy.name = m.name + "_ATO";
                ObjectRegistry.RegisterReplacedObject(m, copy);
                context.AssetSaver.SaveAsset(copy);
                map[m] = copy;
            }
            return map;
        }

        // ------------------------------------------------------------------
        /// <summary>True when applying the plan would change the current
        /// importer state. 应用计划是否会改变当前导入器状态。</summary>
        private static bool ImportDiffers(BuildContext context, Texture2D tex, ATOImportPlan plan)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return true;
            if (!AssetImporter.GetAtPath(path, out var importer) || !(importer is TextureImporter ti))
            {
                return true;
            }
            bool srgb = plan.Category == ATOTextureCategory.Normal ? false :
                        plan.Category == ATOTextureCategory.Gray ? ti.sRGB : true;
            if (ti.mipMap != plan.Mipmaps) return true;
            if (ti.sRGB != srgb) return true;
            var name = ImportStageImpl.PlatformSettingsName(EditorUserBuildSettings.activeBuildTarget);
            var cur = ti.GetPlatformTextureSettings(name);
            if (cur != null && cur.overriden && cur.format != plan.Format) return true;
            return false;
        }

        /// <summary>Clones a texture asset (pixel copy) and registers the
        /// replacement. 克隆贴图资产（像素拷贝）并注册替换。</summary>
        private static Texture2D CloneTexture(
            ATOContext ctx, BuildContext context, Texture2D tex, ATOLog log)
        {
            try
            {
                var copy = new Texture2D(tex.width, tex.height, tex.format,
                    tex.mipmapCount > 1);
                copy.name = tex.name + "_ATO";
                copy.filterMode = tex.filterMode;
                copy.wrapMode = tex.wrapMode;
                copy.SetPixels(tex.GetPixels());
                copy.Apply(false, true);
                context.AssetSaver.SaveAsset(copy);
                ObjectRegistry.RegisterReplacedObject(tex, copy);
                log.V(ATOLogMask.Import,
                    $"cloned texture \"{tex.name}\" for import settings. 已克隆贴图用于导入设置。");
                return copy;
            }
            catch (System.Exception e)
            {
                log.Warn(ATOLogMask.Import,
                    $"could not clone texture \"{tex.name}\" - keeping original: {e.Message} " +
                    "无法克隆贴图，保留原贴图。");
                return tex;
            }
        }

        // ------------------------------------------------------------------
        private static void ApplyImport(ATOContext ctx, Texture2D tex, ATOImportPlan plan)
        {
            var log = ctx.Log;
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path))
            {
                return; // in-memory  内存中
            }
            if (!AssetImporter.GetAtPath(path, out var importer) || !(importer is TextureImporter ti))
            {
                return;
            }

            // sRGB  sRGB
            bool srgb = plan.Category == ATOTextureCategory.Normal ? false :
                        plan.Category == ATOTextureCategory.Gray ? ti.sRGB : true;
            ti.sRGB = srgb;

            // texture type  贴图类型
            ti.textureType = plan.Category == ATOTextureCategory.Normal
                ? TextureImporterType.Normal
                : TextureImporterType.Default;

            // wrap mode / readability: atlas pages forced Clamp + no Read/Write
            // 图集页强制 Clamp + 关闭 Read/Write
            bool isAtlas = tex.name.StartsWith("ATO_", System.StringComparison.Ordinal);
            if (isAtlas)
            {
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.isReadable = false;
            }

            // filter mode: NPOT requires bilinear  NPOT 需要双线性
            if (plan.NpotAllowed && ti.filterMode == FilterMode.Point)
            {
                ti.filterMode = FilterMode.Bilinear;
            }

            // mipmaps + mipstreaming bound  mipmap + mipstreaming 绑定
            ti.mipMap = plan.Mipmaps;

            // platform compression format  平台压缩格式
            var target = EditorUserBuildSettings.activeBuildTarget;
            var plat = new TextureImporterPlatformSettings
            {
                name = ImportStageImpl.PlatformSettingsName(target),
                overriden = true,
                format = plan.Format,
                textureCompressionQuality = 50,
            };
            ti.SetPlatformTextureSettings(plat);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            // VRChat requires mip streaming on textures with mipmaps: the flag
            // lives on the Texture2D asset (m_StreamingMipmaps).
            // VRChat 要求带 mipmap 的贴图开启 MipStreaming：标志位于贴图资产上。
            try
            {
                var so = new SerializedObject(tex);
                var p = so.FindProperty("m_StreamingMipmaps");
                if (p != null)
                {
                    p.boolValue = plan.Mipmaps;
                    so.ApplyModifiedProperties();
                }
            }
            catch (System.Exception e)
            {
                log.V(ATOLogMask.Import, "mip streaming flag unavailable: " + e.Message);
            }
        }
    }
}
