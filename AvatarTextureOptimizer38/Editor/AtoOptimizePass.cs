using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Single NDMF pass that runs the full ATO pipeline. / 执行完整 ATO 管线的 NDMF Pass。
    /// </summary>
    public sealed class AtoOptimizePass : Pass<AtoOptimizePass>
    {
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootObject;
            if (root == null) return;

            var components = root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (components == null || components.Length == 0)
            {
                AtoLog.VerboseLog("No AvatarTextureOptimizer component; skip.");
                return;
            }

            try
            {
                OptimizePipeline.Run(context, components);
            }
            catch (AtoCanceledException)
            {
                AtoLog.Warn("Canceled. / 已取消。");
                throw;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                TextureDecodeCache.DisposeAll();
                GpuUtil.ReleaseScratch();
            }
        }
    }
}
