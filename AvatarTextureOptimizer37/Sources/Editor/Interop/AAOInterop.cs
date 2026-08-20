// ============================================================================
// ATO - Avatar Optimizer (AAO) interop
// ATO - Avatar Optimizer (AAO) 互操作
//
// ATO may rewrite UV channels that AAO itself depends on (RemoveMeshByMask /
// RemoveMeshByUVTile). AAO 1.8+ ships UVUsageCompabilityAPI (yes, the typo is
// AAO's own) for exactly this:
//   - IsTexCoordUsed(renderer, channel)
//   - RegisterTexCoordEvacuation(renderer, originalChannel, savedChannel)
// AAO is an OPTIONAL dependency, so all access goes through reflection with
// a graceful no-op fallback when AAO is not installed.
// 注意：该 API 只接受 SkinnedMeshRenderer；对 MeshRenderer 无法做通道撤离，
// 此时若 AAO 使用了我们要改写的通道，则把相关网格按白名单处理并告警。
// ATO 可能改写 AAO 自身依赖的 UV 通道（RemoveMeshByMask / RemoveMeshByUVTile）
// 。AAO 1.8+ 提供了 UVUsageCompabilityAPI（拼写错误是 AAO 原文）专为此用：
//   - IsTexCoordUsed(renderer, channel)
//   - RegisterTexCoordEvacuation(renderer, originalChannel, savedChannel)
// AAO 是可选依赖，因此全部通过反射访问；未安装 AAO 时优雅地 no-op。
// 注意：该 API 只接受 SkinnedMeshRenderer；对 MeshRenderer 无法做通道撤离，
// 此时若 AAO 使用了我们要改写的通道，则把相关网格按白名单处理并告警。
// ============================================================================

#region

