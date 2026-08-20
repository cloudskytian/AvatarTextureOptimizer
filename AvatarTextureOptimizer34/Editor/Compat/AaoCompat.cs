// AvatarTextureOptimizer - AaoCompat
// EN: Reflection bridge to AAO's UVUsageCompabilityAPI (works with or without AAO installed). Before we overwrite
// a UV channel that AAO relies on, we save the ORIGINAL UVs into a free channel and register the evacuation.
// CN: 到 AAO UVUsageCompabilityAPI 的反射桥（AAO 装或不装都能工作）。在我们覆写 AAO 依赖的 UV 通道前，
//     把原始 UV 保存到空闲通道并登记疏散。
using System;
using System.Reflection;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// EN: AAO compatibility layer. Verified against AAO 1.9.17 source: the API class is
    /// Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI in assembly com.anatawa12.avatar-optimizer.api.editor
    /// (autoReferenced=false, so we access it via reflection).
    /// CN: AAO 兼容层。已对照 AAO 1.9.17 源码核实：API 类为
    ///     Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI，程序集 com.anatawa12.avatar-optimizer.api.editor
    ///     （autoReferenced=false，故经反射访问）。
    /// </summary>
    public static class AaoCompat
    {
        private static Type _apiType;
        private static MethodInfo _isUsed;
        private static MethodInfo _register;

        public static bool AaoPresent => Resolve() != null;

        private static Type Resolve()
        {
            if (_apiType != null) return _apiType;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.Contains("avatar-optimizer")) continue;
                    _apiType = asm.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
                    if (_apiType != null) break;
                }
                if (_apiType != null)
                {
                    _isUsed = _apiType.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                    _register = _apiType.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                }
            }
            catch (Exception e)
            {
                AtoLog.Detail($"AAO API resolve failed: {e.Message}");
            }
            return _apiType;
        }

        /// <summary>EN: True when AAO will use this UV channel on this renderer. / CN: AAO 将使用该渲染器此 UV 通道时为真。</summary>
        public static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            if (!AaoPresent || _isUsed == null) return false;
            try { return (bool)_isUsed.Invoke(null, new object[] { renderer, channel }); }
            catch (Exception) { return false; }
        }

        /// <summary>EN: Registers that we saved originalChannel's UVs into savedChannel. / CN: 登记我们已把 originalChannel 的 UV 保存到 savedChannel。</summary>
        public static bool RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
        {
            if (!AaoPresent || _register == null) return false;
            try { _register.Invoke(null, new object[] { renderer, originalChannel, savedChannel }); return true; }
            catch (Exception e)
            {
                AtoLog.Warn($"AAO evacuation registration failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// EN: For each renderer whose channel we modified: if AAO uses that channel, copy the ORIGINAL UVs into a
        /// free channel and register the evacuation (AAO restores & removes the saved channel afterwards).
        /// CN: 对每个通道被我们修改的渲染器：若 AAO 使用该通道，把原始 UV 复制到空闲通道并登记疏散
        ///     （AAO 之后会恢复并移除保存通道）。
        /// </summary>
        public static void EvacuateModifiedChannels(AtoBuildState state)
        {
            if (!AaoPresent) return;

            var processed = new System.Collections.Generic.HashSet<SkinnedMeshRenderer>();
            foreach (var g in state.UvGroups)
            {
                if (g.whitelisted || g.layout == null) continue;
                foreach (var r in g.renderers)
                {
                    var smr = r as SkinnedMeshRenderer;
                    if (smr == null || smr.sharedMesh == null) continue;
                    if (!processed.Add(smr)) continue;
                    if (!AaoCompat.IsTexCoordUsed(smr, g.channel)) continue;

                    var mesh = smr.sharedMesh;
                    int saved = FindFreeChannel(smr, mesh, state);
                    if (saved < 0)
                    {
                        AtoLog.Warn(string.Format(I18n.T("warn.aao.noslot"), smr.name));
                        continue;
                    }
                    // EN: Copy original UVs (captured before remap) into the saved channel.
                    // CN: 把原始 UV（重映射前捕获）复制到保存通道。
                    var original = new System.Collections.Generic.List<Vector2>(mesh.vertexCount);
                    var data = FindMeshUvData(state, g.mesh, g.channel);
                    if (data != null && data.uvs.Length == mesh.vertexCount)
                    {
                        original.AddRange(data.uvs);
                        mesh.SetUVs(saved, original);
                        if (AaoCompat.RegisterTexCoordEvacuation(smr, g.channel, saved))
                        {
                            AtoLog.Detail($"AAO evacuation: {smr.name} ch{g.channel} -> ch{saved}");
                        }
                    }
                }
            }
        }
private static int FindFreeChannel(SkinnedMeshRenderer smr, Mesh mesh, AtoBuildState state)
        {
            for (int ch = 0; ch < 8; ch++)
            {
                if (AaoCompat.IsTexCoordUsed(smr, ch)) continue;
                bool modified = false;
                foreach (var g in state.UvGroups)
                    if (g.renderer == smr && g.channel == ch && !g.whitelisted) { modified = true; break; }
                if (modified) continue;
                return ch;
            }
            return -1;
        }

        private static MeshUvData FindMeshUvData(AtoBuildState state, Mesh mesh, int channel)
        {
            foreach (var d in state.MeshUvData)
                if (d.mesh == mesh && d.channel == channel) return d;
            return null;
        }
    }
}
