using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Root component. Exactly one per avatar, must sit on VRCAvatarDescriptor.
    /// 根组件：每个 Avatar 仅允许一个，且必须挂在带 VRCAvatarDescriptor 的对象上。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FOSA/Avatar Texture Optimizer")]
    [HelpURL("https://github.com/fosa/avatar-texture-optimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour, IEditorOnly
    {
        [Tooltip("Whitelist objects (mesh/material/texture/clip/…). All textures they reference skip optimization.")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        public bool optimizeTextures = true;
        public bool optimizeMaterials = true;

        [Header("Common / 通用")]
        public AtoPlatformSettings common = new AtoPlatformSettings();

        public bool enablePcOverride;
        public AtoPlatformSettings pc = new AtoPlatformSettings();
        public bool enableAndroidOverride;
        public AtoPlatformSettings android = new AtoPlatformSettings();
        public bool enableIosOverride;
        public AtoPlatformSettings ios = new AtoPlatformSettings();

        [Header("Localization / 本地化")]
        public string language = "Auto";

        [Header("Debug / 调试")]
        public bool verboseLogs = true;

        public AtoPlatformSettings Resolve(AtoPlatform platform)
        {
            switch (platform)
            {
                case AtoPlatform.PC when enablePcOverride:
                    return pc;
                case AtoPlatform.Android when enableAndroidOverride:
                    return android;
                case AtoPlatform.iOS when enableIosOverride:
                    return ios;
                default:
                    return common;
            }
        }

        private void Reset()
        {
            common.ApplyPresetIfNotCustom();
            pc.ApplyPresetIfNotCustom();
            android.ApplyPresetIfNotCustom();
            ios.ApplyPresetIfNotCustom();
        }

        private void OnValidate()
        {
            common.ApplyPresetIfNotCustom();
            if (enablePcOverride) pc.ApplyPresetIfNotCustom();
            if (enableAndroidOverride) android.ApplyPresetIfNotCustom();
            if (enableIosOverride) ios.ApplyPresetIfNotCustom();
        }
    }
}
