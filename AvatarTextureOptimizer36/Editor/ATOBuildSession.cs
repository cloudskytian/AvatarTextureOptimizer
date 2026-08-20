using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;
using Debug = UnityEngine.Debug;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Owns one build invocation and all temporary resources. / 管理一次构建调用及其全部临时资源。
    /// </summary>
    internal sealed class ATOBuildSession : IDisposable
    {
        private readonly BuildContextAdapter _context;
        private readonly AvatarTextureOptimizer _component;
        private readonly ATOPlatform _platform;
        private readonly ATOPlatformOptions _platformOptions;
        private readonly ATOProgress _progress;
        private readonly ATOLogger _logger;
        private readonly ATOBuildReport _report;
        private BuildSnapshot _snapshot;
        private bool _disposed;

        private ATOBuildSession(BuildContextAdapter context, AvatarTextureOptimizer component)
        {
            _context = context;
            _component = component;
            _platform = ATOPlatformResolver.Current();
            _platformOptions = component.ResolvePlatformOptions(_platform);
            _progress = new ATOProgress(component.showProgress);
            _logger = new ATOLogger(component.detailedLogging);
            _report = new ATOBuildReport(_platform, component.qualityPreset);
        }

        /// <summary>
        /// Executes the complete safe pipeline. / 执行完整的安全处理流水线。
        /// </summary>
        public static void Execute(nadena.dev.ndmf.BuildContext context)
        {
            ATOBuildSession session = null;
            try
            {
                using (new ATOProfilerScope("Total build"))
                {
                    BuildContextAdapter adapter = new BuildContextAdapter(context);
                    AvatarTextureOptimizer component = ValidateRoot(adapter);
                    session = new ATOBuildSession(adapter, component);
                    session.Run();
                }
            }
            catch (ATOUserCancelledException)
            {
                Debug.Log("[ATO] Build cancelled by user. Temporary generated assets were kept by NDMF.");
                throw;
            }
            catch (Exception exception)
            {
                ATOLogger.Error("Build aborted safely: " + exception.Message);
                throw;
            }
            finally
            {
                if (session != null) session.Dispose();
            }
        }

        private static AvatarTextureOptimizer ValidateRoot(BuildContextAdapter context)
        {
            AvatarTextureOptimizer[] components = context.Root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (components.Length != 1)
            {
                throw new InvalidOperationException(
                    "[ATO] Exactly one AvatarTextureOptimizer component is required under the avatar root; found " +
                    components.Length + ". / Avatar 根节点及子级必须且只能存在一个 AvatarTextureOptimizer 组件。"
                );
            }

            AvatarTextureOptimizer component = components[0];
            if (component.gameObject.GetComponent("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor") == null)
            {
                throw new InvalidOperationException(
                    "[ATO] The component host must also contain VRCAvatarDescriptor. / 组件挂载对象上必须存在 VRCAvatarDescriptor。"
                );
            }

            component.EnsureQualityParameters();
            return component;
        }

        private void Run()
        {
            using (_progress)
            using (_logger)
            {
                _logger.Info("Starting build for " + _context.Root.name + " on " + _platform);
                _progress.Step(0.02f, "Validate configuration / 检查配置");

                if (_component.minimumPadding > _platformOptions.maxAtlasSize / 2)
                {
                    _logger.Warning("Minimum padding is larger than half the atlas; clamped during packing. / 最小 padding 大于图集一半，装箱时会自动钳制。 ");
                }

                ATOExtensionContext extensionContext = new ATOExtensionContext(_context.Raw, _context.Root, _component, _platform);
                ATOExtensionRegistry.BeforeAnalyze(extensionContext, _logger);
                BuildSnapshot snapshot;
                using (_logger.Measure("Analyze avatar / 分析 Avatar"))
                using (_progress.Scope(0.05f, 0.22f, "Analyze materials, meshes and animations / 分析材质、网格与动画"))
                {
                    snapshot = AvatarSnapshotAnalyzer.Analyze(_context, _component, _platformOptions, _logger, _progress);
                    _snapshot = snapshot;
                }
                _report.SetAnalysis(snapshot);
                _progress.Step(0.30f, "Deduplicate source assets / 去重源资产");
                TextureDeduplication.Apply(snapshot, _context, _component, _logger, _report);

                _progress.Step(0.38f, "Build UV islands / 建立 UV 岛");
                UVIslandBuilder.Build(snapshot, _component, _logger, _progress);

                _progress.Step(0.46f, "Evaluate quality and scale / 评估质量并缩放");
                QualityPlanner.Plan(snapshot, _component.qualityParameters, _component.minimumPixelsPerMeter,
                    _component.maximumPixelsPerMeter, _logger, _progress, _report);

                if (_platformOptions.optimizeTextures && _component.optimizeTextures &&
                    _platformOptions.generateAtlases && _component.generateAtlases)
                {
                    _progress.Step(0.58f, "Pack and generate atlases / 装箱并生成图集");
                    AtlasPipeline.Generate(snapshot, _context, _component, _platformOptions, _logger, _progress, _report);
                }
                else if (_platformOptions.optimizeTextures && _component.optimizeTextures)
                {
                    _logger.Info("Atlas generation is disabled; preserving UV layout and applying whole-texture optimization only. / 未启用图集，保留 UV 布局，仅执行整图优化。");
                    TexturePipeline.OptimizeWholeTextures(snapshot, _context, _component, _platformOptions,
                        _logger, _progress, _report);
                }
                else
                {
                    _logger.Info("Texture optimization is disabled; no texture or UV references are changed. / 未启用纹理优化，不修改纹理与 UV 引用。");
                }

                _progress.Step(0.80f, "Apply import settings / 应用导入设置");
                TextureImportPipeline.Apply(snapshot, _context, _component, _platformOptions, _logger, _report);

                if (_component.enableMaterialDeduplication && _platformOptions.optimizeMaterials)
                {
                    _progress.Step(0.88f, "Deduplicate materials / 去重材质");
                    MaterialDeduplicator.Apply(snapshot, _context, _component, _logger, _report);
                }

                _progress.Step(0.94f, "Finalize report / 整理报告");
                _report.Finish();
                ATOExtensionRegistry.AfterBuild(extensionContext, new ATOExtensionSummary(_report), _logger);
                ATOReportPrinter.Print(_report, _logger);
                _logger.Info("Build completed successfully. / 构建成功完成。");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_snapshot != null) _snapshot.Dispose();
            _progress?.Dispose();
            _logger?.Dispose();
        }

        /// <summary>
        /// Adapter prevents the rest of the code from depending on NDMF internals. / 适配器防止其余代码依赖 NDMF 内部实现。
        /// </summary>
        internal sealed class BuildContextAdapter
        {
            public readonly nadena.dev.ndmf.BuildContext Raw;
            public readonly GameObject Root;

            public BuildContextAdapter(nadena.dev.ndmf.BuildContext raw)
            {
                Raw = raw ?? throw new ArgumentNullException(nameof(raw));
                Root = raw.AvatarRootObject;
            }

            public UnityEngine.Object AssetContainer => Raw.AssetContainer;

            public void RegisterReplacement(UnityEngine.Object oldObject, UnityEngine.Object newObject)
            {
                if (oldObject != null && newObject != null) Raw.ObjectRegistry.RegisterReplacedObject(oldObject, newObject);
            }

            public void SaveAsset(UnityEngine.Object asset)
            {
                if (asset == null) return;
                Raw.AssetSaver.SaveAsset(asset);
            }
        }
    }

    /// <summary>
    /// A cancellation signal that is not treated as a build error. / 不应被视为构建错误的取消信号。
    /// </summary>
    internal sealed class ATOUserCancelledException : Exception
    {
        public ATOUserCancelledException() : base("Avatar Texture Optimizer build cancelled.") { }
    }

    internal static class ATOPlatformResolver
    {
        public static ATOPlatform Current()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android:
                    return ATOPlatform.Android;
                case BuildTarget.iOS:
                    return ATOPlatform.iOS;
                default:
                    return ATOPlatform.PC;
            }
        }
    }

    /// <summary>
    /// Small disposable stopwatch used by every major stage. / 每个主要阶段使用的轻量可释放计时器。
    /// </summary>
    internal sealed class ATOProfilerScope : IDisposable
    {
        private readonly string _name;
        private readonly Stopwatch _watch;

        public ATOProfilerScope(string name)
        {
            _name = name;
            _watch = Stopwatch.StartNew();
            ATOLogger.Debug("Begin " + name);
        }

        public void Dispose()
        {
            _watch.Stop();
            ATOLogger.Debug(_name + " took " + _watch.Elapsed.TotalMilliseconds.ToString("F1") + " ms");
        }
    }

    internal sealed class ATOProgress : IDisposable
    {
        private readonly bool _enabled;
        private float _lastValue;
        private string _lastTitle = "Avatar Texture Optimizer / Avatar Texture Optimizer";
        private bool _disposed;

        public ATOProgress(bool enabled)
        {
            _enabled = enabled;
        }

        public void Step(float value, string title)
        {
            if (_disposed || !_enabled) return;
            _lastValue = Mathf.Clamp01(value);
            _lastTitle = title;
            if (EditorUtility.DisplayCancelableProgressBar("Avatar Texture Optimizer / Avatar Texture Optimizer", title, _lastValue))
                throw new ATOUserCancelledException();
        }

        public void CheckCancellation()
        {
            if (_disposed || !_enabled) return;
            if (EditorUtility.DisplayCancelableProgressBar("Avatar Texture Optimizer / Avatar Texture Optimizer", _lastTitle, _lastValue))
                throw new ATOUserCancelledException();
        }

        public ScopeHandle Scope(float from, float to, string title)
        {
            return new ScopeHandle(this, from, to, title);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_enabled) EditorUtility.ClearProgressBar();
        }

        internal sealed class ScopeHandle : IDisposable
        {
            private readonly ATOProgress _owner;
            private readonly float _from;
            private readonly float _to;
            private readonly string _title;
            private bool _disposed;

            public ScopeHandle(ATOProgress owner, float from, float to, string title)
            {
                _owner = owner;
                _from = from;
                _to = to;
                _title = title;
            }

            public void Report(float normalized)
            {
                if (_disposed) return;
                _owner.Step(Mathf.Lerp(_from, _to, Mathf.Clamp01(normalized)), _title);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _owner.Step(_to, _title);
            }
        }
    }
}
