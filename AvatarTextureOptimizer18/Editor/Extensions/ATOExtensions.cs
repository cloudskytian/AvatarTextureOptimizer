using System;
using System.Collections.Generic;
using System.Reflection;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Extensions
{
    // 扩展接口：供高级用户与第三方开发者自定义扩展。
    // - IATOPipelineHook：在指定管线阶段前后执行自定义逻辑（自动发现：实现类须有无参构造）。
    // - ATOAnalysisSnapshot：只读分析快照（供钩子读取分析结果）。
    // Extension interfaces for power users and third-party developers.
    // - IATOPipelineHook: custom logic before/after a pipeline stage (auto-discovered; needs a parameterless ctor).
    // - ATOAnalysisSnapshot: read-only analysis snapshot for hooks.

    // 只读分析快照。Read-only analysis snapshot.
    public sealed class ATOAnalysisSnapshot
    {
        public string avatarName;
        public string stageId;
        public int slotCount;
        public int materialCount;
        public int textureCount;
        public int islandCount;
        public int atlasCount;
        public int whitelistedTextureCount;
        public long dedupBytesSaved;
    }

    // 管线钩子接口。Pipeline hook interface.
    public interface IATOPipelineHook
    {
        // 钩子名称。Hook name.
        string Name { get; }

        // 在指定阶段前执行。Runs before the given stage.
        void OnBeforeStage(ATOAnalysisSnapshot snapshot, BuildContext context);

        // 在指定阶段后执行。Runs after the given stage.
        void OnAfterStage(ATOAnalysisSnapshot snapshot, BuildContext context);
    }

    // 扩展注册中心。Extension registry.
    public static class ATOExtensions
    {
        private static readonly List<IATOPipelineHook> Hooks = new List<IATOPipelineHook>();
        private static bool _scanned;

        // 手动注册钩子。Manually registers a hook.
        public static void Register(IATOPipelineHook hook)
        {
            if (hook == null) return;
            lock (Hooks)
            {
                if (!Hooks.Contains(hook)) Hooks.Add(hook);
            }
        }

        // 自动发现：扫描程序集，实现 IATOPipelineHook 且有公开无参构造的类型自动注册。
        // Auto-discovery: registers types implementing IATOPipelineHook with a public parameterless ctor.
        [InitializeOnLoadMethod]
        private static void AutoDiscover()
        {
            if (_scanned) return;
            _scanned = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        continue;
                    }
                    foreach (var t in types)
                    {
                        if (t.IsAbstract || t.IsInterface) continue;
                        if (!typeof(IATOPipelineHook).IsAssignableFrom(t)) continue;
                        if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                        try
                        {
                            Register((IATOPipelineHook)Activator.CreateInstance(t));
                            Debug.Log(ATOConstants.LogPrefix + " 已注册扩展钩子 / registered extension hook: " + t.FullName);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning(ATOConstants.LogPrefix + " 扩展钩子实例化失败 / hook instantiation failed: " + t.FullName + " (" + e.Message + ")");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(ATOConstants.LogPrefix + " 扩展自动发现失败 / auto-discovery failed: " + e.Message);
            }
        }

        // 内部调用：阶段前。Internal: before a stage.
        internal static void InvokeBefore(string stageId, ATOContext ctx)
        {
            var snapshot = BuildSnapshot(stageId, ctx);
            foreach (var hook in SnapshotHooks())
            {
                try
                {
                    hook.OnBeforeStage(snapshot, ctx.ndmf);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(ATOConstants.LogPrefix + " 扩展钩子异常 / hook exception: " + hook.Name + " (" + e.Message + ")");
                }
            }
        }

        // 内部调用：阶段后。Internal: after a stage.
        internal static void InvokeAfter(string stageId, ATOContext ctx)
        {
            var snapshot = BuildSnapshot(stageId, ctx);
            foreach (var hook in SnapshotHooks())
            {
                try
                {
                    hook.OnAfterStage(snapshot, ctx.ndmf);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(ATOConstants.LogPrefix + " 扩展钩子异常 / hook exception: " + hook.Name + " (" + e.Message + ")");
                }
            }
        }

        private static IATOPipelineHook[] SnapshotHooks()
        {
            lock (Hooks)
            {
                return Hooks.ToArray();
            }
        }

        private static ATOAnalysisSnapshot BuildSnapshot(string stageId, ATOContext ctx)
        {
            return new ATOAnalysisSnapshot
            {
                avatarName = ctx.avatarRoot != null ? ctx.avatarRoot.name : "",
                stageId = stageId,
                slotCount = ctx.slots.Count,
                materialCount = ctx.materials.Count,
                textureCount = ctx.textures.Count,
                islandCount = ctx.islandEntities.Count,
                atlasCount = ctx.atlasPlans.Count,
                whitelistedTextureCount = ctx.report.whitelistedTextureCount,
                dedupBytesSaved = ctx.report.dedupBytesSaved
            };
        }
    }
}
