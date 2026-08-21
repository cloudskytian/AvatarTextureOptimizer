using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Resolves stable package-relative asset paths.
    /// 解析稳定的包内资源路径。
    /// </summary>
    internal static class AtoAssetLayout
    {
        private static string _packageRoot;

        public static string FindPackageRoot()
        {
            if (!string.IsNullOrEmpty(_packageRoot))
            {
                return _packageRoot;
            }

            var guid = AssetDatabase.FindAssets("AtoAssetLayout t:Script").FirstOrDefault();
            if (string.IsNullOrEmpty(guid))
            {
                _packageRoot = "Packages/net.fosa.avatar-texture-optimizer";
                return _packageRoot;
            }

            var scriptPath = AssetDatabase.GUIDToAssetPath(guid).Replace("\\", "/");
            var scriptDirectory = Path.GetDirectoryName(scriptPath)?.Replace("\\", "/") ?? string.Empty;
            var packageDirectory = Path.GetDirectoryName(Path.GetDirectoryName(scriptDirectory) ?? string.Empty)?.Replace("\\", "/") ?? string.Empty;
            _packageRoot = string.IsNullOrWhiteSpace(packageDirectory) ? "Packages/net.fosa.avatar-texture-optimizer" : packageDirectory;
            return _packageRoot;
        }
    }

    /// <summary>
    /// Central logging helper for ATO.
    /// ATO 统一日志助手。
    /// </summary>
    internal static class AtoLog
    {
        public const string Prefix = "[ATO]";

        public static void Info(string message)
        {
            Debug.Log($"{Prefix} {message}");
        }

        public static void Warn(string message)
        {
            Debug.LogWarning($"{Prefix} {message}");
        }

        public static void Error(string message)
        {
            Debug.LogError($"{Prefix} {message}");
        }

        public static void Stage(string stageName, Stopwatch stopwatch, string details)
        {
            Info($"Stage {stageName} finished in {stopwatch.Elapsed.TotalMilliseconds:F2} ms. {details}");
        }
    }

    /// <summary>
    /// Editor progress scope with optional cancellation support.
    /// 带可选取消支持的编辑器进度条作用域。
    /// </summary>
    internal sealed class AtoProgressScope : IDisposable
    {
        private readonly string _title;
        private readonly bool _enabled;
        private readonly bool _cancelable;

        public AtoProgressScope(string title, bool enabled, bool cancelable)
        {
            _title = title;
            _enabled = enabled && !Application.isBatchMode;
            _cancelable = cancelable;
        }

        public void Report(string info, float progress01, ref bool cancelled)
        {
            if (!_enabled)
            {
                return;
            }

            if (_cancelable)
            {
                if (EditorUtility.DisplayCancelableProgressBar(_title, info, Mathf.Clamp01(progress01)))
                {
                    cancelled = true;
                }
            }
            else
            {
                EditorUtility.DisplayProgressBar(_title, info, Mathf.Clamp01(progress01));
            }
        }

        public void Dispose()
        {
            if (_enabled)
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }

    /// <summary>
    /// Reflection helpers used to keep optional integrations soft-coupled.
    /// 反射辅助工具，用于让可选集成保持软依赖。
    /// </summary>
    internal static class AtoReflection
    {
        public static Type GetAvatarDescriptorType()
        {
            return Type.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor, VRCSDK3A");
        }

        public static Component GetAvatarDescriptor(GameObject gameObject)
        {
            var type = GetAvatarDescriptorType();
            return type == null ? null : gameObject.GetComponent(type);
        }

        public static bool IsAvatarDescriptorRoot(GameObject gameObject)
        {
            return GetAvatarDescriptor(gameObject) != null;
        }

        public static bool TryIsAaoTexCoordUsed(SkinnedMeshRenderer renderer, int channel, out bool used, out string failure)
        {
            used = false;
            failure = null;
            try
            {
                var apiType = Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor");
                if (apiType == null)
                {
                    failure = "AAO API not installed.";
                    return false;
                }

                var method = apiType.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    failure = "AAO UV query method missing.";
                    return false;
                }

                used = (bool)method.Invoke(null, new object[] { renderer, channel });
                return true;
            }
            catch (TargetInvocationException ex)
            {
                failure = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }
        }

        public static bool TryRegisterAaoUvEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel, out string failure)
        {
            failure = null;
            try
            {
                var apiType = Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor");
                if (apiType == null)
                {
                    failure = "AAO API not installed.";
                    return false;
                }

                var method = apiType.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    failure = "AAO UV evacuation method missing.";
                    return false;
                }

                method.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
                return true;
            }
            catch (TargetInvocationException ex)
            {
                failure = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Shared reporting helpers.
    /// 共享问题上报助手。
    /// </summary>
    internal static class AtoIssues
    {
        public static void ReportError(Object contextObject, string key, params object[] args)
        {
            using (ErrorReport.WithContextObject(contextObject))
            {
                ErrorReport.ReportError(AtoLocalization.Localizer, ErrorSeverity.Error, key, args);
            }
        }

        public static void ReportWarning(Object contextObject, string key, params object[] args)
        {
            using (ErrorReport.WithContextObject(contextObject))
            {
                ErrorReport.ReportError(AtoLocalization.Localizer, ErrorSeverity.NonFatal, key, args);
            }
        }

        public static void ReportInfo(Object contextObject, string key, params object[] args)
        {
            using (ErrorReport.WithContextObject(contextObject))
            {
                ErrorReport.ReportError(AtoLocalization.Localizer, ErrorSeverity.Information, key, args);
            }
        }
    }

    internal static class AtoExtensions
    {
        public static bool IsEditorOnly(this GameObject gameObject)
        {
            return gameObject != null && gameObject.CompareTag("EditorOnly");
        }

        public static string SafeAssetPath(this Object asset)
        {
            if (asset == null)
            {
                return "<null>";
            }

            var path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrWhiteSpace(path) ? "<scene or generated>" : path;
        }

        public static string HierarchyPath(this Transform transform)
        {
            if (transform == null)
            {
                return "<null>";
            }

            var stack = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                stack.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", stack);
        }
    }
}
