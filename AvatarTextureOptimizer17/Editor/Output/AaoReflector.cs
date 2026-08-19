// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Output/AaoReflector.cs — AAO UVUsageCompabilityAPI 反射封装 /
// Reflection wrapper for AAO's UVUsageCompabilityAPI
//
// 需求: 兼容 AAO 的 UVUsageCompabilityAPI（非拼写错误），需要考虑用户未安装 AAO 的情况。
// 实现: 反射调用（Unity asmdef 不支持可选引用；AAO 未安装时 Api == null，调用方跳过）。
// ============================================================================
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// AAO API 接口 / AAO API surface.
    /// </summary>
    public interface IAaoApi
    {
        bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel);
        void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel);
    }

    /// <summary>
    /// AAO UVUsageCompabilityAPI 反射封装 / Reflection wrapper.
    /// </summary>
    public static class AaoReflector
    {
        private const string ApiTypeName = "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI";
        private const string ApiAssembly = "com.anatawa12.avatar-optimizer.api.editor";

        /// <summary>可用时为非 null / non-null when AAO is installed</summary>
        public static IAaoApi Api { get; private set; }

        [InitializeOnLoadMethod]
        private static void Init()
        {
            try
            {
                var type = System.Type.GetType($"{ApiTypeName}, {ApiAssembly}") ??
                           System.AppDomain.CurrentDomain.GetAssemblies()
                               .Select(a => a.GetType(ApiTypeName))
                               .FirstOrDefault(t => t != null);
                if (type == null) return;

                var isUsed = type.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                var register = type.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                if (isUsed == null || register == null) return;

                Api = new ReflectedAaoApi(isUsed, register);
                Log.VerboseLog("AAO UVUsageCompabilityAPI integration ready.");
            }
            catch (System.Exception)
            {
                Api = null;
            }
        }

        private sealed class ReflectedAaoApi : IAaoApi
        {
            private readonly MethodInfo _isUsed;
            private readonly MethodInfo _register;

            public ReflectedAaoApi(MethodInfo isUsed, MethodInfo register)
            {
                _isUsed = isUsed;
                _register = register;
            }

            public bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
            {
                return (bool)_isUsed.Invoke(null, new object[] { renderer, channel });
            }

            public void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
            {
                _register.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
            }
        }
    }
}
