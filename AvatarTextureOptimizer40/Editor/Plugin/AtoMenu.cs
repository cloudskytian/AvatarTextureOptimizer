using Fosa.Ato.Runtime;
using UnityEditor;
using UnityEngine;
#if ATO_VRCSDK_INSTALLED
using VRC.SDK3.Avatars.Components;
#endif

namespace Fosa.Ato.Editor.Plugin
{
    /// <summary>Convenience menu to add the component to the selected avatar root. / 便捷菜单：为选中 Avatar 根节点添加组件。</summary>
    internal static class AtoMenu
    {
        [MenuItem("Tools/Avatar Texture Optimizer/Add to selected avatar", false, 0)]
        private static void AddToSelected()
        {
            var go = Selection.activeGameObject;
            if (go == null) { EditorUtility.DisplayDialog("ATO", "Select an avatar root first.", "OK"); return; }
            if (go.GetComponent<AvatarTextureOptimizer>() != null) { EditorUtility.DisplayDialog("ATO", "Already added.", "OK"); return; }
#if ATO_VRCSDK_INSTALLED
            if (go.GetComponent<VRCAvatarDescriptor>() == null)
            {
                if (!EditorUtility.DisplayDialog("ATO", "The selected object has no VRCAvatarDescriptor. Add anyway?", "Add", "Cancel"))
                    return;
            }
#endif
            Undo.AddComponent<AvatarTextureOptimizer>(go);
        }

        [MenuItem("Tools/Avatar Texture Optimizer/Add to selected avatar", true)]
        private static bool Validate() => Selection.activeGameObject != null;
    }
}
