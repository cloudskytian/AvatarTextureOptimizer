using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Custom inspector for the optimizer component. / 优化组件自定义 Inspector。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var comp = (AvatarTextureOptimizer)target;

            EditorGUILayout.HelpBox(
                "Optimizes this avatar's textures: UV-island quality scaling, type-grouped atlas packing, and safe deduplication. " +
                "此组件优化本 Avatar 的贴图：UV 岛质量缩放、按类型组图集装箱与安全去重。",
                MessageType.Info);

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("general"), true);

            var overrideProp = serializedObject.FindProperty("enablePlatformOverride");
            EditorGUILayout.PropertyField(overrideProp, new GUIContent("Platform Override / 分平台覆盖"));
            if (overrideProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("pc"), true);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("android"), true);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("ios"), true);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("language"),
                new GUIContent("Language / 语言"));

            serializedObject.ApplyModifiedProperties();

            // validation hints / 校验提示
            var descriptor = comp.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                EditorGUILayout.HelpBox(
                    "This component must be on the same GameObject as a VRCAvatarDescriptor. " +
                    "此组件必须与 VRCAvatarDescriptor 位于同一物体。",
                    MessageType.Error);
            }
            var count = comp.GetComponentsInChildren<AvatarTextureOptimizer>(true).Length;
            if (count > 1)
            {
                EditorGUILayout.HelpBox(
                    "Only one AvatarTextureOptimizer is allowed per avatar. " +
                    "一个 Avatar 只允许挂载一个 AvatarTextureOptimizer。",
                    MessageType.Error);
            }
        }
    }
}
