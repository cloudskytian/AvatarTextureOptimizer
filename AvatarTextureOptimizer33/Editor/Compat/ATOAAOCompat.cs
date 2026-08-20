// SPDX-License-Identifier: MIT
// EN: Optional integration with anatawa12's Avatar Optimizer (AAO). Accessed purely through reflection so
//     the package stays usable when AAO is not installed.
// ZH: 与 anatawa12 的 Avatar Optimizer (AAO) 的可选集成。完全通过反射访问，
//     因此未安装 AAO 时本包依然可用。

using System;
using System.Reflection;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: Thin reflection wrapper around <c>Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI</c>
    ///     (the API name is spelled like this upstream).
    /// ZH: 对 <c>Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI</c> 的轻量反射封装
    ///     （上游就是这个拼写）。
    /// </summary>
    public sealed class ATOAAOCompat
    {
        private const string TypeName = "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI";

        private readonly ATOLog _log;
        private readonly MethodInfo _isTexCoordUsed;
        private readonly MethodInfo _registerEvacuation;

        public ATOAAOCompat(ATOLog log)
        {
            _log = log;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try
                {
                    type = assembly.GetType(TypeName, false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (type == null) continue;

                _isTexCoordUsed = type.GetMethod("IsTexCoordUsed",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(SkinnedMeshRenderer), typeof(int) }, null);
                _registerEvacuation = type.GetMethod("RegisterTexCoordEvacuation",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(SkinnedMeshRenderer), typeof(int), typeof(int) }, null);
                break;
            }

            Available = _isTexCoordUsed != null && _registerEvacuation != null;
            _log.Info("aao", Available ? "Avatar Optimizer UV compatibility API found" : "Avatar Optimizer not installed");
        }

        /// <summary>EN: True when AAO exposes the UV compatibility API. ZH: AAO 提供 UV 兼容 API 时为 true。</summary>
        public bool Available { get; }

        /// <summary>
        /// EN: Returns true if AAO reads the given UV channel of the renderer.
        /// ZH: 若 AAO 会读取该渲染器的指定 UV 通道则返回 true。
        /// </summary>
        public bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            if (!Available || renderer == null) return false;
            try
            {
                return (bool)_isTexCoordUsed.Invoke(null, new object[] { renderer, channel });
            }
            catch (Exception e)
            {
                _log.Warning("aao", $"IsTexCoordUsed failed: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        /// <summary>
        /// EN: Tells AAO that the original UVs of <paramref name="originalChannel"/> were copied to
        ///     <paramref name="savedChannel"/>.
        /// ZH: 告知 AAO：<paramref name="originalChannel"/> 的原始 UV 已被复制到
        ///     <paramref name="savedChannel"/>。
        /// </summary>
        public bool RegisterEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
        {
            if (!Available || renderer == null) return false;
            try
            {
                _registerEvacuation.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
                _log.Info("aao", $"'{renderer.name}': UV{originalChannel} evacuated to UV{savedChannel}");
                return true;
            }
            catch (Exception e)
            {
                _log.Warning("aao", $"RegisterTexCoordEvacuation failed: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        /// <summary>
        /// EN: Finds a UV channel that is neither populated on the mesh nor used by AAO.
        /// ZH: 找到一个既未被网格占用、也不会被 AAO 使用的 UV 通道。
        /// </summary>
        public int FindFreeChannel(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            var list = new System.Collections.Generic.List<Vector2>();
            for (var c = 7; c >= 1; c--)
            {
                mesh.GetUVs(c, list);
                if (list.Count > 0) continue;
                if (IsTexCoordUsed(renderer, c)) continue;
                return c;
            }

            return -1;
        }
    }
}
