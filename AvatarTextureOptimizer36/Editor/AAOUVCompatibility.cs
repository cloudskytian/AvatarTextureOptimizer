using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Optional AAO UV evacuation bridge. / 可选的 AAO UV 疏散桥接。
    /// </summary>
    internal static class AAOUVCompatibility
    {
        private static Type _apiType;
        private static MethodInfo _isUsed;
        private static MethodInfo _register;
        private static bool _searched;

        public static bool Prepare(SkinnedMeshRenderer renderer, int originalChannel, RendererRecord record, ATOLogger logger)
        {
            if (renderer == null || record == null) return false;
            if (record.RegisteredAAOChannels.Contains(originalChannel)) return true;
            EnsureMethods();
            if (_apiType == null) return true;

            try
            {
                int savedChannel = -1;
                for (int channel = 0; channel < 8; channel++)
                {
                    if (channel == originalChannel) continue;
                    bool used = (bool)_isUsed.Invoke(null, new object[] { renderer, channel });
                    if (!used)
                    {
                        savedChannel = channel;
                        break;
                    }
                }
                if (savedChannel < 0)
                {
                    logger.Warning("AAO is using all UV channels; UV rewrite is skipped for renderer '" + renderer.name + "'. / AAO 占用了全部 UV 通道，跳过 UV 改写。");
                    return false;
                }
                _register.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
                record.RegisteredAAOChannels.Add(originalChannel);
                logger.Detail("Registered AAO UV evacuation " + originalChannel + " -> " + savedChannel + ". / 已注册 AAO UV 疏散。");
                return true;
            }
            catch (TargetInvocationException exception)
            {
                logger.Warning("AAO UVUsageCompabilityAPI rejected evacuation; UV rewrite is skipped. / AAO API 拒绝疏散，跳过 UV 改写。 " +
                               (exception.InnerException == null ? exception.Message : exception.InnerException.Message));
                return false;
            }
            catch (Exception exception)
            {
                logger.Warning("AAO UVUsageCompabilityAPI could not be called; UV rewrite is skipped. / 无法调用 AAO API，跳过 UV 改写。 " + exception.Message);
                return false;
            }
        }

        private static void EnsureMethods()
        {
            if (_searched) return;
            _searched = true;
            string fullName = "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI";
            _apiType = Type.GetType(fullName + ", com.anatawa12.avatar-optimizer.api.editor");
            if (_apiType == null)
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length && _apiType == null; i++)
                    _apiType = assemblies[i].GetType(fullName, false);
            }
            if (_apiType == null) return;
            _isUsed = _apiType.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
            _register = _apiType.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
            if (_isUsed == null || _register == null)
            {
                _apiType = null;
                _isUsed = null;
                _register = null;
            }
        }
    }
}
