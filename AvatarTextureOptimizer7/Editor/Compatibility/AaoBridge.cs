using System;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Soft dependency on AAO UVUsageCompabilityAPI (note AAO's original spelling).
    /// When AAO is absent this is a no-op. Only SkinnedMeshRenderer is supported by AAO.
    /// 对 AAO UVUsageCompabilityAPI 的软依赖（拼写与 AAO 原文一致）。
    /// 未安装 AAO 时为空操作。AAO 只支持 SkinnedMeshRenderer。
    /// </summary>
    public static class AaoBridge
    {
        static bool _resolved;
        static MethodInfo _isUsed;
        static MethodInfo _register;
        static bool _available;

        static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            var t = Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor")
                    ?? Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.editor")
                    ?? FindType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
            if (t == null)
            {
                _available = false;
                return;
            }

            _isUsed = t.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
            _register = t.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
            _available = _isUsed != null && _register != null;
        }

        static Type FindType(string full)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(full);
                if (t != null) return t;
            }

            return null;
        }

        public static bool Available
        {
            get { Resolve(); return _available; }
        }

        public static bool IsTexCoordUsed(SkinnedMeshRenderer smr, int channel)
        {
            Resolve();
            if (!_available || smr == null) return false;
            try { return (bool)_isUsed.Invoke(null, new object[] { smr, channel }); }
            catch { return false; }
        }

        public static void CopyOriginalUvForEvacuate(AtoSession session, SkinnedMeshRenderer smr, Mesh newMesh, int originalChannel)
        {
            Resolve();
            if (!_available || smr == null || newMesh == null) return;
            try
            {
                if (!IsTexCoordUsed(smr, originalChannel)) return;
                int saved = FindFreeChannel(smr, newMesh, originalChannel);
                if (saved < 0) return;
                var orig = new System.Collections.Generic.List<Vector2>();
                newMesh.GetUVs(originalChannel, orig);
                if (orig.Count == 0 && smr.sharedMesh != null)
                    smr.sharedMesh.GetUVs(originalChannel, orig);
                if (orig.Count == newMesh.vertexCount)
                    newMesh.SetUVs(saved, orig);
            }
            catch (Exception e)
            {
                session.Log.Warn("AAO copy UV failed: " + e.Message);
            }
        }

        public static void EvacuateIfNeeded(AtoSession session, SkinnedMeshRenderer smr, int originalChannel, Mesh newMesh)
        {
            Resolve();
            if (!_available || smr == null || newMesh == null) return;
            try
            {
                if (!IsTexCoordUsed(smr, originalChannel)) return;
                int saved = FindFreeChannel(smr, newMesh, originalChannel);
                if (saved < 0)
                {
                    session.Log.Warn("AAO wants UV" + originalChannel + " but no free channel to evacuate on " + smr.name);
                    return;
                }

                _register.Invoke(null, new object[] { smr, originalChannel, saved });
                session.Log.Info("AAO UV evacuate " + smr.name + " UV" + originalChannel + " -> UV" + saved);
            }
            catch (Exception e)
            {
                session.Log.Warn("AAO UVUsageCompabilityAPI failed: " + e.Message);
            }
        }

        static int FindFreeChannel(SkinnedMeshRenderer smr, Mesh mesh, int originalChannel)
        {
            // Prefer a channel AAO does not use. The cloned mesh still carries original UV1–7,
            // so a non-empty channel is fine — we overwrite it with the evacuated copy.
            // 优先 AAO 不用的通道。克隆网格仍带有原始 UV1–7，非空也可以，我们会覆盖成撤离副本。
            for (int c = 7; c >= 0; c--)
            {
                if (c == originalChannel) continue;
                if (IsTexCoordUsed(smr, c)) continue;
                return c;
            }

            return -1;
        }
    }
}
