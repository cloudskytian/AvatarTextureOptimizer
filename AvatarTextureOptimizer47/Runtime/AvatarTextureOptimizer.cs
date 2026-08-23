using nadena.dev.ndmf;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// EN: Adds non-destructive texture optimization to the avatar containing this component.
    /// ZH: 为包含本组件的 Avatar 添加非破坏性贴图优化。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Avatar Texture Optimizer/Avatar Texture Optimizer")]
    public sealed class AvatarTextureOptimizer : MonoBehaviour, INDMFEditorOnly
    {
        public OptimizerSettings settings = new OptimizerSettings();

        private void Reset()
        {
            settings = new OptimizerSettings();
            settings.Validate();
        }

        private void OnValidate()
        {
            if (settings == null) settings = new OptimizerSettings();
            settings.Validate();
        }
    }
}
