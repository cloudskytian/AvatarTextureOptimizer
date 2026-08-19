// AAO (Avatar Optimizer) compatibility via reflection - works when AAO is absent.
// 通过反射兼容 AAO（未安装时自动跳过）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Bridges to AAO's UVUsageCompabilityAPI (name as in AAO source, >=1.8.0):
    /// IsTexCoordUsed(renderer, channel) / RegisterTexCoordEvacuation(renderer, orig, saved).
    /// When AAO uses a UV channel we rewrote, the original coordinates are copied to a free
    /// channel and registered so AAO reads the originals.
    /// 桥接 AAO 的 UVUsageCompabilityAPI：若 AAO 会用到被重写的UV通道，把原始UV疏散到空闲
    /// 通道并注册，保证 AAO 读到原始坐标。
    /// </summary>
    public static class AaoCompat
    {
        private static Type _apiType;
        private static MethodInfo _isUsed, _register;
        private static bool _searched;

        public static bool Available
        {
            get
            {
                if (_searched) return _apiType != null && _isUsed != null && _register != null;
                _searched = true;
                _apiType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI"))
                    .FirstOrDefault(t => t != null);
                if (_apiType != null)
                {
                    _isUsed = _apiType.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                    _register = _apiType.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                    AtoLog.Info("AAO UVUsageCompabilityAPI detected; UV evacuation enabled");
                }
                else AtoLog.Debugf("AAO not installed; UV compat bridge disabled");
                return _apiType != null && _isUsed != null && _register != null;
            }
        }

        /// <summary>Evacuate original UVs of rewritten channels into free channels. / 疏散原始UV。</summary>
        public static void EvacuateUvChannels(SkinnedMeshRenderer smr, Mesh originalMesh, Mesh cloneMesh,
            List<int> rewrittenChannels)
        {
            if (!Available) return;
            try
            {
                // find free channels on the clone / 找空闲通道
                var used = new HashSet<int>(rewrittenChannels);
                for (int c = 0; c < 8; c++)
                {
                    var tmp = new List<Vector2>();
                    cloneMesh.GetUVs(c, tmp);
                    if (tmp.Count > 0) used.Add(c);
                }

                foreach (var ch in rewrittenChannels)
                {
                    bool aaoUses = (bool)_isUsed.Invoke(null, new object[] { smr, ch });
                    if (!aaoUses) continue;
                    int free = -1;
                    for (int c = 7; c >= 1; c--)
                        if (!used.Contains(c)) { free = c; break; }
                    if (free < 0)
                    {
                        AtoLog.Warn($"no free UV channel to evacuate uv{ch} for '{smr.name}'; AAO may misbehave");
                        continue;
                    }
                    var orig = new List<Vector2>();
                    originalMesh.GetUVs(ch, orig);
                    cloneMesh.SetUVs(free, orig);
                    used.Add(free);
                    _register.Invoke(null, new object[] { smr, ch, free });
                    AtoLog.Debugf($"evacuated uv{ch} -> uv{free} on '{smr.name}' for AAO");
                }
            }
            catch (Exception e)
            {
                AtoLog.Warn($"AAO UV evacuation failed on '{smr.name}': {e.Message}");
            }
        }
    }
}
