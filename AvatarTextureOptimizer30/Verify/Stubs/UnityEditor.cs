// 编译验证桩：UnityEditor 最小 API 表面 / Compile-check stubs: minimal UnityEditor API surface.
// 仅用于编译验证，不做运行时语义模拟。Not shipped with the package.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEditor
{
    public class AssetDatabase
    {
        public static string GetAssetPath(UnityEngine.Object assetObject) => "";
        public static string[] FindAssets(string filter, string[] searchInFolders = null) => null;
        public static string GUIDToAssetPath(string guid) => "";
        public static T LoadAssetAtPath<T>(string assetPath) where T : UnityEngine.Object => default;
        public static UnityEngine.Object LoadAssetAtPath(string assetPath, Type type) => null;
        public static string GenerateUniqueAssetPath(string path) => path;
        public static void ImportAsset(string path, ImportAssetOptions options = ImportAssetOptions.Default) { }
        public static bool IsMainAsset(UnityEngine.Object obj) => true;
    }

    [Flags]
    public enum ImportAssetOptions
    {
        Default = 0,
        ForceUpdate = 1,
        ForceSynchronousImport = 8,
        DontDownloadFromCacheServer = 8192,
    }

    public class AssetImporter : UnityEngine.Object
    {
        public static AssetImporter GetAtPath(string path) => null;
        public void SaveAndReimport() { }
    }

    public class TextureImporter : AssetImporter
    {
        public bool sRGBTexture { get; set; }
        public FilterMode filterMode { get; set; }
        public TextureWrapMode wrapMode { get; set; }
        public bool mipmapEnabled { get; set; }
        public bool streamingMipmaps { get; set; }
        public int streamingMipmapsPriority { get; set; }
        public bool isReadable { get; set; }
        public int anisoLevel { get; set; }
        public int maxTextureSize { get; set; }
        public TextureImporterType textureType { get; set; }
        public TextureImporterNPOTScale npotScale { get; set; }
        public TextureImporterMipFilter mipmapFilter { get; set; }
        public bool crunchedCompression { get; set; }
        public int compressionQuality { get; set; }
        public TextureImporterCompression textureCompression { get; set; }
        public void SetPlatformTextureSettings(TextureImporterPlatformSettings settings) { }
        public TextureImporterPlatformSettings GetPlatformTextureSettings(string platform) => default;
        public TextureImporterFormat GetAutomaticFormat(string platform) => default;
    }

    public enum TextureImporterType { Default = 0, NormalMap = 1, GUI = 2, Sprite = 8, SingleChannel = 10 }
    public enum TextureImporterNPOTScale { None = 0, ToNearest = 1, ToLarger = 2, ToSmaller = 3 }
    public enum TextureImporterMipFilter { BoxFilter = 0, KaiserFilter = 1 }
    public enum TextureImporterCompression { Uncompressed = 0, Compressed = 1, CompressedHQ = 2, CompressedLQ = 3 }
    public enum TextureImporterFormat
    {
        Automatic = -1, RGBA32 = 4, RGB24 = 3, DXT1 = 10, DXT5 = 12, BC4 = 26, BC5 = 27, BC7 = 25,
        ETC_RGB4 = 34, ETC2_RGB4 = 45, ETC2_RGBA8 = 47, ASTC_4x4 = 48, ASTC_6x6 = 50, ASTC_8x8 = 52,
        ASTC_12x12 = 54, PVRTC_RGB4 = 30, PVRTC_RGBA4 = 32, R8 = 63, RG16 = 64,
    }

    public struct TextureImporterPlatformSettings
    {
        public string name;
        public bool overridden;
        public int maxTextureSize;
        public TextureImporterCompression textureCompression;
        public TextureImporterFormat format;
    }

    public class SerializedObject : IDisposable
    {
        public SerializedObject(UnityEngine.Object obj) { }
        public SerializedProperty FindProperty(string propertyPath) => null;
        public void Update() { }
        public void ApplyModifiedProperties() { }
        public void ApplyModifiedPropertiesWithoutUndo() { }
        public void Dispose() { }
        public UnityEngine.Object targetObject => null;
    }

    public class SerializedProperty
    {
        public string stringValue { get; set; }
        public float floatValue { get; set; }
        public int intValue { get; set; }
        public bool boolValue { get; set; }
        public Color colorValue { get; set; }
        public Vector4 vector4Value { get; set; }
        public UnityEngine.Object objectReferenceValue { get; set; }
        public bool isArray { get; }
        public int arraySize { get; set; }
        public SerializedPropertyType propertyType { get; }
        public SerializedProperty GetArrayElementAtIndex(int index) => null;
        public SerializedProperty FindPropertyRelative(string relativePropertyPath) => null;
    }

    public enum SerializedPropertyType
    {
        Generic = -1, Integer = 0, Boolean = 1, Float = 2, String = 3, Color = 4,
        ObjectReference = 5, Vector2 = 7, Vector3 = 8, Vector4 = 9, Rect = 10,
        ArraySize = 12, Enum = 15,
    }

    public static class AnimationUtility
    {
        public static EditorCurveBinding[] GetCurveBindings(AnimationClip clip) => null;
        public static EditorCurveBinding[] GetObjectReferenceCurveBindings(AnimationClip clip) => null;
        public static AnimationCurve GetEditorCurve(AnimationClip clip, EditorCurveBinding binding) => null;
        public static void SetEditorCurve(AnimationClip clip, EditorCurveBinding binding, AnimationCurve curve) { }
        public static ObjectReferenceKeyframe[] GetObjectReferenceCurve(AnimationClip clip, EditorCurveBinding binding) => null;
        public static void SetObjectReferenceCurve(AnimationClip clip, EditorCurveBinding binding, ObjectReferenceKeyframe[] keyframes) { }
    }

    public struct EditorCurveBinding
    {
        public string path;
        public Type type;
        public string propertyName;
    }

    public struct ObjectReferenceKeyframe
    {
        public float time;
        public UnityEngine.Object value;
    }

    public class Editor : ScriptableObject
    {
        public SerializedObject serializedObject { get; }
        public UnityEngine.Object target { get; }
        public virtual void OnInspectorGUI() { }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class CustomEditor : Attribute
    {
        public CustomEditor(Type inspectedType) { }
        public CustomEditor(Type inspectedType, bool editorForChildClasses) { }
    }

    public class InitializeOnLoadMethodAttribute : Attribute { }

    public static class EditorUtility
    {
        public static bool DisplayCancelableProgressBar(string title, string info, float progress) => false;
        public static void ClearProgressBar() { }
        public static void SetDirty(UnityEngine.Object target) { }
        public static bool IsPersistent(UnityEngine.Object obj) => false;
    }

    public static class EditorGUILayout
    {
        public static void HelpBox(string message, MessageType type, bool wide = true) { }
        public static void Space(float pixels = 6f) { }
        public static void BeginHorizontal(params GUILayoutOption[] options) { }
        public static void EndHorizontal() { }
        public static void LabelField(string label, params GUILayoutOption[] options) { }
        public static void LabelField(string label, GUIStyle style, params GUILayoutOption[] options) { }
        public static bool Foldout(bool foldout, string content, bool toggleOnLabelClick = true) => foldout;
        public static int IntPopup(int selectedValue, string[] displayedOptions, int[] optionValues, params GUILayoutOption[] options) => selectedValue;
        public static int Popup(int selectedIndex, string[] displayedOptions, params GUILayoutOption[] options) => selectedIndex;
        public static int Popup(string label, int selectedIndex, string[] displayedOptions, params GUILayoutOption[] options) => selectedIndex;
        public static bool ToggleLeft(string label, bool value, params GUILayoutOption[] options) => value;
        public static bool PropertyField(SerializedProperty property, params GUILayoutOption[] options) => true;
        public static bool PropertyField(SerializedProperty property, GUIContent label, params GUILayoutOption[] options) => true;
        public static bool PropertyField(SerializedProperty property, GUIContent label, bool includeChildren, params GUILayoutOption[] options) => true;
        public static void IndentLevel(int v) { }
    }

    public static class EditorGUI
    {
        public static int indentLevel { get; set; }
        public static void BeginChangeCheck() { }
        public static bool EndChangeCheck() => false;
        public static void IndentLevel() { }
    }

    public static class GUILayout
    {
        public static GUILayoutOption Width(float width) => null;
        public static GUILayoutOption Height(float height) => null;
        public static GUILayoutOption ExpandWidth(bool expand) => null;
    }

    public class GUILayoutOption { }
    public class GUIContent
    {
        public GUIContent(string text) { }
        public GUIContent(string text, string tooltip) { }
        public string text;
    }

    public class GUIStyle { }
    public static class EditorStyles
    {
        public static GUIStyle boldLabel => null;
    }

    public enum MessageType { None = 0, Info = 1, Warning = 2, Error = 3 }
}
