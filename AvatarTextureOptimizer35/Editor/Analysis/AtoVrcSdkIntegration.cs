using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Isolated reflection-based integration with the VRChat SDK (com.vrchat.avatars). /
    /// 与 VRChat SDK（com.vrchat.avatars）的反射隔离集成。
    ///
    /// The SDK is an OPTIONAL dependency: we must compile and work without it. All access goes
    /// through reflection against the VRCSDK3A assembly. Type/member layout verified against
    /// VRCSDK3A.dll metadata (3.10.4): VRCAvatarDescriptor.baseAnimationLayers /
    /// specialAnimationLayers are arrays of nested public type CustomAnimLayer
    /// { type: AnimLayerType, animatorController, mask, isDefault, eyeMovement }. /
    /// SDK 是可选依赖：必须能在未安装时编译与运行。所有访问经反射走 VRCSDK3A 程序集。
    /// 类型/成员布局已对照 VRCSDK3A.dll（3.10.4）元数据核实。
    /// </summary>
    internal static class AtoVrcSdkIntegration
    {
        private const string AssemblyName = "VRCSDK3A";
        private const string DescriptorTypeName = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";

        private static Type _descriptorType;
        private static bool _resolved;

        /// <summary>The VRCAvatarDescriptor type, or null if the SDK is not installed. / VRCAvatarDescriptor 类型；未装 SDK 时为 null。</summary>
        public static Type DescriptorType
        {
            get
            {
                if (!_resolved)
                {
                    _resolved = true;
                    try
                    {
                        _descriptorType = AppDomain.CurrentDomain.GetAssemblies()
                            .Where(a => a.GetName().Name == AssemblyName)
                            .Select(a => a.GetType(DescriptorTypeName))
                            .FirstOrDefault(t => t != null);
                    }
                    catch (Exception)
                    {
                        _descriptorType = null;
                    }
                }
                return _descriptorType;
            }
        }

        /// <summary>Whether the VRChat Avatars SDK is installed. / VRChat Avatars SDK 是否已安装。</summary>
        public static bool IsSdkInstalled => DescriptorType != null;

        /// <summary>
        /// Whether the GameObject has a VRCAvatarDescriptor component. / GameObject 是否有 VRCAvatarDescriptor 组件。
        /// </summary>
        public static bool HasVrcAvatarDescriptor(GameObject go)
        {
            var type = DescriptorType;
            if (type == null || go == null) return false;
            foreach (var component in go.GetComponents<Component>())
            {
                if (component != null && type.IsInstanceOfType(component)) return true;
            }
            return false;
        }

        /// <summary>One animator layer entry from the descriptor. / 描述符中的一个动画层条目。</summary>
        public readonly struct AvatarLayerEntry
        {
            public readonly RuntimeAnimatorController Controller;
            /// <summary>AnimLayerType name (e.g. "FX", "Gesture"). / AnimLayerType 名称（如 "FX"、"Gesture"）。</summary>
            public readonly string LayerTypeName;

            public AvatarLayerEntry(RuntimeAnimatorController controller, string layerTypeName)
            {
                Controller = controller;
                LayerTypeName = layerTypeName;
            }
        }

        /// <summary>
        /// Enumerate all animator controllers on the avatar descriptor (base + special layers,
        /// skipping default layers). / 枚举描述符上的全部 AnimatorController（基础层+特殊层，跳过默认层）。
        /// </summary>
        public static IEnumerable<AvatarLayerEntry> GetAvatarAnimatorControllers(Transform avatarRoot)
        {
            var type = DescriptorType;
            if (type == null || avatarRoot == null) yield break;

            Component descriptor = null;
            var found = avatarRoot.GetComponentsInChildren(type, true);
            if (found != null && found.Length > 0) descriptor = found[0];
            if (descriptor == null) yield break;

            foreach (var fieldName in new[] { "baseAnimationLayers", "specialAnimationLayers" })
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                if (field == null) continue;
                if (!(field.GetValue(descriptor) is Array layers)) continue;

                foreach (var layer in layers)
                {
                    if (layer == null) continue;
                    var layerType = layer.GetType();
                    var controllerField = layerType.GetField("animatorController", BindingFlags.Public | BindingFlags.Instance);
                    var isDefaultField = layerType.GetField("isDefault", BindingFlags.Public | BindingFlags.Instance);
                    var typeField = layerType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
                    if (controllerField == null) continue;

                    var controller = controllerField.GetValue(layer) as RuntimeAnimatorController;
                    if (controller == null) continue;
                    if (isDefaultField != null && isDefaultField.GetValue(layer) is bool isDefault && isDefault) continue;

                    var layerTypeName = typeField?.GetValue(layer)?.ToString() ?? "Unknown";
                    yield return new AvatarLayerEntry(controller, layerTypeName);
                }
            }
        }
    }
}
