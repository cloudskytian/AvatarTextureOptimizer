using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Reflection wrapper around Avatar Optimizer's UVUsageCompabilityAPI (spelling as in AAO).
    ///
    ///     Reflection rather than a direct assembly reference is required: AAO's API assembly is marked
    ///     <c>autoReferenced: false</c> with <c>overrideReferences: true</c>, so naming it in our asmdef
    ///     would break compilation for every user who has not installed AAO.
    ///
    ///     Note the API surface only accepts SkinnedMeshRenderer - AAO's UV-consuming components
    ///     (Remove Mesh By Mask / By UV Tile) only exist on skinned meshes, so a MeshRenderer needs no
    ///     evacuation at all.
    ///
    /// ZH: 对 Avatar Optimizer 的 UVUsageCompabilityAPI（拼写同 AAO 原文）的反射封装。
    ///
    ///     必须用反射而非直接程序集引用：AAO 的 API 程序集标记了 <c>autoReferenced: false</c>
    ///     与 <c>overrideReferences: true</c>，在我们的 asmdef 中写上它会让所有未安装 AAO 的用户编译失败。
    ///
    ///     注意该 API 只接受 SkinnedMeshRenderer——AAO 中消费 UV 的组件
    ///     （Remove Mesh By Mask / By UV Tile）只存在于蒙皮网格上，因此 MeshRenderer 完全不需要疏散。
    /// </summary>
    public static class AAOCompat
    {
        private static bool _probed;
        private static MethodInfo _isTexCoordUsed;
        private static MethodInfo _registerEvacuation;

        /// <summary>EN: True when Avatar Optimizer is installed and its API responded. ZH: AAO 已安装且其 API 有响应时为 true。</summary>
        public static bool Available
        {
            get { Probe(); return _isTexCoordUsed != null && _registerEvacuation != null; }
        }

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI", false))
                    .FirstOrDefault(t => t != null);
                if (type == null) return;

                _isTexCoordUsed = type.GetMethod("IsTexCoordUsed",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(SkinnedMeshRenderer), typeof(int) }, null);
                _registerEvacuation = type.GetMethod("RegisterTexCoordEvacuation",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(SkinnedMeshRenderer), typeof(int), typeof(int) }, null);
            }
            catch { /* EN: absence is a normal state. ZH: 未安装是正常状态。 */ }
        }

        /// <summary>EN: Does AAO consume this UV channel? Returns false when AAO is absent. ZH: AAO 是否消费该 UV 通道？未安装时返回 false。</summary>
        public static bool IsTexCoordUsed(SkinnedMeshRenderer smr, int channel)
        {
            if (!Available || smr == null) return false;
            try { return (bool)_isTexCoordUsed.Invoke(null, new object[] { smr, channel }); }
            catch { return false; }
        }

        /// <summary>
        /// EN: Tell AAO that the original UVs of <paramref name="originalChannel"/> were copied into
        ///     <paramref name="savedChannel"/> before we rewrote them.
        /// ZH: 告知 AAO：在我们重写之前，<paramref name="originalChannel"/> 的原始 UV 已复制到
        ///     <paramref name="savedChannel"/>。
        /// </summary>
        public static bool RegisterEvacuation(SkinnedMeshRenderer smr, int originalChannel, int savedChannel, ATOLog log)
        {
            if (!Available || smr == null) return false;
            try
            {
                _registerEvacuation.Invoke(null, new object[] { smr, originalChannel, savedChannel });
                log.Verbose($"AAO UV evacuation registered on '{smr.name}': uv{originalChannel} -> uv{savedChannel}");
                return true;
            }
            catch (Exception e)
            {
                log.Warn($"AAO UV evacuation failed on '{smr.name}': {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }
    }
}
