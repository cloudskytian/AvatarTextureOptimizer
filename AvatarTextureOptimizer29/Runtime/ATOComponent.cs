// ATO avatar component. Add exactly one to the VRCAvatarDescriptor object.
// ATO Avatar 组件：在挂有 VRCAvatarDescriptor 的对象上添加，且全 Avatar 仅允许一个。
//
// English: Runtime-side configuration holder. All heavy logic lives in Editor/.
// 中文：运行时仅承载配置，全部处理逻辑在 Editor/。

using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace net.fosa.ato
{
    [AddComponentMenu("ATO/Avatar Texture Optimizer (ATO)")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosa/avatar-texture-optimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour, IEditorOnly
    {
        public const string PluginQualifiedName = "net.fosa.avatar-texture-optimizer";

        // ---------- master toggles / 主开关 ----------
        // Generate atlas (repack islands, strip unused UV areas, rewrite UVs).
        // 生成图集（重排岛、剔除未用UV、重写UV）。关闭时仅整图缩放+参数优化。
        public bool generateAtlas = true;

        // Dedup textures by content+import settings; dedup materials by full equality.
        // 贴图（内容+导入设置）/材质去重开关。
        public bool dedupTextures = true;
        public bool dedupMaterials = true;

        // Whitelist: every texture referenced by these objects (any type) skips ALL optimization.
        // 白名单：这些对象引用的全部贴图跳过所有优化。
        public List<Object> whitelist = new List<Object>();

        // Logging with [ATO] prefix. / [ATO] 前缀日志级别。
        public AtoLogLevel logLevel = AtoLogLevel.Info;

        // "" = Auto (follow NDMF language); otherwise a language code matching a Localization/*.json.
        // 空=Auto 跟随 NDMF；否则为 Localization/*.json 对应的语言码。
        public string languageOverride = "";

        // ---------- per-platform overrides / 平台覆写 ----------
        public AtoPlatformSettings pcSettings = new AtoPlatformSettings();
        public AtoPlatformSettings androidSettings = new AtoPlatformSettings();
        public AtoPlatformSettings iosSettings = new AtoPlatformSettings();

        public AtoPlatformSettings GetPlatformSettings(AtoPlatform p)
        {
            switch (p)
            {
                case AtoPlatform.Android: return androidSettings;
                case AtoPlatform.iOS: return iosSettings;
                default: return pcSettings;
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor-time validation. Returns error list (empty = ok).
        /// 编辑期校验，返回错误列表（空=合法）。
        /// Rules: exactly one component under the avatar; must sit on the VRCAvatarDescriptor object.
        /// 规则：整Avatar仅一个；必须挂在 VRCAvatarDescriptor 对象上。
        /// </summary>
        public static List<string> ValidatePlacement(Transform avatarRoot)
        {
            var errors = new List<string>();
            if (avatarRoot == null)
            {
                errors.Add("avatar root is null");
                return errors;
            }

            var found = new List<AvatarTextureOptimizer>();
            GetComponentsInChildren(avatarRoot, true, found);
            if (found.Count == 0) errors.Add("no AvatarTextureOptimizer component");
            if (found.Count > 1)
                errors.Add($"expected 1 AvatarTextureOptimizer under the avatar, found {found.Count}");
            foreach (var c in found)
            {
                if (c == null) continue;
                if (c.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() == null)
                    errors.Add(
                        $"AvatarTextureOptimizer on '{c.gameObject.name}' has no VRCAvatarDescriptor on the same object");
            }

            return errors;
        }

        private static void GetComponentsInChildren<T>(Transform root, bool includeInactive, List<T> sink)
            where T : Component
        {
            sink.AddRange(root.GetComponentsInChildren<T>(includeInactive));
        }
#endif
    }
}
