using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class AAOUvCompatibilityBridgeTests
    {
        private static int _registerCalls;
        private static Component _destroyBeforeFailure;

        [Test]
        public void OnlyAuditedAaoPackageVersionIsAccepted()
        {
            Assert.That(AAOUvCompatibilityBridge.IsAuditedPackageVersion(
                "com.anatawa12.avatar-optimizer", "1.9.17"), Is.True);
            Assert.That(AAOUvCompatibilityBridge.IsAuditedPackageVersion(
                "com.anatawa12.avatar-optimizer", "1.9.16"), Is.False);
            Assert.That(AAOUvCompatibilityBridge.IsAuditedPackageVersion(
                "com.anatawa12.avatar-optimizer", "1.10.0"), Is.False);
            Assert.That(AAOUvCompatibilityBridge.IsAuditedPackageVersion("other.package", "1.9.17"), Is.False);
        }

        [Test]
        public void OnlyAuditedAaoApiAssemblyIdentityIsAccepted()
        {
            Assert.That(AAOUvCompatibilityBridge.IsAuditedApiAssemblyName(
                "com.anatawa12.avatar-optimizer.api.editor"), Is.True);
            Assert.That(AAOUvCompatibilityBridge.IsAuditedApiAssemblyName(
                "com.anatawa12.avatar-optimizer.editor"), Is.False);
            Assert.That(AAOUvCompatibilityBridge.IsAuditedApiAssemblyName(
                "com.anatawa12.avatar-optimizer.api.editor.dll"), Is.False);
            Assert.That(AAOUvCompatibilityBridge.IsAuditedApiAssemblyName(null), Is.False);
        }

        [Test]
        public void SpoofedOrAmbiguousApiAssemblyMappingsAreRejected()
        {
            const string audited = "com.anatawa12.avatar-optimizer.api.editor";
            Assert.That(AAOUvCompatibilityBridge.HasUniqueAuditedAssemblyMapping(audited,
                new[] { audited }), Is.True);
            Assert.That(AAOUvCompatibilityBridge.HasUniqueAuditedAssemblyMapping("spoof.editor",
                new[] { audited }), Is.False);
            Assert.That(AAOUvCompatibilityBridge.HasUniqueAuditedAssemblyMapping(audited,
                new[] { audited, audited }), Is.False);
            Assert.That(AAOUvCompatibilityBridge.HasUniqueAuditedAssemblyMapping(audited,
                Array.Empty<string>()), Is.False);
        }

        [Test]
        public void ApiPackageOwnerMustMatchExactRegisteredIdentityAndResolvedPath()
        {
            var packagePath = Path.Combine(Application.dataPath, "..", "Packages",
                "com.anatawa12.avatar-optimizer");
            var equivalentPath = Path.Combine(packagePath, ".");
            var otherPath = Path.Combine(Application.dataPath, "..", "Packages", "spoof-package");

            Assert.That(AAOUvCompatibilityBridge.SameResolvedPackageIdentity(
                "com.anatawa12.avatar-optimizer", "1.9.17", packagePath,
                "com.anatawa12.avatar-optimizer", "1.9.17", equivalentPath), Is.True);
            Assert.That(AAOUvCompatibilityBridge.SameResolvedPackageIdentity(
                "com.anatawa12.avatar-optimizer", "1.9.17", packagePath,
                "com.anatawa12.avatar-optimizer", "1.9.17", otherPath), Is.False);
            Assert.That(AAOUvCompatibilityBridge.SameResolvedPackageIdentity(
                "com.anatawa12.avatar-optimizer", "1.9.17", packagePath,
                "com.anatawa12.avatar-optimizer", "1.9.16", packagePath), Is.False);
        }

        [Test]
        public void UnsupportedDetectedAaoContractFailsClosedForSkinnedUvGroups()
        {
            var gameObject = new GameObject("unsupported-aao");
            var mesh = NewUvMesh();
            try
            {
                var renderer = gameObject.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;
                var analysis = new AvatarAnalysis();
                AddRenderer(analysis, renderer, mesh);
                var bridge = new AAOUvCompatibilityBridge(null, null,
                    unsupportedContract: "simulated unaudited AAO version");

                bridge.Analyze(analysis);

                Assert.That(analysis.UvGroups.Single().AtlasSafe, Is.False);
                Assert.That(analysis.Fallbacks.Single().Reason,
                    Does.Contain("simulated unaudited AAO version"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void PartialRegistrationFailureRestoresExistingAndRemovesAddedComponents()
        {
            _registerCalls = 0;
            var firstObject = new GameObject("first");
            var secondObject = new GameObject("second");
            var firstSource = NewUvMesh(); var secondSource = NewUvMesh();
            var firstGenerated = NewUvMesh(); var secondGenerated = NewUvMesh();
            try
            {
                var firstRenderer = firstObject.AddComponent<SkinnedMeshRenderer>();
                var secondRenderer = secondObject.AddComponent<SkinnedMeshRenderer>();
                firstRenderer.sharedMesh = firstSource; secondRenderer.sharedMesh = secondSource;
                var existing = firstObject.AddComponent<BoxCollider>();
                existing.size = new Vector3(7f, 1f, 1f);

                var analysis = new AvatarAnalysis();
                AddRenderer(analysis, firstRenderer, firstSource);
                AddRenderer(analysis, secondRenderer, secondSource);
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var bridge = new AAOUvCompatibilityBridge(
                    typeof(AAOUvCompatibilityBridgeTests).GetMethod(nameof(IsUsed), flags),
                    typeof(AAOUvCompatibilityBridgeTests).GetMethod(nameof(Register), flags),
                    typeof(BoxCollider).FullName, typeof(SphereCollider).FullName);
                bridge.Analyze(analysis);
                bridge.CopyOriginalUvs(firstRenderer, firstSource, firstGenerated, new[] { 0 });
                bridge.CopyOriginalUvs(secondRenderer, secondSource, secondGenerated, new[] { 0 });

                Assert.Throws<TargetInvocationException>(() => bridge.Register());

                Assert.That(existing.size.x, Is.EqualTo(7f),
                    "an existing AAO evacuation component must be restored after a later registration fails");
                Assert.That(InjectedRegistrationComponents(secondObject), Is.Empty,
                    "all AAO evacuation components added before the failure must be removed");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(firstObject);
                UnityEngine.Object.DestroyImmediate(secondObject);
                UnityEngine.Object.DestroyImmediate(firstSource);
                UnityEngine.Object.DestroyImmediate(secondSource);
                UnityEngine.Object.DestroyImmediate(firstGenerated);
                UnityEngine.Object.DestroyImmediate(secondGenerated);
            }
        }

        [Test]
        public void FailedInternalRegistrationRollbackRemainsFailedForPipelineRetry()
        {
            _registerCalls = 0;
            var firstObject = new GameObject("first-rollback-failure");
            var secondObject = new GameObject("second-rollback-failure");
            var firstSource = NewUvMesh(); var secondSource = NewUvMesh();
            var firstGenerated = NewUvMesh(); var secondGenerated = NewUvMesh();
            try
            {
                var firstRenderer = firstObject.AddComponent<SkinnedMeshRenderer>();
                var secondRenderer = secondObject.AddComponent<SkinnedMeshRenderer>();
                firstRenderer.sharedMesh = firstSource; secondRenderer.sharedMesh = secondSource;
                _destroyBeforeFailure = firstObject.AddComponent<BoxCollider>();

                var analysis = new AvatarAnalysis();
                AddRenderer(analysis, firstRenderer, firstSource);
                AddRenderer(analysis, secondRenderer, secondSource);
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var bridge = new AAOUvCompatibilityBridge(
                    typeof(AAOUvCompatibilityBridgeTests).GetMethod(nameof(IsUsed), flags),
                    typeof(AAOUvCompatibilityBridgeTests).GetMethod(nameof(Register), flags),
                    typeof(BoxCollider).FullName, typeof(SphereCollider).FullName);
                bridge.Analyze(analysis);
                bridge.CopyOriginalUvs(firstRenderer, firstSource, firstGenerated, new[] { 0 });
                bridge.CopyOriginalUvs(secondRenderer, secondSource, secondGenerated, new[] { 0 });

                LogAssert.Expect(LogType.Error,
                    new Regex("\\[ATO\\] AAO rollback could not restore a component destroyed during registration\\."));
                Assert.Throws<TargetInvocationException>(() => bridge.Register());
                Assert.That(bridge.Rollback(), Is.False,
                    "the Pipeline retry must retain an earlier internal rollback failure after snapshots are cleared");
            }
            finally
            {
                _destroyBeforeFailure = null;
                UnityEngine.Object.DestroyImmediate(firstObject);
                UnityEngine.Object.DestroyImmediate(secondObject);
                UnityEngine.Object.DestroyImmediate(firstSource);
                UnityEngine.Object.DestroyImmediate(secondSource);
                UnityEngine.Object.DestroyImmediate(firstGenerated);
                UnityEngine.Object.DestroyImmediate(secondGenerated);
            }
        }

        private static void AddRenderer(AvatarAnalysis analysis, SkinnedMeshRenderer renderer, Mesh mesh)
        {
            var record = new RendererRecord { Renderer = renderer, Mesh = mesh };
            var slot = new MaterialSlotRecord();
            record.Slots.Add(slot); analysis.Renderers.Add(record);
            analysis.UvGroups.Add(new UvGroupRecord
            {
                Id = analysis.UvGroups.Count, Renderer = record, Slot = slot, UvChannel = 0, AtlasSafe = true
            });
        }

        private static Mesh NewUvMesh()
        {
            var mesh = new Mesh();
            mesh.SetVertices(new List<Vector3> { Vector3.zero });
            mesh.SetUVs(0, new List<Vector4> { new Vector4(0.25f, 0.75f, 0f, 0f) });
            return mesh;
        }

        private static bool IsUsed(SkinnedMeshRenderer renderer, int channel) => channel == 0;

        private static void Register(SkinnedMeshRenderer renderer, int original, int saved)
        {
            _registerCalls++;
            var component = renderer.GetComponent<BoxCollider>();
            if (component == null)
            {
                component = renderer.gameObject.AddComponent<BoxCollider>();
                renderer.gameObject.AddComponent<SphereCollider>();
            }
            component.size = new Vector3(component.size.x + 1f, component.size.y, component.size.z);
            if (_registerCalls == 2)
            {
                if (_destroyBeforeFailure != null) UnityEngine.Object.DestroyImmediate(_destroyBeforeFailure);
                throw new InvalidOperationException("simulated AAO registration failure");
            }
        }

        private static Component[] InjectedRegistrationComponents(GameObject gameObject) =>
            gameObject.GetComponents<Component>().Where(component =>
                component is BoxCollider || component is SphereCollider).ToArray();
    }
}
