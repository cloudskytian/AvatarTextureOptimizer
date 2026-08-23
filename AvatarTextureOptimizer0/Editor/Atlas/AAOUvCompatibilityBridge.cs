using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using UnityEditor;
using UnityEditor.Compilation;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>Optional reflection bridge to AAO 1.8+; the package remains usable when AAO is absent. / AAO 缺失时仍可用。</summary>
    internal sealed class AAOUvCompatibilityBridge
    {
        private sealed class Evacuation
        {
            public SkinnedMeshRenderer Renderer;
            public RendererRecord Record;
            public int Original, Saved;
            public bool Copied;
        }

        private const string DefaultEvacuationType = "Anatawa12.AvatarOptimizer.InternalEvacuateUVChannel";
        private const string DefaultRevertType = "Anatawa12.AvatarOptimizer.InternalRevertEvacuateUVChannel";
        private const string AaoPackageName = "com.anatawa12.avatar-optimizer";
        private const string AuditedAaoVersion = "1.9.17";
        private const string AuditedApiAssembly = "com.anatawa12.avatar-optimizer.api.editor";

        private readonly MethodInfo _isUsed;
        private readonly MethodInfo _register;
        private readonly string _unsupportedContract;
        private readonly string _evacuationTypeName;
        private readonly string _revertTypeName;
        private readonly List<Evacuation> _evacuations = new List<Evacuation>();
        private readonly Dictionary<Component, string> _componentSnapshots = new Dictionary<Component, string>();
        private readonly Dictionary<GameObject, HashSet<int>> _componentIds = new Dictionary<GameObject, HashSet<int>>();
        private bool _registered;
        private bool _rollbackRestored = true;

        public AAOUvCompatibilityBridge()
        {
            _evacuationTypeName = DefaultEvacuationType;
            _revertTypeName = DefaultRevertType;
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
            {
                try { return assembly.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI", false); }
                catch { return null; }
            }).FirstOrDefault(value => value != null);
            PackageInfo package = null;
            try
            {
                package = PackageInfo.GetAllRegisteredPackages()
                    .FirstOrDefault(value => string.Equals(value.name, AaoPackageName, StringComparison.Ordinal));
            }
            catch (Exception exception)
            {
                if (type != null)
                    _unsupportedContract = "AAO was detected but its registered package version could not be verified: " +
                                           exception.Message;
            }

            if (package == null && type == null) return; // AAO is optional and absent.
            if (_unsupportedContract != null) return;
            if (package == null || !IsAuditedPackageVersion(package.name, package.version))
            {
                _unsupportedContract = "detected AAO package is not the audited " + AaoPackageName + " " +
                                       AuditedAaoVersion + " contract";
                return;
            }
            if (type == null)
            {
                _unsupportedContract = "AAO " + AuditedAaoVersion + " is installed but its verified UV API is unavailable";
                return;
            }
            if (!TryVerifyApiAssemblyOrigin(type, package, out var originFailure))
            {
                _unsupportedContract = "AAO " + AuditedAaoVersion + " UV API assembly origin is not trusted: " +
                                       originFailure;
                return;
            }
            _isUsed = type.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(SkinnedMeshRenderer), typeof(int) }, null);
            _register = type.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(SkinnedMeshRenderer), typeof(int), typeof(int) }, null);
            if (!type.IsPublic || !type.IsAbstract || !type.IsSealed ||
                _isUsed == null || _isUsed.ReturnType != typeof(bool) ||
                _register == null || _register.ReturnType != typeof(void))
                _unsupportedContract = "AAO " + AuditedAaoVersion + " UV API signatures do not match the audited contract";
        }

        internal AAOUvCompatibilityBridge(MethodInfo isUsed, MethodInfo register,
            string evacuationTypeName = DefaultEvacuationType, string revertTypeName = DefaultRevertType,
            string unsupportedContract = null)
        {
            _isUsed = isUsed;
            _register = register;
            _unsupportedContract = unsupportedContract;
            _evacuationTypeName = evacuationTypeName;
            _revertTypeName = revertTypeName;
        }

        public void Analyze(AvatarAnalysis analysis)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            if (_unsupportedContract != null)
            {
                foreach (var record in analysis.Renderers.Where(value => value.Renderer is SkinnedMeshRenderer))
                {
                    var affected = analysis.UvGroups.Where(group => group.Renderer == record && group.AtlasSafe).ToArray();
                    if (affected.Length == 0) continue;
                    foreach (var group in affected) group.AtlasSafe = false;
                    analysis.Fallbacks.Add(new FallbackRecord(record.Renderer,
                        "AAO compatibility safety fallback: " + _unsupportedContract));
                }
                return;
            }
            if (_isUsed == null || _register == null) return;
            foreach (var record in analysis.Renderers.Where(value => value.Renderer is SkinnedMeshRenderer))
            {
                var renderer = (SkinnedMeshRenderer)record.Renderer;
                var modified = analysis.UvGroups.Where(group => group.Renderer == record && group.AtlasSafe)
                    .Select(group => group.UvChannel).Distinct().OrderBy(value => value).ToArray();
                var unavailable = new HashSet<int>(modified);
                for (var channel = 0; channel < 8; channel++)
                    if (record.Mesh.HasVertexAttribute((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel))) unavailable.Add(channel);
                var reservations = new List<Evacuation>(); var failed = false;
                try
                {
                    foreach (var original in modified)
                    {
                        if (!InvokeBool(_isUsed, renderer, original)) continue;
                        var saved = FindEvacuationChannel(unavailable,
                            channel => InvokeBool(_isUsed, renderer, channel));
                        if (saved < 0) { failed = true; break; }
                        unavailable.Add(saved); reservations.Add(new Evacuation
                        { Renderer = renderer, Record = record, Original = original, Saved = saved });
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[ATO] AAO UV compatibility query failed: " + Unwrap(exception).Message); failed = true;
                }
                if (failed)
                {
                    foreach (var group in analysis.UvGroups.Where(group => group.Renderer == record)) group.AtlasSafe = false;
                    analysis.Fallbacks.Add(new FallbackRecord(renderer,
                        "AAO uses a remapped UV channel but no proven-safe evacuation channel is available"));
                }
                else _evacuations.AddRange(reservations);
            }
        }

        public void CopyOriginalUvs(Renderer renderer, Mesh source, Mesh generated, IReadOnlyList<int> sourceVertices)
        {
            foreach (var evacuation in _evacuations.Where(value => value.Renderer == renderer))
            {
                var values = new List<Vector4>(); source.GetUVs(evacuation.Original, values);
                if (values.Count != source.vertexCount || sourceVertices.Count != generated.vertexCount)
                    throw new InvalidOperationException("AAO UV evacuation cannot preserve the source vertex mapping.");
                var copied = new List<Vector4>(sourceVertices.Count);
                foreach (var sourceVertex in sourceVertices) copied.Add(values[sourceVertex]);
                generated.SetUVs(evacuation.Saved, copied); evacuation.Copied = true;
            }
        }

        public void Register()
        {
            var copied = _evacuations.Where(value => value.Copied).ToArray();
            if (_registered || copied.Length == 0) return;
            foreach (var gameObject in copied.Select(value => value.Renderer.gameObject).Distinct())
            {
                var components = gameObject.GetComponents<Component>().Where(value => value != null).ToArray();
                _componentIds[gameObject] = new HashSet<int>(components.Select(value => value.GetInstanceID()));
                foreach (var component in components.Where(value => value.GetType().FullName == _evacuationTypeName))
                    _componentSnapshots[component] = EditorJsonUtility.ToJson(component);
            }
            try
            {
                foreach (var evacuation in copied)
                    _register.Invoke(null, new object[] { evacuation.Renderer, evacuation.Original, evacuation.Saved });
                _registered = true;
            }
            catch
            {
                Rollback(); throw;
            }
        }

        public bool Rollback()
        {
            // Preserve a prior failed internal rollback across Register()'s rethrow: the Pipeline invokes Rollback
            // again after catching the original registration exception and must still fail closed.
            var restored = _rollbackRestored;
            foreach (var snapshot in _componentSnapshots.ToArray())
            {
                try
                {
                    if (snapshot.Key == null)
                    {
                        restored = false;
                        Debug.LogError("[ATO] AAO rollback could not restore a component destroyed during registration.");
                        continue;
                    }
                    EditorJsonUtility.FromJsonOverwrite(snapshot.Value, snapshot.Key);
                    if (!string.Equals(EditorJsonUtility.ToJson(snapshot.Key), snapshot.Value,
                            StringComparison.Ordinal))
                    {
                        restored = false;
                        Debug.LogError("[ATO] AAO rollback component state did not match its pre-registration snapshot: " +
                                       snapshot.Key.GetType().FullName);
                    }
                }
                catch (Exception exception)
                {
                    restored = false;
                    Debug.LogError("[ATO] AAO rollback could not restore component " +
                                   (snapshot.Key == null ? "<destroyed>" : snapshot.Key.GetType().FullName) + ": " + exception);
                }
            }
            foreach (var pair in _componentIds.ToArray())
            {
                try
                {
                    if (pair.Key == null) continue;
                    foreach (var component in pair.Key.GetComponents<Component>())
                        if (component != null && !pair.Value.Contains(component.GetInstanceID()) &&
                            (component.GetType().FullName == _evacuationTypeName ||
                             component.GetType().FullName == _revertTypeName))
                        {
                            UnityEngine.Object.DestroyImmediate(component);
                            if (component != null)
                            {
                                restored = false;
                                Debug.LogError("[ATO] AAO rollback could not destroy a newly registered evacuation component.");
                            }
                        }
                }
                catch (Exception exception)
                {
                    restored = false;
                    Debug.LogError("[ATO] AAO rollback could not remove newly registered evacuation components: " + exception);
                }
            }
            _componentSnapshots.Clear();
            _componentIds.Clear();
            _registered = false;
            _rollbackRestored = restored;
            return restored;
        }

        private static bool TryVerifyApiAssemblyOrigin(Type apiType, PackageInfo registeredPackage,
            out string failure)
        {
            failure = null;
            if (apiType == null || registeredPackage == null)
            {
                failure = "the API type or registered package is missing";
                return false;
            }
            var reflectionAssemblyName = apiType.Assembly.GetName().Name;
            if (!IsAuditedApiAssemblyName(reflectionAssemblyName))
            {
                failure = "the reflected type belongs to assembly " + (reflectionAssemblyName ?? "<unnamed>");
                return false;
            }

            try
            {
                // Unity 2022.3 PackageInfo.FindForAssembly accepts System.Reflection.Assembly. Independently map
                // its exact audited asmdef name into compilation metadata and reject ambiguous matches before asking
                // UPM for the reflected assembly's owning registered package.
                // Unity 2022.3 的 FindForAssembly 接收反射程序集；仍先按已审计 asmdef 名唯一映射并拒绝歧义。
                var editorAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                if (!HasUniqueAuditedAssemblyMapping(reflectionAssemblyName,
                        editorAssemblies.Select(value => value == null ? null : value.name)))
                {
                    var matchCount = editorAssemblies.Count(value => value != null &&
                        string.Equals(value.name, reflectionAssemblyName, StringComparison.Ordinal));
                    failure = "Unity compilation metadata contains " + matchCount +
                              " matching API assemblies instead of exactly one";
                    return false;
                }
                var owner = PackageInfo.FindForAssembly(apiType.Assembly);
                if (owner == null || !IsAuditedPackageVersion(owner.name, owner.version))
                {
                    failure = "the API compilation assembly is not owned by the audited registered package";
                    return false;
                }
                if (!SameResolvedPackage(owner, registeredPackage))
                {
                    failure = "the API assembly owner differs from the package selected by the registry";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                failure = "package ownership lookup failed: " + exception.Message;
                return false;
            }
        }

        internal static bool IsAuditedPackageVersion(string packageName, string version) =>
            string.Equals(packageName, AaoPackageName, StringComparison.Ordinal) &&
            string.Equals(version, AuditedAaoVersion, StringComparison.Ordinal);

        internal static bool IsAuditedApiAssemblyName(string assemblyName) =>
            string.Equals(assemblyName, AuditedApiAssembly, StringComparison.Ordinal);

        internal static bool HasUniqueAuditedAssemblyMapping(string reflectionAssemblyName,
            IEnumerable<string> compilationAssemblyNames) =>
            IsAuditedApiAssemblyName(reflectionAssemblyName) && compilationAssemblyNames != null &&
            compilationAssemblyNames.Count(value =>
                string.Equals(value, reflectionAssemblyName, StringComparison.Ordinal)) == 1;

        internal static bool SameResolvedPackage(PackageInfo first, PackageInfo second) =>
            first != null && second != null && SameResolvedPackageIdentity(
                first.name, first.version, first.resolvedPath,
                second.name, second.version, second.resolvedPath);

        internal static bool SameResolvedPackageIdentity(string firstName, string firstVersion, string firstResolvedPath,
            string secondName, string secondVersion, string secondResolvedPath)
        {
            if (!IsAuditedPackageVersion(firstName, firstVersion) ||
                !IsAuditedPackageVersion(secondName, secondVersion) ||
                string.IsNullOrEmpty(firstResolvedPath) || string.IsNullOrEmpty(secondResolvedPath)) return false;
            try
            {
                var firstPath = Path.GetFullPath(firstResolvedPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var secondPath = Path.GetFullPath(secondResolvedPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(firstPath, secondPath,
                    Application.platform == RuntimePlatform.WindowsEditor
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        internal static int FindEvacuationChannel(ISet<int> unavailable, Func<int, bool> isUsed)
        {
            if (unavailable == null || isUsed == null) return -1;
            for (var channel = 0; channel < 8; channel++)
                if (!unavailable.Contains(channel) && !isUsed(channel)) return channel;
            // FirstOrDefault cannot be used here: its no-match result is channel 0, which is also a valid result.
            return -1;
        }

        private static bool InvokeBool(MethodInfo method, SkinnedMeshRenderer renderer, int channel) =>
            (bool)method.Invoke(null, new object[] { renderer, channel });
        private static Exception Unwrap(Exception value) => value is TargetInvocationException invocation && invocation.InnerException != null
            ? invocation.InnerException : value;
    }
}
