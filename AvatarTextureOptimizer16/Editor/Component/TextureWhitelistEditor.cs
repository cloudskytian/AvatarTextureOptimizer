using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>Custom inspector for the whitelist component. / 白名单组件自定义 Inspector。</summary>
    [CustomEditor(typeof(TextureWhitelist))]
    public sealed class TextureWhitelistEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var comp = (TextureWhitelist)target;
            EditorGUILayout.HelpBox(
                "Textures referenced by the listed objects skip all optimization. " +
                "列出的对象所引用的贴图跳过所有优化。",
                MessageType.Info);

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("objects"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("includeChildren"), true);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
