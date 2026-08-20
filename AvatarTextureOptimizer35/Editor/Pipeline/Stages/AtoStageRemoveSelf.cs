using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Stage: remove ATO's own components from the baked avatar. / 阶段：从烘焙成品上移除 ATO 自身组件。
    /// </summary>
    internal sealed class AtoStageRemoveSelf : IAtoStage
    {
        public string I18nKey => "removeSelf";

        public void Run(AtoContext ctx)
        {
            var removed = 0;
            foreach (var component in ctx.AvatarRoot.GetComponentsInChildren<AtoAvatarRoot>(true))
            {
                if (component == null) continue;
                Object.DestroyImmediate(component);
                removed++;
            }
            if (removed > 0)
            {
                AtoLog.Info($"[ATO] removed {removed} ATO component(s) from the baked avatar.");
            }
        }
    }
}
