// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using AvatarTextureOptimizer.Editor.Core;
using AvatarTextureOptimizer.Editor.ShaderAnalysis;
using nadena.dev.ndmf;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Passes
{
    /// <summary>
    /// Pass 3 — pre-analyze all shaders used by collected materials and surface warnings
    /// for unsupported shaders early.
    ///
    /// Pass 3 —— 预分析收集到的材质所用的全部着色器，尽早输出不支持着色器的警告。
    /// </summary>
    public sealed class ATOShaderAnalysisPass : Pass<ATOShaderAnalysisPass>
    {
        public override string DisplayName => "ATO: Analyze shaders / 分析着色器";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ATOBuildState>();
            if (state.Component == null) return;
            state.BeginStage("Analyze shaders / 分析着色器");

            var seen = new HashSet<Shader>();
            int unsupported = 0;

            foreach (var matRec in state.Materials.Values)
            {
                var shader = matRec.Material?.shader;
                if (shader == null || !seen.Add(shader)) continue;

                var info = ATOShaderAnalyzerRegistry.Analyze(shader);
                if (info.Unsupported)
                {
                    unsupported++;
                    ATOLog.Warning($"Shader '{shader.name}' unsupported: {info.UnsupportedReason}. " +
                                   $"Its textures will be whitelisted. / 着色器不支持，其贴图列入白名单。");
                }
                else
                {
                    ATOLog.Verbose($"Shader '{shader.name}': {info.Textures.Count} texture properties analyzed. / " +
                                   $"已分析 {info.Textures.Count} 个贴图属性。");
                }
            }

            if (unsupported > 0)
                ATOLog.Info($"{unsupported} unsupported shader(s). / {unsupported} 个不支持着色器。");
        }
    }
}
