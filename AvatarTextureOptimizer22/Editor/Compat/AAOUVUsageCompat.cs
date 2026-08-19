// AvatarTextureOptimizer
// File: Editor/Compat/AAOUVUsageCompat.cs
//
// Compatibility with Avatar Optimizer's UVUsageCompabilityAPI, called via
// REFLECTION so this tool has no compile-time dependency on AAO (works whether
// AAO is installed or not). The API was read in full from AAO 1.9.17 source
// (API-Editor/UVUsageCompabilityAPI.cs): a static `Impl` field is injected at
// build time; IsTexCoordUsed(renderer, channel) reports whether AAO depends on
// a UV channel; RegisterTexCoordEvacuation(renderer, original, saved) makes AAO
// use the saved copy and clean it up afterwards.
//
// Protocol (verified against source):
//   1. Before rewriting a channel, ask IsTexCoordUsed(renderer, channel).
//   2. If used, copy the ORIGINAL UVs to a spare channel (must not itself be
//      used by AAO), then rewrite the original channel.
//   3. Call RegisterTexCoordEvacuation(renderer, original, saved).
//
// 与 Avatar Optimizer 的 UVUsageCompabilityAPI 兼容，通过【反射】调用，使本
// 工具对 AAO 无编译期依赖（无论是否安装 AAO 都能工作）。已完整通读 AAO
// 1.9.17 源码（API-Editor/UVUsageCompabilityAPI.cs）：静态 `Impl` 字段在构建
// 期注入；IsTexCoordUsed(renderer, channel) 报告 AAO 是否依赖某 UV 通道；
// RegisterTexCoordEvacuation(renderer, original, saved) 使 AAO 使用保存的
// 副本并在处理后清理它。
//
// 协议（已对照源码验证）：
//   1. 重写某个通道前，询问 IsTexCoordUsed(renderer, channel)。
//   2. 若依赖，将【原始】UV 拷贝到备用通道（备用通道本身不能被 AAO 使用），
//      再重写原通道。
//   3. 调用 RegisterTexCoordEvacuation(renderer, original, saved)。

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.compat
{
    /// <summary>
    /// Per-renderer UV evacuation plan. Populated by the Applier before it
    /// rewrites UVs; committed to AAO right before the rewrite.
    /// 每个渲染器的 UV 疏散计划。由 Applier 在重写 UV 前填充；在重写前
    /// 立即提交给 AAO。
    /// </summary>
    public sealed class AAOUVEvacuationPlan
    {
        public readonly Renderer Renderer;
        public readonly List<(int OriginalChannel, int SavedChannel)> Evacuations =
            new List<(int OriginalChannel, int SavedChannel)>();

        public AAOUVEvacuationPlan(Renderer renderer) { Renderer = renderer; }
    }

    /// <summary>
    /// Reflection-based wrapper over AAO's UVUsageCompabilityAPI. All methods
    /// are safe no-ops when AAO is absent or no build is running.
    /// 基于反射的 AAO UVUsageCompabilityAPI 包装。AAO 缺席或未在构建时，所有
    /// 方法都是安全的空操作。
    /// </summary>
    public static class AAOUVUsage
    {
        private static Type _apiType;
        private static FieldInfo _implField;
        private static bool _probed;

        private static object GetImpl()
        {
            if (!_probed)
            {
                _probed = true;
                try
                {
                    _apiType = Type.GetType(
                        "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, Anatawa12.AvatarOptimizer.API",
                        false);
                    if (_apiType != null)
                        _implField = _apiType.GetField("Impl", BindingFlags.Static | BindingFlags.NonPublic);
                }
                catch
                {
                    _apiType = null;
                }
            }
            if (_apiType == null || _implField == null) return null;
            try { return _implField.GetValue(null); }
            catch { return null; }
        }

        private static object Invoke(object impl, string method, params object[] args)
        {
            try
            {
                var m = impl.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
                return m?.Invoke(impl, args);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Whether AAO uses the given UV channel on the renderer.
        /// AAO 是否在该渲染器上使用给定 UV 通道。
        /// </summary>
        public static bool IsTexCoordUsed(Renderer renderer, int channel)
        {
            if (renderer is not SkinnedMeshRenderer smr || channel is < 0 or >= 8) return false;
            var impl = GetImpl();
            if (impl == null) return false;
            var result = Invoke(impl, "IsTexCoordUsed", smr, channel);
            return result is bool b && b;
        }

        /// <summary>
        /// Find a spare channel (0..7) that neither carries data nor is used by
        /// AAO; returns -1 when none exists.
        /// 查找一个既无数据也未被 AAO 使用的备用通道（0..7）；不存在返回 -1。
        /// </summary>
        public static int FindSpareChannel(Mesh mesh, Renderer renderer, int avoidChannel)
        {
            for (int c = 7; c >= 0; c--)
            {
                if (c == avoidChannel) continue;
                if (MeshHasChannel(mesh, c)) continue;
                if (IsTexCoordUsed(renderer, c)) continue;
                return c;
            }
            return -1;
        }

        private static bool MeshHasChannel(Mesh mesh, int channel)
        {
            var list = new List<Vector2>();
            try { mesh.GetUVs(channel, list); } catch { return true; }
            return list.Count > 0;
        }

        /// <summary>
        /// Commit an evacuation to AAO. Must be called during the build (AAO's
        /// API impl is injected at build time).
        /// 将疏散提交给 AAO。必须在构建期间调用（AAO 的 API 实现是构建期
        /// 注入的）。
        /// </summary>
        public static void RegisterEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
        {
            var impl = GetImpl();
            if (impl == null) return;
            Invoke(impl, "RegisterTexCoordEvacuation", renderer, originalChannel, savedChannel);
            Debug.Log($"[ATO] Registered AAO UV evacuation: uv{originalChannel} -> uv{savedChannel} on {renderer.name}. / 已注册 AAO UV 疏散。");
        }
    }
}
