// Copyright (c) fosa. Licensed under the MIT License.
// Compatibility bridge to Avatar Optimizer's UV usage API. AAO may read UV coordinates to decide
// which triangles to remove; if we repack UVs without telling it, its mesh removal breaks.
// Everything here is reflection-based so the tool works with or without AAO installed.
// 与 Avatar Optimizer 的 UV 使用 API 的兼容桥接。
// AAO 可能读取 UV 坐标来决定移除哪些三角形；若我们重排 UV 而不告知它，其网格移除就会出错。
// 此处全部通过反射实现，使工具在安装或未安装 AAO 时都能正常工作。

using System;
using System.Reflection;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Talks to Avatar Optimizer without a hard assembly reference.
    /// 在不建立硬程序集引用的前提下与 Avatar Optimizer 交互。
    /// </summary>
    public sealed class AAOCompat
    {
        private const string ApiTypeName =
            "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor";

        private readonly ATOLogger _log;
        private readonly MethodInfo _isTexCoordUsed;
        private readonly MethodInfo _registerEvacuation;

        /// <summary>True when Avatar Optimizer is present and its API resolved. / AAO 存在且其 API 解析成功时为 true。</summary>
        public bool IsAvailable { get; }

        /// <summary>Creates the bridge, resolving the API once. / 创建桥接并一次性解析 API。</summary>
        public AAOCompat(ATOLogger log)
        {
            _log = log;

            try
            {
                var type = Type.GetType(ApiTypeName, false) ?? FindApiType();
                if (type == null)
                {
                    _log?.Detail("Avatar Optimizer not detected; UV evacuation is not required");
                    return;
                }

                _isTexCoordUsed = type.GetMethod(
                    "IsTexCoordUsed",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(SkinnedMeshRenderer), typeof(int) },
                    null);

                _registerEvacuation = type.GetMethod(
                    "RegisterTexCoordEvacuation",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(SkinnedMeshRenderer), typeof(int), typeof(int) },
                    null);

                IsAvailable = _isTexCoordUsed != null && _registerEvacuation != null;

                if (IsAvailable)
                {
                    _log?.Info("Avatar Optimizer detected; UV usage compatibility enabled");
                }
                else
                {
                    _log?.Warning(
                        "Avatar Optimizer was found but its UV API could not be resolved. " +
                        "UV evacuation will be skipped.");
                }
            }
            catch (Exception e)
            {
                _log?.Warning($"Avatar Optimizer compatibility check failed: {e.Message}");
            }
        }

        private static Type FindApiType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (name == null || name.IndexOf("avatar-optimizer", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var t = asm.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI", false);
                if (t != null) return t;
            }

            return null;
        }

        /// <summary>
        /// Returns true when Avatar Optimizer may read this UV channel.
        /// Conservative: on any doubt it reports "used" so we never silently break AAO.
        /// 当 Avatar Optimizer 可能读取该 UV 通道时返回 true。
        /// 保守策略：任何存疑情况都报告为「已使用」，从而绝不静默破坏 AAO。
        /// </summary>
        public bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            if (!IsAvailable || renderer == null) return false;
            if (channel < 0 || channel > 7) return false;

            try
            {
                return (bool)_isTexCoordUsed.Invoke(null, new object[] { renderer, channel });
            }
            catch (Exception e)
            {
                _log?.Warning(
                    $"IsTexCoordUsed failed for {renderer.name} UV{channel}: {e.Message}. " +
                    "Assuming the channel is used.");
                return true;
            }
        }

        /// <summary>
        /// Tells Avatar Optimizer that the original UVs of <paramref name="originalChannel" />
        /// were copied to <paramref name="savedChannel" />.
        /// 告知 Avatar Optimizer：<paramref name="originalChannel" /> 的原始 UV
        /// 已被复制到 <paramref name="savedChannel" />。
        /// </summary>
        /// <returns>False when registration failed and the renderer must be excluded. / 注册失败、必须排除该渲染器时返回 false。</returns>
        public bool RegisterEvacuation(
            SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
        {
            if (!IsAvailable || renderer == null) return true;

            try
            {
                _registerEvacuation.Invoke(
                    null, new object[] { renderer, originalChannel, savedChannel });
                _log?.Detail(
                    $"{renderer.name}: evacuated UV{originalChannel} -> UV{savedChannel} for AAO");
                return true;
            }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException)
            {
                // AAO already claims the destination channel.
                // AAO 已占用目标通道。
                _log?.Warning(
                    $"{renderer.name}: UV{savedChannel} is already used by Avatar Optimizer; " +
                    "cannot evacuate UVs.");
                return false;
            }
            catch (Exception e)
            {
                _log?.Warning($"{renderer.name}: UV evacuation failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Finds a free UV channel to copy original coordinates into, or -1 when the mesh has
        /// none left.
        /// 寻找可用于复制原始坐标的空闲 UV 通道，若网格已无空闲通道则返回 -1。
        /// </summary>
        public static int FindFreeUVChannel(Mesh mesh, int excludeChannel)
        {
            if (mesh == null) return -1;

            var temp = new System.Collections.Generic.List<Vector4>();

            // Channels 0 and 1 are conventionally meaningful; search from the top down so we
            // disturb the least commonly used channels first.
            // 通道 0 与 1 通常具有约定含义；自高位向下搜索，
            // 从而优先占用最不常用的通道。
            for (var ch = 7; ch >= 2; ch--)
            {
                if (ch == excludeChannel) continue;

                temp.Clear();
                mesh.GetUVs(ch, temp);
                if (temp.Count == 0) return ch;
            }

            return -1;
        }
    }
}
