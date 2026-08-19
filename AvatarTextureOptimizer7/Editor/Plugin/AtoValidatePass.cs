using System.Text;
using Fosa.AvatarTextureOptimizer;
using nadena.dev.ndmf;
using UnityEngine;
#if ATO_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Early check: one component, must live with VRCAvatarDescriptor.
    /// Illegal mounts abort the bake.
    /// 早期校验：只能有一个组件，且必须与 VRCAvatarDescriptor 同物体。不合规则中止烘焙。
    /// </summary>
    public sealed class AtoValidatePass : Pass<AtoValidatePass>
    {
        public override string DisplayName => "ATO Validate Component";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootObject;
            if (root == null) return;

            var comps = root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (comps == null || comps.Length == 0) return;

            var lang = comps[0] != null ? comps[0].language : AtoLanguageMode.Auto;

            if (comps.Length > 1)
            {
                var sb = new StringBuilder();
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(c.gameObject.name);
                }

                ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Error, "error.multiple", sb.ToString());
                throw new System.InvalidOperationException("[ATO] Multiple AvatarTextureOptimizer components on avatar");
            }

            var ato = comps[0];
#if ATO_VRCSDK3_AVATARS
            var desc = ato.GetComponent<VRCAvatarDescriptor>();
            if (desc == null)
            {
                ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Error, "error.noDescriptor");
                throw new System.InvalidOperationException("[ATO] Component is not on a VRCAvatarDescriptor object");
            }
#else
            Debug.LogWarning("[ATO] VRChat Avatars SDK not present; descriptor check skipped.");
#endif
            _ = lang;
        }
    }
}
