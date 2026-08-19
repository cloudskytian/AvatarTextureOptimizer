// English: Optional AAO UVUsageCompabilityAPI via reflection (no hard assembly reference).
// 中文：通过反射可选调用 AAO 的 UVUsageCompabilityAPI（不硬引用程序集）。拼写按 AAO 原文。
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOAaoCompat
    {
        private static bool _resolved;
        private static MethodInfo _isUsed;
        private static MethodInfo _evacuate;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            var type = FindType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
            if (type == null)
            {
                return;
            }

            _isUsed = type.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
            _evacuate = type.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
        }

        public static bool Available
        {
            get
            {
                Resolve();
                return _isUsed != null && _evacuate != null;
            }
        }

        public static void EvacuateIfNeeded(ATOState state, SkinnedMeshRenderer smr, Mesh destMesh, int channel,
            List<Vector2> originalUvs)
        {
            if (smr == null || destMesh == null || !Available) return;
            try
            {
                var used = (bool)_isUsed.Invoke(null, new object[] { smr, channel });
                if (!used) return;
                var dest = FindFreeChannel(smr, destMesh);
                if (dest < 0)
                {
                    state.Log.Warn("AAO uses UV" + channel + " on " + smr.name + " but no free channel to evacuate");
                    return;
                }

                destMesh.SetUVs(dest, originalUvs);
                _evacuate.Invoke(null, new object[] { smr, channel, dest });
                state.Log.Info("evacuated UV" + channel + " -> UV" + dest + " on " + smr.name + " for AAO");
            }
            catch (Exception e)
            {
                state.Log.Warn("AAO UVUsageCompabilityAPI failed: " + e.Message);
            }
        }

        private static int FindFreeChannel(SkinnedMeshRenderer smr, Mesh destMesh)
        {
            for (var ch = 7; ch >= 0; ch--)
            {
                var usedByAao = false;
                try { usedByAao = (bool)_isUsed.Invoke(null, new object[] { smr, ch }); }
                catch { }

                if (usedByAao) continue;
                var existing = new List<Vector2>();
                destMesh.GetUVs(ch, existing);
                if (existing != null && existing.Count > 0) continue;
                return ch;
            }

            return -1;
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName, false); }
                catch { }

                if (t != null) return t;
            }

            return null;
        }
    }
}
