// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Linq;
using AvatarTextureOptimizer.Editor.Localization;
using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.UI
{
    /// <summary>
    /// Custom inspector for the main component. Adds the language switch (Auto follows
    /// NDMF) and organized foldouts. Most fields already carry bilingual tooltips.
    ///
    /// 主组件自定义 Inspector。新增语言切换（Auto 跟随 NDMF）与分组折叠。
    /// 大多数字段已带双语 Tooltip。
    /// </summary>
    [CustomEditor(typeof(ATOAvatarTextureOptimizer))]
    public sealed class ATOAvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var comp = (ATOAvatarTextureOptimizer)target;
            var T = new System.Func<string, string>(ATOI18n.T);

            // Language. 语言。
            ATOI18n.Load();
            var langs = ATOI18n.AvailableLanguages.ToList();
            string[] options = new[] { "Auto" }.Concat(langs).ToArray();

            int selected = 0;
            if (!string.IsNullOrEmpty(ATOI18n.OverrideLanguage))
                selected = System.Math.Max(0, langs.IndexOf(ATOI18n.OverrideLanguage) + 1);

            EditorGUI.BeginChangeCheck();
            int newSel = EditorGUILayout.Popup(T("language"), selected, options);
            if (EditorGUI.EndChangeCheck())
                ATOI18n.OverrideLanguage = newSel <= 0 ? null : langs[newSel - 1];

            EditorGUILayout.Space();

            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("generateAtlas"),
                new GUIContent(T("generateAtlas"), T("generateAtlas.tooltip")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("minPixelDensity"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxPixelDensity"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("quality"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("atlasPadding"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("allowNPOT"));

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("compression"), true);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("platformOverride"), true);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("whitelist"),
                new GUIContent(T("whitelist"), T("whitelist.tooltip")), true);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("deduplicateMaterials"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("deduplicateTextures"));

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("logLevel"));

            serializedObject.ApplyModifiedProperties();
        }
    }
}
