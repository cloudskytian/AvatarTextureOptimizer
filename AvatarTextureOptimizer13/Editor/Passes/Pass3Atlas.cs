// ATO — Avatar Texture Optimizer
// Pass 3 — atlas: packs type groups with the BLF packer, bakes the atlases and saves them
// with the configured import settings.
// Pass 3——图集：用 BLF 装箱器对类型组装箱，烘焙图集并按配置的导入设置保存。

using System.Collections.Generic;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Pass 3 — atlas generation. Pass 3——图集生成。
    /// </summary>
    public class Pass3Atlas : ATOBasePass<Pass3Atlas>
    {
        protected override void Process(ATOBuildContext bc, nadena.dev.ndmf.BuildContext context)
        {
            var result = bc.Result;
            if (result == null || !result.didAnything) return;
            if (!result.settings.generateAtlas) return; // whole-texture path in Pass 4. 整图路径在 Pass 4。

            RunStage(bc, ATOI18nKeys.StageAtlas, result.typeGroups.Count, () =>
            {
                var dropped = new List<ATOUVGroup>();
                var packResults = AtlasPacker.Pack(bc, result, dropped);

                var atlases = AtlasBaker.Bake(bc, result, packResults);
                result.atlases = atlases;

                // Save each atlas asset and swap in the imported (compressed) texture.
                // 保存每个图集资产，替换为导入（压缩）后的贴图。
                int saved = 0;
                foreach (var atlas in atlases)
                {
                    bc.ThrowIfCancelled();
                    var imported = TextureSettingsApplier.SaveAtlas(bc, atlas, atlas.transparent, atlas.sources);
                    if (imported != null)
                    {
                        UnityEngine.Object.DestroyImmediate(atlas.texture);
                        atlas.texture = imported;
                    }
                    bc.ReportProgress(saved++);
                }

                // Dropped UV groups keep their scaled-in-place UVs; MeshRewriter falls back to
                // scaledUV for islands without an atlas placement, so no extra flag is needed.
                // 被放弃的 UV 组保持原地缩放的 UV；MeshRewriter 对无图集放置的岛回退到 scaledUV，
                // 因此无需额外标记。
                bc.Report.AddDetail($"[Atlas] generated {atlases.Count} atlases, dropped {dropped.Count} UV groups (scaled in place).");
            });

            bc.ClearCaches();
        }

        protected override void ReleaseResources(ATOBuildContext bc)
        {
            bc.ClearCaches();
        }
    }
}
