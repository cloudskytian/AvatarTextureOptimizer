// SPDX-License-Identifier: MIT
// EN: Optional integration with anatawa12's Avatar Optimizer. Everything goes through reflection so the
//     package compiles and runs whether or not AAO is installed.
// ZH: 与 anatawa12 的 Avatar Optimizer 的可选集成。全部通过反射实现，
//     因此无论是否安装 AAO，本包都能编译并运行。

using System;
using System.Linq;
using System.Reflection;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Interop
{
    /// <summary>
    /// EN: Thin wrapper over <c>Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI</c>
    ///     (spelling as in the AAO source). It lets ATO tell AAO where the original UVs were saved so
    ///     that features like Remove Mesh By Mask keep working after atlasing.
    /// ZH: 对 <c>Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI</c> 的轻量封装
    ///     （拼写与 AAO 源码一致）。它让 ATO 能告知 AAO 原始 UV 被保存到了哪里，
    ///     从而使 Remove Mesh By Mask 等功能在图集化之后仍然有效。
    /// </summary>
    public static class AaoInterop
    {
        private const string Stage = "AAO";
        private static bool _resolved;
        private static MethodInfo _isTexCoordUsed;
        private static MethodInfo _registerEvacuation;

        /// <summary>EN: True when AAO 1.8.0 or newer is present. ZH: 存在 AAO 1.8.0 或更新版本时为 true。</summary>
        public static bool Available
        {
            get
            {
                Resolve();
                return _isTexCoordUsed != null && _registerEvacuation != null;
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI", false))
                    .FirstOrDefault(t => t != null);
                if (type == null)
                {
                    AtoLog.Info(Stage, "Avatar Optimizer is not installed; UV evacuation is not needed.");
                    return;
                }

                _isTexCoordUsed = type.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                _registerEvacuation = type.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                AtoLog.Info(Stage, "Avatar Optimizer UV compatibility API detected.");
            }
            catch (Exception e)
            {
                AtoLog.Warning(Stage, $"failed to bind to the Avatar Optimizer API: {e.Message}");
            }
        }

        /// <summary>
        /// EN: Returns true when AAO reads the given UV channel of the renderer. Returns false when AAO is
        ///     not installed, which is the correct answer in that case.
        /// ZH: 当 AAO 会读取该渲染器的指定 UV 通道时返回 true。未安装 AAO 时返回 false，
        ///     在那种情况下这也是正确答案。
        /// </summary>
        public static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            if (!Available) return false;
            try
            {
                return (bool)_isTexCoordUsed.Invoke(null, new object[] { renderer, channel });
            }
            catch (Exception e)
            {
                AtoLog.Warning(Stage, $"IsTexCoordUsed failed: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        /// <summary>
        /// EN: Registers that the original contents of <paramref name="originalChannel"/> were copied to
        ///     <paramref name="savedChannel"/>. Returns false when AAO refused, in which case the caller
        ///     must fall back to leaving the mesh alone.
        /// ZH: 登记 <paramref name="originalChannel"/> 的原始内容已被复制到 <paramref name="savedChannel"/>。
        ///     AAO 拒绝时返回 false，此时调用方必须回退为不改动该网格。
        /// </summary>
        public static bool RegisterEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
        {
            if (!Available) return true;
            try
            {
                _registerEvacuation.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
                AtoLog.Debug_(Stage, $"registered UV evacuation on '{renderer.name}': uv{originalChannel} -> uv{savedChannel}");
                return true;
            }
            catch (Exception e)
            {
                AtoLog.Warning(Stage, $"RegisterTexCoordEvacuation failed on '{renderer.name}': {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        /// <summary>
        /// EN: Finds a UV channel that neither the mesh nor AAO is using, or -1 when the mesh is full.
        ///     Note that AAO's API only accepts <see cref="SkinnedMeshRenderer"/>; plain MeshRenderers
        ///     cannot be evacuated, but AAO's UV consuming components only attach to skinned renderers,
        ///     so nothing is lost.
        /// ZH: 找出网格与 AAO 都未使用的 UV 通道，网格已满时返回 -1。
        ///     注意 AAO 的 API 只接受 <see cref="SkinnedMeshRenderer"/>；普通 MeshRenderer 无法 evacuate，
        ///     但 AAO 消费 UV 的组件只会挂在蒙皮渲染器上，因此不会有损失。
        /// </summary>
        public static int FindFreeChannel(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            for (int c = 7; c >= 1; c--)
            {
                if (mesh.HasVertexAttribute((UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + c)))
                    continue;
                if (IsTexCoordUsed(renderer, c)) continue;
                return c;
            }
            return -1;
        }
    }
}
