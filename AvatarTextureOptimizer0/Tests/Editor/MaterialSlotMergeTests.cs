using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class MaterialSlotMergeTests
    {
        [Test]
        public void SeparatedSubmeshBoundsCanBeProvenDisjoint()
        {
            var mesh = CreateTwoTriangleMesh(3f);
            try
            {
                Assert.That(MaterialAnimationRewriter.SubmeshBoundsAreStrictlySeparated(mesh, 0, 1), Is.True);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void TouchingOrOverlappingSubmeshBoundsAreRejected()
        {
            var touching = CreateTwoTriangleMesh(1f);
            var overlapping = CreateTwoTriangleMesh(0.5f);
            try
            {
                Assert.That(MaterialAnimationRewriter.SubmeshBoundsAreStrictlySeparated(touching, 0, 1), Is.False);
                Assert.That(MaterialAnimationRewriter.SubmeshBoundsAreStrictlySeparated(overlapping, 0, 1), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(touching);
                Object.DestroyImmediate(overlapping);
            }
        }

        [Test]
        public void MergedMeshReplacementReclaimsOnlySupersededGeneratedMesh()
        {
            var source = new Mesh { name = "source" };
            var generated = new Mesh { name = "generated" };
            var merged = new Mesh { name = "merged" };
            var sourceAliasCommit = new MaterialAnimationRewriter.RendererCommit
                { BeforeMesh = source, AfterMesh = source };
            var aliasMerged = new Mesh { name = "alias-merged" };
            try
            {
                var commit = new MaterialAnimationRewriter.RendererCommit
                    { BeforeMesh = source, AfterMesh = generated };
                MaterialAnimationRewriter.ReplaceAfterMeshWithMerged(commit, merged);

                Assert.That(commit.AfterMesh, Is.SameAs(merged));
                Assert.That(generated == null, Is.True,
                    "the pre-merge transient has no owner after successful replacement and must be reclaimed");
                Assert.That(source == null, Is.False);

                MaterialAnimationRewriter.ReplaceAfterMeshWithMerged(sourceAliasCommit, aliasMerged);
                Assert.That(sourceAliasCommit.AfterMesh, Is.SameAs(aliasMerged));
                Assert.That(source == null, Is.False,
                    "the original source mesh must never be deleted even if an unexpected alias reaches the helper");
            }
            finally
            {
                if (source != null) Object.DestroyImmediate(source);
                if (generated != null) Object.DestroyImmediate(generated);
                if (merged != null) Object.DestroyImmediate(merged);
                if (aliasMerged != null) Object.DestroyImmediate(aliasMerged);
            }
        }

        [Test]
        public void SlotIndexMigrationRewritesMaterialAndPropertyBindings()
        {
            var materialBinding = EditorCurveBinding.PPtrCurve(string.Empty, typeof(MeshRenderer),
                "m_Materials.Array.data[3]");
            var propertyBinding = EditorCurveBinding.FloatCurve(string.Empty, typeof(MeshRenderer),
                "materials.Array.data[3]._Color.r");

            var movedMaterial = MaterialAnimationRewriter.RemapBinding(materialBinding, 1, null);
            var movedProperty = MaterialAnimationRewriter.RemapBinding(propertyBinding, 1, "_Color.r");
            var movedToFirstProperty = MaterialAnimationRewriter.RemapBinding(propertyBinding, 0, "_Color.r");

            Assert.That(movedMaterial.propertyName, Is.EqualTo("m_Materials.Array.data[1]"));
            Assert.That(movedProperty.propertyName, Is.EqualTo("materials.Array.data[1]._Color.r"));
            Assert.That(movedToFirstProperty.propertyName, Is.EqualTo("material._Color.r"));
            Assert.That(MaterialAnimationRewriter.TryParseBinding(movedMaterial.propertyName,
                out var materialSlot, out var materialProperty), Is.True);
            Assert.That(materialSlot, Is.EqualTo(1));
            Assert.That(materialProperty, Is.Null);
            Assert.That(MaterialAnimationRewriter.TryParseBinding(movedProperty.propertyName,
                out var propertySlot, out var property), Is.True);
            Assert.That(propertySlot, Is.EqualTo(1));
            Assert.That(property, Is.EqualTo("_Color.r"));
        }

        [Test]
        public void MaterialDeduplicationIdentityIgnoresOnlyCloneName()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            if (shader == null) Assert.Ignore("Unity internal error shader is unavailable.");
            var first = new Material(shader) { name = "ATO_First" };
            var second = Object.Instantiate(first); second.name = "ATO_Second";
            try
            {
                Assert.That(MaterialAnimationRewriter.MaterialIdentity(first),
                    Is.EqualTo(MaterialAnimationRewriter.MaterialIdentity(second)));
                second.renderQueue = first.renderQueue + 1;
                Assert.That(MaterialAnimationRewriter.MaterialIdentity(first),
                    Is.Not.EqualTo(MaterialAnimationRewriter.MaterialIdentity(second)),
                    "non-name material state must remain part of the deduplication key");
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void SlotMergeWorksWhenGlobalMaterialDeduplicationIsDisabled()
        {
            var shader = Shader.Find("Standard");
            if (shader == null) Assert.Ignore("Unity built-in Standard shader is unavailable.");
            var avatarObject = new GameObject("independent-slot-merge");
            var otherObject = new GameObject("independent-unmerged-material");
            var sourceMesh = CreateTwoTriangleMesh(3f);
            var otherMesh = new Mesh { name = "ATO_UnmergedSlotTest" };
            otherMesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            otherMesh.triangles = new[] { 0, 1, 2 };
            var sourceMaterial = new Material(shader) { name = "source-material" };
            Mesh mergedMesh = null;
            Material generatedMaterial = null;
            Material otherGeneratedMaterial = null;
            try
            {
                var filter = avatarObject.AddComponent<MeshFilter>();
                var renderer = avatarObject.AddComponent<MeshRenderer>();
                filter.sharedMesh = sourceMesh;
                renderer.sharedMaterials = new[] { sourceMaterial, sourceMaterial };
                var record = new RendererRecord
                    { Renderer = renderer, Mesh = sourceMesh, Path = string.Empty };
                for (var index = 0; index < 2; index++)
                {
                    var slot = new MaterialSlotRecord { Slot = index };
                    slot.Materials.Add(sourceMaterial);
                    slot.Bindings.Add(new TextureBindingRecord
                    {
                        Renderer = record,
                        Slot = slot,
                        Material = sourceMaterial,
                        AtlasSafe = true,
                        AlphaMode = ATOAlphaMode.Opaque
                    });
                    record.Slots.Add(slot);
                }
                var otherFilter = otherObject.AddComponent<MeshFilter>();
                var otherRenderer = otherObject.AddComponent<MeshRenderer>();
                otherFilter.sharedMesh = otherMesh;
                otherRenderer.sharedMaterials = new[] { sourceMaterial };
                var otherRecord = new RendererRecord
                    { Renderer = otherRenderer, Mesh = otherMesh, Path = "other" };
                var otherSlot = new MaterialSlotRecord { Slot = 0 };
                otherSlot.Materials.Add(sourceMaterial);
                otherSlot.Bindings.Add(new TextureBindingRecord
                {
                    Renderer = otherRecord,
                    Slot = otherSlot,
                    Material = sourceMaterial,
                    AtlasSafe = true,
                    AlphaMode = ATOAlphaMode.Opaque
                });
                otherRecord.Slots.Add(otherSlot);

                var analysis = new AvatarAnalysis();
                analysis.Renderers.Add(record); analysis.Renderers.Add(otherRecord);
                var saver = new RecordingSaver();
                var rewriter = new MaterialAnimationRewriter(saver,
                    new AnimationIndex(Array.Empty<VirtualNode>()), false, true);
                var transaction = rewriter.Apply(analysis, new AtlasPlan(), new AtlasBuildResult(),
                    new Dictionary<Renderer, Mesh>
                    {
                        [renderer] = sourceMesh,
                        [otherRenderer] = otherMesh
                    });
                try
                {
                    mergedMesh = filter.sharedMesh;
                    generatedMaterial = renderer.sharedMaterials.Single();
                    otherGeneratedMaterial = otherRenderer.sharedMaterials.Single();
                    Assert.That(mergedMesh, Is.Not.SameAs(sourceMesh));
                    Assert.That(mergedMesh.subMeshCount, Is.EqualTo(1));
                    Assert.That(renderer.sharedMaterials.Length, Is.EqualTo(1));
                    Assert.That(otherGeneratedMaterial, Is.Not.SameAs(generatedMaterial),
                        "slot-local identity matching must not enable global material deduplication");
                    Assert.That(saver.Saved.OfType<Material>().ToArray(),
                        Is.EquivalentTo(new[] { generatedMaterial, otherGeneratedMaterial }),
                        "the unreachable merged-slot clone must be pruned while the other Renderer clone remains");
                    transaction.Complete();
                    Assert.That(filter.sharedMesh, Is.SameAs(mergedMesh));
                    Assert.That(renderer.sharedMaterials.Single(), Is.SameAs(generatedMaterial));
                    Assert.That(otherRenderer.sharedMaterials.Single(), Is.SameAs(otherGeneratedMaterial));
                }
                finally { transaction.Dispose(); }
            }
            finally
            {
                Object.DestroyImmediate(avatarObject);
                Object.DestroyImmediate(otherObject);
                if (mergedMesh != null && mergedMesh != sourceMesh) Object.DestroyImmediate(mergedMesh);
                Object.DestroyImmediate(sourceMesh);
                Object.DestroyImmediate(otherMesh);
                if (otherGeneratedMaterial != null && otherGeneratedMaterial != generatedMaterial &&
                    otherGeneratedMaterial != sourceMaterial) Object.DestroyImmediate(otherGeneratedMaterial);
                if (generatedMaterial != null && generatedMaterial != sourceMaterial)
                    Object.DestroyImmediate(generatedMaterial);
                Object.DestroyImmediate(sourceMaterial);
            }
        }

        [Test]
        public void RendererCommitFailureRollsBackEarlierRendererMesh()
        {
            var firstObject = new GameObject("commit-first");
            var failingObject = new GameObject("commit-failing");
            var before = new Mesh { name = "before" };
            var after = new Mesh { name = "after" };
            var failingAfter = new Mesh { name = "failing-after" };
            try
            {
                var firstFilter = firstObject.AddComponent<MeshFilter>();
                var firstRenderer = firstObject.AddComponent<MeshRenderer>();
                firstFilter.sharedMesh = before;
                var failingRenderer = failingObject.AddComponent<MeshRenderer>(); // Intentionally no MeshFilter.
                var commits = new List<MaterialAnimationRewriter.RendererCommit>
                {
                    new MaterialAnimationRewriter.RendererCommit
                    {
                        Record = new RendererRecord { Renderer = firstRenderer },
                        BeforeMesh = before,
                        AfterMesh = after,
                        BeforeMaterials = new Material[0],
                        AfterMaterials = new Material[0]
                    },
                    new MaterialAnimationRewriter.RendererCommit
                    {
                        Record = new RendererRecord { Renderer = failingRenderer },
                        BeforeMesh = null,
                        AfterMesh = failingAfter,
                        BeforeMaterials = new Material[0],
                        AfterMaterials = new Material[0]
                    }
                };

                LogAssert.Expect(LogType.Error,
                    new Regex("\\[ATO\\] Transaction rollback failed for current renderer mesh:"));
                Assert.Throws<System.InvalidOperationException>(() =>
                    MaterialAnimationRewriter.CommitRendererChangesForTests(commits));

                Assert.That(firstFilter.sharedMesh, Is.SameAs(before));
                Assert.That(firstRenderer.sharedMaterials, Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(firstObject);
                Object.DestroyImmediate(failingObject);
                Object.DestroyImmediate(before);
                Object.DestroyImmediate(after);
                Object.DestroyImmediate(failingAfter);
            }
        }

        [Test]
        public void ApplyFailureWithIncompleteInternalRollbackSignalsUnsafeExternalCleanup()
        {
            var firstObject = new GameObject("apply-first");
            var failingObject = new GameObject("apply-failing");
            var before = new Mesh { name = "before" };
            var after = new Mesh { name = "after" };
            var failingAfter = new Mesh { name = "failing-after" };
            try
            {
                var firstFilter = firstObject.AddComponent<MeshFilter>();
                var firstRenderer = firstObject.AddComponent<MeshRenderer>();
                firstFilter.sharedMesh = before;
                var failingRenderer = failingObject.AddComponent<MeshRenderer>(); // Intentionally no MeshFilter.
                var firstRecord = new RendererRecord
                    { Renderer = firstRenderer, Mesh = before, Path = "first" };
                var failingRecord = new RendererRecord
                    { Renderer = failingRenderer, Mesh = null, Path = "failing" };
                var analysis = new AvatarAnalysis();
                analysis.Renderers.Add(firstRecord);
                analysis.Renderers.Add(failingRecord);
                var meshes = new Dictionary<Renderer, Mesh>
                {
                    [firstRenderer] = after,
                    [failingRenderer] = failingAfter
                };
                var rewriter = new MaterialAnimationRewriter(new RecordingSaver(),
                    new AnimationIndex(Array.Empty<VirtualNode>()), false, false);

                LogAssert.Expect(LogType.Error,
                    new Regex("\\[ATO\\] Transaction rollback failed for current renderer mesh:"));
                var exception = Assert.Throws<ATORollbackIncompleteException>(() =>
                    rewriter.Apply(analysis, new AtlasPlan(), new AtlasBuildResult(), meshes));

                Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(firstFilter.sharedMesh, Is.SameAs(before));
                Assert.That(after == null, Is.False,
                    "an incomplete rollback must retain all externally owned generated meshes");
                Assert.That(failingAfter == null, Is.False,
                    "the pipeline must receive an explicit unsafe-cleanup signal");
            }
            finally
            {
                Object.DestroyImmediate(firstObject);
                Object.DestroyImmediate(failingObject);
                Object.DestroyImmediate(before);
                if (after != null) Object.DestroyImmediate(after);
                if (failingAfter != null) Object.DestroyImmediate(failingAfter);
            }
        }

        [Test]
        public void SuccessfulAtlasApplyCanRollbackAfterLaterPipelineFailure()
        {
            var avatarObject = new GameObject("deferred-commit");
            var sourceMesh = new Mesh { name = "source" };
            var generatedMesh = new Mesh { name = "generated" };
            var shader = Shader.Find("Hidden/InternalErrorShader");
            if (shader == null)
            {
                Object.DestroyImmediate(avatarObject);
                Object.DestroyImmediate(sourceMesh);
                Object.DestroyImmediate(generatedMesh);
                Assert.Ignore("Unity internal error shader is unavailable.");
            }
            var sourceMaterial = new Material(shader) { name = "source-material" };
            try
            {
                var filter = avatarObject.AddComponent<MeshFilter>();
                var renderer = avatarObject.AddComponent<MeshRenderer>();
                filter.sharedMesh = sourceMesh;
                renderer.sharedMaterials = new[] { sourceMaterial };
                var record = new RendererRecord { Renderer = renderer, Mesh = sourceMesh, Path = string.Empty };
                var slot = new MaterialSlotRecord { Slot = 0 };
                slot.Materials.Add(sourceMaterial); record.Slots.Add(slot);
                var analysis = new AvatarAnalysis(); analysis.Renderers.Add(record);
                var meshes = new Dictionary<Renderer, Mesh> { [renderer] = generatedMesh };
                var saver = new RecordingSaver();
                var rewriter = new MaterialAnimationRewriter(saver,
                    new AnimationIndex(Array.Empty<VirtualNode>()), false, false);
                var transaction = rewriter.Apply(analysis, new AtlasPlan(), new AtlasBuildResult(), meshes);
                try
                {
                    Assert.That(saver.Saved, Does.Contain(generatedMesh),
                        "NDMF must register every generated mesh as a temporary asset before the renderer references it");
                    Assert.That(filter.sharedMesh, Is.SameAs(generatedMesh));
                    Assert.That(renderer.sharedMaterials[0], Is.Not.SameAs(sourceMaterial));
                    var rollbackRestored = false;
                    Assert.Throws<InvalidOperationException>(() =>
                    {
                        try { throw new InvalidOperationException("simulated final report failure"); }
                        finally { rollbackRestored = transaction.Rollback(); }
                    });
                    Assert.That(rollbackRestored, Is.True);
                    Assert.That(filter.sharedMesh, Is.SameAs(sourceMesh));
                    Assert.That(renderer.sharedMaterials[0], Is.SameAs(sourceMaterial));
                    Assert.That(generatedMesh == null, Is.True,
                        "a fully restored deferred transaction may release its transient generated mesh");
                }
                finally { transaction.Dispose(); }
            }
            finally
            {
                Object.DestroyImmediate(avatarObject);
                Object.DestroyImmediate(sourceMesh);
                if (generatedMesh != null) Object.DestroyImmediate(generatedMesh);
                Object.DestroyImmediate(sourceMaterial);
            }
        }

        [Test]
        public void SuccessfulAtlasCompleteAndDisposeKeepCommittedRendererReferences()
        {
            var avatarObject = new GameObject("completed-commit");
            var sourceMesh = new Mesh { name = "source" };
            var generatedMesh = new Mesh { name = "generated" };
            var shader = Shader.Find("Hidden/InternalErrorShader");
            if (shader == null)
            {
                Object.DestroyImmediate(avatarObject);
                Object.DestroyImmediate(sourceMesh);
                Object.DestroyImmediate(generatedMesh);
                Assert.Ignore("Unity internal error shader is unavailable.");
            }
            var sourceMaterial = new Material(shader) { name = "source-material" };
            Material generatedMaterial = null;
            try
            {
                var filter = avatarObject.AddComponent<MeshFilter>();
                var renderer = avatarObject.AddComponent<MeshRenderer>();
                filter.sharedMesh = sourceMesh;
                renderer.sharedMaterials = new[] { sourceMaterial };
                var record = new RendererRecord { Renderer = renderer, Mesh = sourceMesh, Path = string.Empty };
                var slot = new MaterialSlotRecord { Slot = 0 };
                slot.Materials.Add(sourceMaterial); record.Slots.Add(slot);
                var analysis = new AvatarAnalysis(); analysis.Renderers.Add(record);
                var rewriter = new MaterialAnimationRewriter(new RecordingSaver(),
                    new AnimationIndex(Array.Empty<VirtualNode>()), false, false);
                var transaction = rewriter.Apply(analysis, new AtlasPlan(), new AtlasBuildResult(),
                    new Dictionary<Renderer, Mesh> { [renderer] = generatedMesh });
                generatedMaterial = renderer.sharedMaterials[0];
                try
                {
                    Assert.That(filter.sharedMesh, Is.SameAs(generatedMesh));
                    Assert.That(generatedMaterial, Is.Not.SameAs(sourceMaterial));
                    Assert.DoesNotThrow(transaction.Complete);
                    Assert.DoesNotThrow(transaction.Dispose);
                    Assert.That(filter.sharedMesh, Is.SameAs(generatedMesh));
                    Assert.That(renderer.sharedMaterials[0], Is.SameAs(generatedMaterial));
                    Assert.That(generatedMesh == null, Is.False);
                }
                finally { transaction.Dispose(); }
            }
            finally
            {
                Object.DestroyImmediate(avatarObject);
                Object.DestroyImmediate(sourceMesh);
                if (generatedMesh != null) Object.DestroyImmediate(generatedMesh);
                Object.DestroyImmediate(sourceMaterial);
                if (generatedMaterial != null) Object.DestroyImmediate(generatedMaterial);
            }
        }

        [Test]
        public void AssetSaveFailureDoesNotExposePartialRendererCommit()
        {
            var avatarObject = new GameObject("save-failure");
            var sourceMesh = new Mesh { name = "source" };
            var generatedMesh = new Mesh { name = "generated" };
            var shader = Shader.Find("Hidden/InternalErrorShader");
            if (shader == null)
            {
                Object.DestroyImmediate(avatarObject);
                Object.DestroyImmediate(sourceMesh);
                Object.DestroyImmediate(generatedMesh);
                Assert.Ignore("Unity internal error shader is unavailable.");
            }
            var sourceMaterial = new Material(shader) { name = "source-material" };
            try
            {
                var filter = avatarObject.AddComponent<MeshFilter>();
                var renderer = avatarObject.AddComponent<MeshRenderer>();
                filter.sharedMesh = sourceMesh;
                renderer.sharedMaterials = new[] { sourceMaterial };
                var record = new RendererRecord { Renderer = renderer, Mesh = sourceMesh, Path = string.Empty };
                var slot = new MaterialSlotRecord { Slot = 0 };
                slot.Materials.Add(sourceMaterial); record.Slots.Add(slot);
                var analysis = new AvatarAnalysis(); analysis.Renderers.Add(record);
                var rewriter = new MaterialAnimationRewriter(new ThrowingSaver(),
                    new AnimationIndex(Array.Empty<VirtualNode>()), false, false);

                Assert.Throws<InvalidOperationException>(() => rewriter.Apply(analysis, new AtlasPlan(),
                    new AtlasBuildResult(), new Dictionary<Renderer, Mesh> { [renderer] = generatedMesh }));

                Assert.That(filter.sharedMesh, Is.SameAs(sourceMesh));
                Assert.That(renderer.sharedMaterials[0], Is.SameAs(sourceMaterial));
            }
            finally
            {
                Object.DestroyImmediate(avatarObject);
                Object.DestroyImmediate(sourceMesh);
                if (generatedMesh != null) Object.DestroyImmediate(generatedMesh);
                Object.DestroyImmediate(sourceMaterial);
            }
        }

        private sealed class RecordingSaver : IAssetSaver
        {
            private readonly List<Object> _saved = new List<Object>();
            public IReadOnlyList<Object> Saved => _saved;
            public Object CurrentContainer => null;
            public void SaveAsset(Object asset) { if (asset != null) _saved.Add(asset); }
            public bool IsTemporaryAsset(Object asset) => asset == null || !EditorUtility.IsPersistent(asset);
            public IEnumerable<Object> GetPersistedAssets() => _saved;
            public void Dispose() { }
        }

        private sealed class ThrowingSaver : IAssetSaver
        {
            public Object CurrentContainer => null;
            public void SaveAsset(Object asset) => throw new InvalidOperationException("simulated asset save failure");
            public bool IsTemporaryAsset(Object asset) => true;
            public IEnumerable<Object> GetPersistedAssets() => Enumerable.Empty<Object>();
            public void Dispose() { }
        }

        private static Mesh CreateTwoTriangleMesh(float secondOffsetX)
        {
            var mesh = new Mesh { name = "ATO_SlotMergeTest" };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f), new Vector3(1f, 0f), new Vector3(0f, 1f),
                new Vector3(secondOffsetX, 0f), new Vector3(secondOffsetX + 1f, 0f),
                new Vector3(secondOffsetX, 1f)
            };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 3, 4, 5 }, 1);
            return mesh;
        }
    }
}