using System;
using System.Reflection;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Interop
{
    public static class AAOInterop
    {
        private const string AaoApiTypeName =
            "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI";

        private static Type _apiType;
        private static MethodInfo _isUsed;
        private static MethodInfo _registerEvacuation;
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    _apiType = asm.GetType(AaoApiTypeName, false);
                }
                catch (Exception)
                {
                    // keep looking
                }

                if (_apiType != null) break;
            }

            if (_apiType == null) return;

            try
            {
                _isUsed = _apiType.GetMethod("IsTexCoordUsed",
                    new[] { typeof(SkinnedMeshRenderer), typeof(int) });
                _registerEvacuation = _apiType.GetMethod("RegisterTexCoordEvacuation",
                    new[] { typeof(SkinnedMeshRenderer), typeof(int), typeof(int) });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ATO] AAO UVUsageCompabilityAPI found but reflection failed: " + e.Message);
                _apiType = null;
            }
        }

        /// <summary>True when AAO is installed and its API is resolvable.
        /// AAO 已安装且 API 可解析。</summary>
        public static bool Available
        {
            get
            {
                Resolve();
                return _apiType != null && _isUsed != null;
            }
        }

        /// <summary>Asks AAO whether it will use this UV channel of the given
        /// SkinnedMeshRenderer. False when AAO is absent (nothing to
        /// coordinate). 询问 AAO 是否使用该 SkinnedMeshRenderer 的指定 UV 通道；
        /// AAO 不存在时返回 false。</summary>
        public static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            if (renderer == null || _isUsed == null || !Available) return false;
            try
            {
                return (bool) _isUsed.Invoke(null, new object[] { renderer, channel });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ATO] AAO IsTexCoordUsed failed: " + e.Message);
                return true; // fail safe: assume it is used 失败安全：视为已使用
            }
        }

        /// <summary>Tells AAO that the original channel was evacuated to a free
        /// saved channel. Returns true when AAO is absent (nothing to do);
        /// returns false when evacuation failed (caller must then whitelist
        /// the mesh).
        /// 告知 AAO 原通道已撤离到空闲通道；AAO 不存在时返回 true（无需处理）；
        /// 撤离失败时返回 false（调用方应将该网格白名单化）。</summary>
        public static bool TryRegisterEvacuation(SkinnedMeshRenderer renderer, int originalChannel,
            int savedChannel)
        {
            if (!Available) return true; // no AAO -> nothing to coordinate 无 AAO -> 无需协调
            if (renderer == null) return false;
            try
            {
                _registerEvacuation.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
                return true;
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException.Message
                    : e.Message;
                Debug.LogWarning("[ATO] AAO RegisterTexCoordEvacuation failed: " + inner);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // MeshRenderer support: the UVUsageCompabilityAPI only accepts
        // SkinnedMeshRenderer. For MeshRenderers we detect AAO's
        // RemoveMeshByMask / RemoveMeshByUVTile components directly via
        // reflection; when reflection cannot read the channels we assume ALL
        // channels are used (conservative).
        // MeshRenderer 支持：UVUsageCompabilityAPI 仅接受 SkinnedMeshRenderer。
        // 对 MeshRenderer 直接反射检测 AAO 的 RemoveMeshByMask /
        // RemoveMeshByUVTile 组件；反射无法读取通道时假定全部通道被使用（保守）。
        // ------------------------------------------------------------------
        private static Type _removeMeshByMaskType;
        private static Type _removeMeshByUvTileType;
        private static bool _mrResolved;

        private static void ResolveMrTypes()
        {
            if (_mrResolved) return;
            _mrResolved = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    _removeMeshByMaskType ??= asm.GetType(
                        "Anatawa12.AvatarOptimizer.RemoveMeshByMask", false);
                    _removeMeshByUvTileType ??= asm.GetType(
                        "Anatawa12.AvatarOptimizer.RemoveMeshByUVTile", false);
                }
                catch (Exception)
                {
                }
            }
        }

        private static readonly BindingFlags FieldFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>Collects AAO-used channels for a renderer via reflection
        /// (AAO components are internal). Returns null when no AAO components
        /// are present (or AAO not installed).
        /// 通过反射收集渲染器上 AAO 使用的 UV 通道（AAO 组件为 internal）。
        /// 无 AAO 组件（或未安装 AAO）时返回 null。</summary>
        public static int[] RendererAaoChannels(MeshRenderer renderer)
        {
            if (renderer == null) return null;
            ResolveMrTypes();
            if (_removeMeshByMaskType == null && _removeMeshByUvTileType == null) return null;

            var used = new System.Collections.Generic.List<int>();
            bool any = false;
            foreach (var comp in renderer.GetComponents<Component>())
            {
                if (comp == null) continue;
                var t = comp.GetType();
                if (_removeMeshByMaskType != null && t == _removeMeshByMaskType)
                {
                    any = true;
                    if (!used.Contains(0)) used.Add(0); // uses UV0  使用 UV0
                }
                else if (_removeMeshByUvTileType != null && t == _removeMeshByUvTileType)
                {
                    any = true;
                    try
                    {
                        var field = t.GetField("materials", FieldFlags);
                        if (field?.GetValue(comp) is System.Array arr)
                        {
                            foreach (var slot in arr)
                            {
                                if (slot == null) continue;
                                var st = slot.GetType();
                                var anyTile = st.GetProperty("RemoveAnyTile", FieldFlags);
                                if (anyTile != null && !(anyTile.GetValue(slot) is true)) continue;
                                var chField = st.GetField("uvChannel", FieldFlags);
                                var v = chField?.GetValue(slot);
                                int ch = -1;
                                if (v != null)
                                {
                                    if (v is int i) ch = i;
                                    else if (v.GetType().IsEnum) ch = Convert.ToInt32(v);
                                }
                                if (ch >= 0 && ch < 8)
                                {
                                    if (!used.Contains(ch)) used.Add(ch);
                                }
                                else
                                {
                                    // conservative: assume 0..3  保守：假定 0..3
                                    for (int c = 0; c < 4; c++)
                                    {
                                        if (!used.Contains(c)) used.Add(c);
                                    }
                                }
                            }
                        }
                        else
                        {
                            for (int c = 0; c < 4; c++)
                            {
                                if (!used.Contains(c)) used.Add(c);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            if (!used.Contains(c)) used.Add(c);
                        }
                    }
                }
            }
            return any ? used.ToArray() : null;
        }

        /// <summary>True when AAO components on the renderer use the channel.
        /// AAO 组件是否使用该通道。</summary>
        public static bool RendererUsesAaoChannel(MeshRenderer renderer, int channel)
        {
            var chs = RendererAaoChannels(renderer);
            return chs != null && System.Array.IndexOf(chs, channel) >= 0;
        }
    }
}
