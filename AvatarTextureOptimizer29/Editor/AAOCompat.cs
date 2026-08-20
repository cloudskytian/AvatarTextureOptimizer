// AAO compatibility via reflection: UVUsageCompabilityAPI (AAO >= 1.8, verified from
// source - see docs/ThirdPartyNotes.md). SMR only; MeshRenderer has no AAO UV usage.
// AAO 兼容（反射）：UVUsageCompabilityAPI（AAO>=1.8，已读源码）。仅 SMR；MeshRenderer 无此API。

using System;
using System.Reflection;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class AAOCompat
    {
        private static MethodInfo _isUsed, _register;
        private static readonly object[] Args2 = new object[3];
        private static readonly object[] Args1 = new object[2];
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            const string typeName = "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI";
            Type type = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType(typeName, false);
                if (type != null) break;
            }
            if (type == null)
            {
                ATOLog.DebugL("AAO UVUsageCompabilityAPI not present (user may not have AAO installed)");
                return;
            }

            _isUsed = type.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
            _register = type.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
        }

        internal static bool Available()
        {
            Resolve();
            return _isUsed != null && _register != null;
        }

        internal static bool IsTexCoordUsed(SkinnedMeshRenderer smr, int channel)
        {
            Resolve();
            if (_isUsed == null) return false;
            try
            {
                Args1[0] = smr;
                Args1[1] = channel;
                return (bool)_isUsed.Invoke(null, Args1);
            }
            catch
            {
                return false;
            }
        }

        internal static bool RegisterEvacuation(SkinnedMeshRenderer smr, int original, int saved)
        {
            Resolve();
            if (_register == null) return false;
            try
            {
                Args2[0] = smr;
                Args2[1] = original;
                Args2[2] = saved;
                _register.Invoke(null, Args2);
                return true;
            }
            catch (Exception e)
            {
                ATOLog.Warn($"AAO RegisterTexCoordEvacuation failed: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        /// <summary>After the mesh rewrite: save original UVs of modified channels into a
        /// free channel and register the evacuation (only needed when AAO uses that channel).
        /// 网格重写后：将被改通道的原始UV存入空闲通道并注册疏散（仅AAO实际使用该通道时需要）。
        /// </summary>
        internal static void EvacuateOriginalUVs(AtoSession s, RendererInfo ri)
        {
            if (!ri.skinned) return;
            var smr = (SkinnedMeshRenderer)ri.renderer;
            if (!Available()) return;
            if (!s.rewrittenChannels.TryGetValue(ri.renderer, out var channels) || channels.Count == 0) return;

            foreach (var ch in channels)
            {
                try
                {
                    if (!IsTexCoordUsed(smr, ch)) continue; // AAO doesn't care / AAO不用此通道

                    // find a free channel / 找空闲通道
                    int free = -1;
                    for (int c = 4; c < 8; c++) // prefer high channels / 优先高通道
                    {
                        if (channels.Contains(c)) continue;
                        var l = new System.Collections.Generic.List<Vector2>();
                        smr.sharedMesh.GetUVs(c, l);
                        if (l.Count != 0) continue;
                        if (IsTexCoordUsed(smr, c)) continue;
                        free = c;
                        break;
                    }

                    if (free < 0)
                    {
                        s.warnings.Add(string.Format(ATOL10n.Get("warn.aaoEvacuate"), ri.path,
                            "no free UV channel"));
                        continue;
                    }

                    var orig = new System.Collections.Generic.List<Vector2>();
                    ri.originalUvBackup.TryGetValue(ch, out var origData);
                    if (origData == null) continue;
                    smr.sharedMesh.SetUVs(free, new System.Collections.Generic.List<Vector2>(origData));
                    if (!RegisterEvacuation(smr, ch, free))
                        s.warnings.Add(string.Format(ATOL10n.Get("warn.aaoEvacuate"), ri.path, "register failed"));
                    else
                        ATOLog.DebugL($"AAO evacuation {ri.path}: uv{ch} -> uv{free}");
                }
                catch (Exception e)
                {
                    s.warnings.Add(string.Format(ATOL10n.Get("warn.aaoEvacuate"), ri.path, e.Message));
                }
            }
        }
    }
}
