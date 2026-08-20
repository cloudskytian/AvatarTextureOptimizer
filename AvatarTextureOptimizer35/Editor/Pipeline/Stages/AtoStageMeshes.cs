using System;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Stage: mesh & UV rewrite. / 阶段：网格与 UV 重写。
    /// For each renderer with atlased UV groups: clones the mesh, rewrites the UVs (translation,
    /// shrink, rotation, placement), applies AAO evacuation, and swaps the mesh. /
    /// 对有图集化 UV 组的渲染器：克隆网格、重写 UV（平移/缩放/旋转/放置）、应用 AAO 疏散并替换网格。
    /// </summary>
    internal sealed class AtoStageMeshes : IAtoStage
    {
        public string I18nKey => "meshes";

        public void Run(AtoContext ctx)
        {
            var rendererIndex = 0;
            foreach (var data in ctx.Renderers)
            {
                ctx.State.SetProgress($"rewriting {data.Renderer.name}",
                    (float)rendererIndex / Mathf.Max(1, ctx.Renderers.Count));
                ctx.State.ThrowIfCancelled();

                var newMesh = AtoMeshRewriter.Rewrite(ctx, data);
                if (newMesh != null)
                {
                    AtoLog.Info($"[ATO] mesh rewritten: {data.Renderer.name} ({newMesh.name})");
                }
                rendererIndex++;
            }
        }
    }
}
