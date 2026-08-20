using System.Linq;
using UnityEngine;
using Fosa.Ato.Editor.Analysis;

namespace Fosa.Ato.Editor.Pipeline.Stages
{
    /// <summary>
    /// Stage 01: validate single-component rule, collect enabled/animated renderers, build the
    /// whitelist set. EditorOnly renderers are skipped (NDMF also removes them in Resolving).
    /// 阶段 01：校验单组件规则、收集启用或动画启用的渲染器、构建白名单集合；跳过 EditorOnly。
    /// </summary>
    internal sealed class Stage01Collect : IStage
    {
        public string Name => "ATO/01 Collecting renderers";
        public float Weight => 1f;
        public void Run(AtoPipeline p)
        {
            var root = p.Ctx.AvatarRootObject;
            p.Progress.Stage(Name, 0f);

            // Whitelist: the component's referenced objects plus anything tagged EditorOnly.
            // 白名单：组件引用对象 + EditorOnly 标记对象
            foreach (var o in p.Component.Whitelist)
                if (o != null) p.Whitelist.Add(o);

            var renderers = root.GetComponentsInChildren<Renderer>(true)
                .Where(r => r is SkinnedMeshRenderer || r is MeshRenderer)
                .Where(r => !r.CompareTag("EditorOnly"))
                .ToList();

            p.Report.RendererCount = renderers.Count;
            AtoLog.VIf(p.Settings.VerboseLogging, $"Collected {renderers.Count} active/eligible renderers.");

            // Stash for later stages via a tiny state object / 通过状态对象暂存供后续阶段使用
            p.GetState<CollectState>().Renderers = renderers;
        }
    }

    internal sealed class CollectState { public System.Collections.Generic.List<Renderer> Renderers = new(); }
}
