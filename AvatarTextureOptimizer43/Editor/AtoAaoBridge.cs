using System;
using System.Reflection;
using UnityEngine;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Optional AAO UVUsageCompabilityAPI via reflection.
    /// AAO 1.9.17 API-Editor is autoReferenced=false; we must not hard-reference it so the
    /// package still compiles when AAO is absent. Read from UVUsageCompabilityAPI.cs:
    ///   IsTexCoordUsed(SkinnedMeshRenderer, int channel 0..7)
    ///   RegisterTexCoordEvacuation(SkinnedMeshRenderer, originalChannel, savedChannel)
    /// 用反射调用 AAO 的 UVUsageCompabilityAPI（原文即为此拼写）。未安装 AAO 时静默跳过。
    /// </summary>
    public static class AtoAaoBridge
    {
        static bool _resolved;
        static MethodInfo _isUsed;
        static MethodInfo _evacuate;

        static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "com.anatawa12.avatar-optimizer.api.editor") continue;
                t = asm.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
                break;
            }
            if (t == null) return;
            _isUsed = t.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
            _evacuate = t.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
            AtoLog.Detail("AAO UVUsageCompabilityAPI resolved: used=" + (_isUsed != null) + " evac=" + (_evacuate != null));
        }

        public static bool Available
        {
            get { Resolve(); return _isUsed != null && _evacuate != null; }
        }

        public static bool IsTexCoordUsed(SkinnedMeshRenderer r, int channel)
        {
            Resolve();
            if (_isUsed == null || r == null) return false;
            try { return (bool)_isUsed.Invoke(null, new object[] { r, channel }); }
            catch (Exception e)
            {
                AtoLog.Warn("AAO IsTexCoordUsed failed: " + e.Message);
                return false;
            }
        }

        public static bool TryEvacuate(SkinnedMeshRenderer r, int original, int saved)
        {
            Resolve();
            if (_evacuate == null || r == null) return false;
            try
            {
                _evacuate.Invoke(null, new object[] { r, original, saved });
                return true;
            }
            catch (Exception e)
            {
                AtoLog.Warn("AAO RegisterTexCoordEvacuation failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// If AAO will read this UV channel, copy original UVs to an unused channel and register evacuation
        /// BEFORE we rewrite the channel for atlasing.
        /// 若 AAO 会读取该 UV 通道，则在改写前把原 UV 拷到空闲通道并登记 evacuation。
        /// </summary>
        public static void EvacuateIfNeeded(SkinnedMeshRenderer smr, Mesh mesh, int channel, AtoReport report)
        {
            if (!Available || smr == null || mesh == null) return;
            if (!IsTexCoordUsed(smr, channel)) return;
            int saved = -1;
            for (int c = 7; c >= 0; c--)
            {
                if (c == channel) continue;
                if (IsTexCoordUsed(smr, c)) continue;
                saved = c; break;
            }
            if (saved < 0)
            {
                report.Warnings.Add("AAO needs UV" + channel + " on " + smr.name + " but no free channel to evacuate");
                return;
            }
            var uvs = new System.Collections.Generic.List<Vector2>();
            mesh.GetUVs(channel, uvs);
            if (uvs.Count == 0) return;
            mesh.SetUVs(saved, uvs);
            if (TryEvacuate(smr, channel, saved))
                AtoLog.Info("Evacuated UV" + channel + " -> UV" + saved + " on " + smr.name);
            else
                report.Warnings.Add("Failed to register AAO UV evacuation on " + smr.name);
        }
    }
}
