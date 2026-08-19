using UnityEditor;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;
#if ATO_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace FOSA.AvatarTextureOptimizer.Editor
{
    internal static class ATOMenu
    {
        [MenuItem("GameObject/FOSA/Avatar Texture Optimizer", false, 20)]
        private static void AddComponent()
        {
            var go = Selection.activeGameObject;
            if (go == null)
            {
                EditorUtility.DisplayDialog("ATO", ATOLoc.T("ato.error.need_descriptor"), "OK");
                return;
            }
#if ATO_VRCSDK3_AVATARS
            if (go.GetComponent<VRCAvatarDescriptor>() == null)
            {
                EditorUtility.DisplayDialog("ATO", ATOLoc.T("ato.error.need_descriptor"), "OK");
                return;
            }
#endif
            if (go.GetComponent<AvatarTextureOptimizer>() == null)
                Undo.AddComponent<AvatarTextureOptimizer>(go);
            else
                EditorUtility.DisplayDialog("ATO", ATOLoc.T("ato.error.multiple_components"), "OK");
        }

        [MenuItem("GameObject/FOSA/Avatar Texture Optimizer", true)]
        private static bool Validate() => Selection.activeGameObject != null;
    }
}
