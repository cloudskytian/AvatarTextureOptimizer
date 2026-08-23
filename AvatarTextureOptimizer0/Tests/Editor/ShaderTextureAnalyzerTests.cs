using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using NUnit.Framework;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class ShaderTextureAnalyzerTests
    {
        [Test]
        public void LilToonOutlinePassClosureTablesMatchAuditedAlphaPaths()
        {
            var sharedSampler = new[]
            {
                "_AlphaMask", "_DissolveMask", "_DissolveNoiseMask",
                "_Main2ndBlendMask", "_Main2ndDissolveMask", "_Main2ndDissolveNoiseMask",
                "_Main3rdBlendMask", "_Main3rdDissolveMask", "_Main3rdDissolveNoiseMask"
            };
            foreach (var property in sharedSampler)
                Assert.That(ShaderTextureAnalyzer.LilOutlinePassUsesSharedSampler(property), Is.True, property);
            foreach (var property in new[] { "_MainTex", "_Main2ndTex", "_EmissionBlendMask", "_FurMask" })
                Assert.That(ShaderTextureAnalyzer.LilOutlinePassUsesSharedSampler(property), Is.False, property);

            foreach (var property in new[] { "_AlphaMask", "_Main2ndBlendMask", "_Main3rdBlendMask" })
                Assert.That(ShaderTextureAnalyzer.LilOutlinePassUsesOutlineUv(property), Is.True, property);
            foreach (var property in new[]
                     {
                         "_DissolveMask", "_DissolveNoiseMask", "_Main2ndDissolveMask",
                         "_Main2ndDissolveNoiseMask", "_Main2ndTex"
                     })
                Assert.That(ShaderTextureAnalyzer.LilOutlinePassUsesOutlineUv(property), Is.False, property);

            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample("Hidden/lilToonCutoutOutline"), Is.True);
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample("Hidden/lilToonTransparentOutline"), Is.True);
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample(
                "Hidden/lilToonCutoutOutline", requireShadowOutline: true), Is.True);
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample(
                "_lil/[Optional] lilToonOutlineOnlyCutout"), Is.True,
                "top-level alpha and dissolve are reachable in its forward outline pass");
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample(
                "_lil/[Optional] lilToonOutlineOnlyCutout", requireShadowOutline: true), Is.False,
                "the optional OutlineOnly wrapper omits SHADOW_CASTER_OUTLINE and its layer-alpha closure");
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample("Hidden/lilToonOutline"), Is.False,
                "LIL_RENDER=0 compiles every alpha closure out");
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample("Hidden/lilToonLiteCutoutOutline"), Is.False,
                "LIL_LITE does not compile the full AlphaMask/layer/dissolve closures");
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample("Hidden/lilToonMultiOutline", 0), Is.False);
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample("Hidden/lilToonMultiOutline", 1), Is.True);
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample("Hidden/lilToonMultiOutline", 2), Is.True);
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample("Hidden/lilToonMultiOutline", -1), Is.True);
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample("Hidden/lilToonMultiOutline", 3), Is.True);
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample(
                "Hidden/lilToonMultiOutline", 1, requireShadowOutline: true), Is.True);
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample(
                "Hidden/lilToonMultiOutline", 0, alphaClipKeyword: true), Is.True,
                "the compiled LIL_RENDER keyword wins over stale opaque metadata");
            Assert.That(ShaderTextureAnalyzer.LilOutlineAlphaPassCanSample(
                "Hidden/lilToonMultiOutline", 0, clipRectKeyword: true), Is.True,
                "the compiled LIL_RENDER keyword wins over stale opaque metadata");
        }

        [Test]
        public void StandardBaseMapsUseMainTexTransformInsteadOfOwnSt()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            try
            {
                material.SetTextureScale("_BumpMap", new Vector2(2f, 3f));
                Assert.That(Analyze(material, "_BumpMap").Safe, Is.True,
                    "Standard samples the normal map with the shared _MainTex transform");

                material.SetTextureScale("_MainTex", new Vector2(2f, 1f));
                Assert.That(Analyze(material, "_BumpMap").Safe, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void LilToonOutlineUsesOwnRawUvTransformNotMainTransform()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 is not installed in this test project");
            var material = new Material(shader);
            try
            {
                var initial = Analyze(material, "_OutlineTex");
                if (!initial.Safe) Assert.Ignore("The installed lilToon shader is not the verified package version/path");

                material.SetTextureScale("_MainTex", new Vector2(2f, 1f));
                Assert.That(Analyze(material, "_OutlineTex").Safe, Is.True,
                    "_OutlineTex starts from raw UV0 and is independent of _MainTex_ST");

                material.SetTextureScale("_OutlineTex", new Vector2(2f, 1f));
                Assert.That(Analyze(material, "_OutlineTex").Safe, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void LilToonMainUvAndOwnStFamiliesAreDistinguished()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 is not installed in this test project");
            var material = new Material(shader);
            try
            {
                var direct = Analyze(material, "_MainColorAdjustMask");
                var composed = Analyze(material, "_BacklightColorTex");
                if (!direct.Safe || !composed.Safe)
                    Assert.Ignore("The installed lilToon shader is not the verified package version/path");

                material.SetTextureScale("_MainColorAdjustMask", new Vector2(2f, 1f));
                Assert.That(Analyze(material, "_MainColorAdjustMask").Safe, Is.True,
                    "The shader samples this NoScaleOffset mask directly from fd.uvMain");

                material.SetTextureScale("_BacklightColorTex", new Vector2(2f, 1f));
                Assert.That(Analyze(material, "_BacklightColorTex").Safe, Is.False,
                    "The shader applies _BacklightColorTex_ST despite the NoScaleOffset declaration");

                material.SetTextureScale("_BacklightColorTex", Vector2.one);
                material.SetTextureScale("_MainTex", new Vector2(2f, 1f));
                Assert.That(Analyze(material, "_MainColorAdjustMask").Safe, Is.False);
                Assert.That(Analyze(material, "_BacklightColorTex").Safe, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void LilToonSharedSamplerChecksCurrentAnimatedAndControllerStates()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 is not installed in this test project");
            var material = new Material(shader);
            var main = NewClampTexture(FilterMode.Bilinear);
            var target = NewClampTexture(FilterMode.Bilinear);
            var animated = NewClampTexture(FilterMode.Bilinear);
            try
            {
                material.SetTexture("_MainTex", main);
                material.SetTexture("_MainColorAdjustMask", target);
                var initial = Analyze(material, "_MainColorAdjustMask");
                if (!initial.Safe) Assert.Ignore("The installed lilToon shader is not the verified package version/path");

                target.filterMode = FilterMode.Point;
                Assert.IsFalse(Analyze(material, "_MainColorAdjustMask").Safe,
                    "The target texture is sampled through sampler_MainTex, not through its own importer state");
                target.filterMode = FilterMode.Bilinear;

                animated.anisoLevel = main.anisoLevel + 1;
                Assert.IsFalse(Analyze(material, "_MainColorAdjustMask",
                    property => property == "_MainColorAdjustMask" ? new[] { animated } : Array.Empty<Texture2D>()).Safe,
                    "Every non-null animated texture candidate must match the effective shared sampler");

                Assert.IsFalse(Analyze(material, "_MainColorAdjustMask", null,
                    property => property == "_MainTex").Safe,
                    "Any controller object-reference curve, including one containing null frames, is unsafe");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(main);
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(animated);
            }
        }

        [TestCase("_FurMask")]
        [TestCase("_FurNoiseMask")]
        [TestCase("_DissolveMask")]
        [TestCase("_Main2ndDissolveNoiseMask")]
        public void LilToonFurAndDissolvePathsUseMainSamplerGate(string property)
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 is not installed in this test project");
            var material = new Material(shader);
            var main = NewClampTexture(FilterMode.Bilinear);
            var target = NewClampTexture(FilterMode.Bilinear);
            try
            {
                material.SetTexture("_MainTex", main);
                material.SetTexture(property, target);
                var initial = Analyze(material, property);
                if (!initial.Safe) Assert.Ignore("The installed lilToon shader is not the verified package version/path");

                target.mipMapBias = 0.5f;
                Assert.IsFalse(Analyze(material, property).Safe,
                    property + " must match sampler_MainTex including mip bias");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(main);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [TestCase("_AlphaMask")]
        [TestCase("_DissolveMask")]
        [TestCase("_DissolveNoiseMask")]
        [TestCase("_Main2ndBlendMask")]
        [TestCase("_Main2ndDissolveMask")]
        [TestCase("_Main2ndDissolveNoiseMask")]
        [TestCase("_Main3rdBlendMask")]
        [TestCase("_Main3rdDissolveMask")]
        [TestCase("_Main3rdDissolveNoiseMask")]
        public void LilToonOutlineAlphaPathsCheckBothPassSamplerControllers(string property)
        {
            var shader = Shader.Find("Hidden/lilToonCutoutOutline");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 cutout-outline shader is not installed");
            var material = new Material(shader);
            var main = NewClampTexture(FilterMode.Bilinear);
            var outline = NewClampTexture(FilterMode.Bilinear);
            var target = NewClampTexture(FilterMode.Bilinear);
            try
            {
                material.SetTexture("_MainTex", main);
                material.SetTexture("_OutlineTex", outline);
                material.SetTexture(property, target);
                if (!ShaderTextureAnalyzer.IsVerifiedLilToonMaterial(material))
                    Assert.Ignore("The installed shader is not the verified official lilToon 2.3.4 package asset");
                var initial = Analyze(material, property);
                Assert.That(initial.Safe, Is.True, initial.Reason);

                outline.wrapModeV = TextureWrapMode.Repeat;
                Assert.IsFalse(Analyze(material, property).Safe,
                    "The outline alpha path aliases sampler_MainTex to sampler_OutlineTex for " + property);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(main);
                UnityEngine.Object.DestroyImmediate(outline);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [TestCase("_AlphaMask")]
        [TestCase("_Main2ndBlendMask")]
        [TestCase("_Main3rdBlendMask")]
        public void LilToonOutlineFdUvMainConsumersCheckOutlineTransform(string property)
        {
            var shader = Shader.Find("Hidden/lilToonCutoutOutline");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 cutout-outline shader is not installed");
            var material = new Material(shader);
            var main = NewClampTexture(FilterMode.Bilinear);
            var outline = NewClampTexture(FilterMode.Bilinear);
            var target = NewClampTexture(FilterMode.Bilinear);
            try
            {
                material.SetTexture("_MainTex", main);
                material.SetTexture("_OutlineTex", outline);
                material.SetTexture(property, target);
                if (!ShaderTextureAnalyzer.IsVerifiedLilToonMaterial(material))
                    Assert.Ignore("The installed shader is not the verified official lilToon 2.3.4 package asset");
                var initial = Analyze(material, property);
                Assert.That(initial.Safe, Is.True, initial.Reason);

                material.SetTextureScale("_OutlineTex", new Vector2(2f, 1f));
                Assert.IsFalse(Analyze(material, property).Safe,
                    property + " consumes outline fd.uvMain in an outline alpha pass");
                material.SetTextureScale("_OutlineTex", Vector2.one);
                Assert.IsFalse(Analyze(material, property, null, null,
                    animated => animated == "_OutlineTex_ScrollRotate").Safe,
                    property + " must reject animated outline UV controls");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(main);
                UnityEngine.Object.DestroyImmediate(outline);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void LilToonEmissionAndLayerSamplerFamiliesStayIndependent()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 is not installed in this test project");
            var material = new Material(shader);
            var main = NewClampTexture(FilterMode.Point);
            var ownSampler = NewClampTexture(FilterMode.Bilinear);
            var sharedSampler = NewClampTexture(FilterMode.Point);
            try
            {
                material.SetTexture("_MainTex", main);
                material.SetTexture("_EmissionMap", ownSampler);
                material.SetTexture("_Main2ndTex", ownSampler);
                material.SetTexture("_EmissionBlendMask", sharedSampler);
                material.SetTexture("_Main2ndBlendMask", sharedSampler);
                var initial = Analyze(material, "_EmissionMap");
                if (!initial.Safe) Assert.Ignore("The installed lilToon shader is not the verified package version/path");

                Assert.IsTrue(Analyze(material, "_EmissionMap").Safe,
                    "Emission maps declare sampler_EmissionMap and retain their own sampling state");
                Assert.IsTrue(Analyze(material, "_Main2ndTex").Safe,
                    "Second and third main textures declare independent samplers");

                sharedSampler.filterMode = FilterMode.Bilinear;
                Assert.IsFalse(Analyze(material, "_EmissionBlendMask").Safe,
                    "Emission masks are sampled through sampler_MainTex");
                Assert.IsFalse(Analyze(material, "_Main2ndBlendMask").Safe,
                    "Layer blend masks are sampled through sampler_MainTex");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(main);
                UnityEngine.Object.DestroyImmediate(ownSampler);
                UnityEngine.Object.DestroyImmediate(sharedSampler);
            }
        }

        [Test]
        public void LilToonMainUvRejectsBackfaceShiftAndItsAnimation()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 is not installed in this test project");
            var material = new Material(shader);
            try
            {
                var initial = Analyze(material, "_MainTex");
                if (!initial.Safe) Assert.Ignore("The installed lilToon shader is not the verified package version/path");

                material.SetFloat("_ShiftBackfaceUV", 1f);
                Assert.IsFalse(Analyze(material, "_MainTex").Safe,
                    "Backfaces would add one to U after atlas remapping");

                material.SetFloat("_ShiftBackfaceUV", 0f);
                Assert.IsFalse(Analyze(material, "_MainTex", null, null,
                    property => property == "_ShiftBackfaceUV").Safe,
                    "A float curve can enable the backface shift at runtime");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void LilToonRefractionBlurSmoothnessFixedSamplerIsRejected()
        {
            var shader = Shader.Find("Hidden/lilToonRefractionBlur");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 RefractionBlur shader is not installed");
            var material = new Material(shader);
            try
            {
                var main = Analyze(material, "_MainTex");
                if (!main.Safe) Assert.Ignore("The installed lilToon shader is not the verified package version/path");
                Assert.IsFalse(Analyze(material, "_SmoothnessTex").Safe,
                    "The blur pass uses lil_sampler_linear_repeat rather than sampler_MainTex");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [TestCase("_Bump2ndScaleMask")]
        [TestCase("_MatCapBumpMask")]
        public void MaskSemanticsOverrideBumpNameTokens(string property)
        {
            var texture = NewClampTexture(FilterMode.Bilinear);
            try
            {
                Assert.That(ShaderTextureAnalyzer.Classify(null, property, texture), Is.EqualTo(ATOTextureKind.Grayscale));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void LilToonGlitterUvZeroIncludesMainUvTransform()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 is not installed in this test project");
            var material = new Material(shader);
            var main = NewClampTexture(FilterMode.Bilinear);
            var glitter = NewClampTexture(FilterMode.Bilinear);
            try
            {
                material.SetTexture("_MainTex", main);
                material.SetTexture("_GlitterColorTex", glitter);
                material.SetFloat("_GlitterColorTex_UVMode", 0f);
                var initial = Analyze(material, "_GlitterColorTex");
                if (!initial.Safe) Assert.Ignore("The installed lilToon shader is not the verified package version/path");

                material.SetTextureScale("_MainTex", new Vector2(2f, 1f));
                Assert.IsFalse(Analyze(material, "_GlitterColorTex").Safe,
                    "UV mode zero starts from fd.uvMain rather than raw UV0");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(main);
                UnityEngine.Object.DestroyImmediate(glitter);
            }
        }

        [Test]
        public void LilToonFixedRepeatPathsAreRejected()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 is not installed in this test project");
            var material = new Material(shader);
            try
            {
                var main = Analyze(material, "_MainTex");
                if (!main.Safe) Assert.Ignore("The installed lilToon shader is not the verified package version/path");
                Assert.IsFalse(Analyze(material, "_Bump2ndMap").Safe);
                Assert.IsFalse(Analyze(material, "_ShadowStrengthMask").Safe);
                Assert.IsFalse(Analyze(material, "_ShadowBlurMask").Safe);
                Assert.IsFalse(Analyze(material, "_ShadowBorderMask").Safe);
                Assert.IsFalse(Analyze(material, "_OutlineWidthMask").Safe);
                Assert.IsFalse(Analyze(material, "_FurVectorTex").Safe);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void StandardFixedPackedChannelsAreReportedFromAuditedShaderSource()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            try
            {
                Assert.That(Analyze(material, "_OcclusionMap").UsedChannels, Is.EqualTo(ATOTextureChannels.G));
                Assert.That(Analyze(material, "_DetailMask").UsedChannels, Is.EqualTo(ATOTextureChannels.A));
                Assert.That(Analyze(material, "_MetallicGlossMap").UsedChannels,
                    Is.EqualTo(ATOTextureChannels.R | ATOTextureChannels.A));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [Test]
        public void LilToonFixedMaskChannelsMatchAuditedIncludes()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 is not installed in this test project");
            var material = new Material(shader);
            try
            {
                var scalar = Analyze(material, "_MainColorAdjustMask");
                if (!scalar.Safe) Assert.Ignore("The installed lilToon shader is not the verified package version/path");
                Assert.That(scalar.UsedChannels, Is.EqualTo(ATOTextureChannels.R));
                Assert.That(Analyze(material, "_MetallicGlossMap").UsedChannels, Is.EqualTo(ATOTextureChannels.R));
                Assert.That(Analyze(material, "_TriMask").UsedChannels, Is.EqualTo(ATOTextureChannels.Rgb));
                Assert.That(Analyze(material, "_MatCapBlendMask").UsedChannels, Is.EqualTo(ATOTextureChannels.Rgb));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [Test]
        public void VrcStandardLiteFixedPackedChannelsMatchAuditedShaderSource()
        {
            var shader = Shader.Find("VRChat/Mobile/Standard Lite");
            if (shader == null) Assert.Ignore("VRC SDK 3.10.4 Standard Lite shader is not installed");
            var material = new Material(shader);
            try
            {
                var occlusion = Analyze(material, "_OcclusionMap");
                if (!occlusion.Safe) Assert.Ignore("The installed VRC shader is not from the verified 3.10.4 package path");
                Assert.That(occlusion.UsedChannels, Is.EqualTo(ATOTextureChannels.G));
                Assert.That(Analyze(material, "_DetailMask").UsedChannels, Is.EqualTo(ATOTextureChannels.A));
                Assert.That(Analyze(material, "_MetallicGlossMap").UsedChannels,
                    Is.EqualTo(ATOTextureChannels.R | ATOTextureChannels.A));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [Test]
        public void VrcToonStandardDynamicMaskSelectorsRemainConservativeRgba()
        {
            var shader = Shader.Find("VRChat/Mobile/Toon Standard");
            if (shader == null) Assert.Ignore("VRC SDK 3.10.4 Toon Standard shader is not installed");
            var material = new Material(shader);
            try
            {
                var occlusion = Analyze(material, "_OcclusionMap");
                if (!occlusion.Safe) Assert.Ignore("The installed VRC shader is not from the verified 3.10.4 package path");
                Assert.That(occlusion.UsedChannels, Is.EqualTo(ATOTextureChannels.Rgba));
                Assert.That(Analyze(material, "_DetailMask").UsedChannels, Is.EqualTo(ATOTextureChannels.Rgba));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [Test]
        public void StandardSurfaceAlphaProofIsPropertySpecific()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            try
            {
                Assert.That(Analyze(material, "_MainTex").SurfaceAlphaUsage,
                    Is.EqualTo(ATOSurfaceAlphaUsage.TextureAlpha));
                Assert.That(Analyze(material, "_DetailAlbedoMap").SurfaceAlphaUsage,
                    Is.EqualTo(ATOSurfaceAlphaUsage.None));

                var color = material.GetColor("_Color"); color.a = 0.5f; material.SetColor("_Color", color);
                Assert.That(Analyze(material, "_MainTex").SurfaceAlphaUsage,
                    Is.EqualTo(ATOSurfaceAlphaUsage.UnsupportedComposite));

                color.a = 1f; material.SetColor("_Color", color);
                Assert.That(Analyze(material, "_MainTex", null, null, property => property == "_Color")
                    .SurfaceAlphaUsage, Is.EqualTo(ATOSurfaceAlphaUsage.UnsupportedComposite));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [Test]
        public void LilToonComplexAlphaPropertiesNeverClaimIndependentTextureAlpha()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 is not installed in this test project");
            var material = new Material(shader);
            try
            {
                var main = Analyze(material, "_MainTex");
                if (!main.Safe) Assert.Ignore("The installed lilToon shader is not the verified package version/path");
                Assert.That(main.SurfaceAlphaUsage, Is.EqualTo(ATOSurfaceAlphaUsage.UnsupportedComposite));
                Assert.That(Analyze(material, "_AlphaMask").SurfaceAlphaUsage,
                    Is.EqualTo(ATOSurfaceAlphaUsage.UnsupportedComposite));
                Assert.That(Analyze(material, "_Main2ndTex").SurfaceAlphaUsage,
                    Is.EqualTo(ATOSurfaceAlphaUsage.UnsupportedComposite));
                Assert.That(Analyze(material, "_DissolveMask").SurfaceAlphaUsage,
                    Is.EqualTo(ATOSurfaceAlphaUsage.UnsupportedComposite));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [TestCase("VRChat/Mobile/Diffuse", "_MainTex")]
        [TestCase("VRChat/Mobile/Bumped Diffuse", "_MainTex")]
        [TestCase("VRChat/Mobile/Bumped Mapped Specular", "_MainTex")]
        [TestCase("VRChat/Mobile/Standard Lite", "_MainTex")]
        [TestCase("VRChat/Mobile/Toon Lit", "_MainTex")]
        [TestCase("VRChat/Mobile/Toon Standard", "_MainTex")]
        [TestCase("VRChat/Mobile/Toon Standard (Outline)", "_MainTex")]
        public void VerifiedVrcMobileFamiliesAreFixedOpaque(string shaderName, string property)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) Assert.Ignore("VRC SDK 3.10.4 shader is not installed: " + shaderName);
            var material = new Material(shader);
            try
            {
                var info = Analyze(material, property);
                if (!info.Safe) Assert.Ignore("The installed VRC shader is not from the verified 3.10.4 package path");
                Assert.That(info.SurfaceAlphaUsage, Is.EqualTo(ATOSurfaceAlphaUsage.None),
                    "A fixed opaque VRC pass must treat texture alpha as ordinary channel data");

                // A queue override does not change the audited shader's fixed Blend/ZWrite code. Even if the broad
                // mode detector becomes conservative, the source alpha semantics remain non-compositing.
                material.renderQueue = 3000;
                Assert.That(AnimationAnalyzer.DetectAlphaMode(material), Is.EqualTo(ATOAlphaMode.Blend));
                Assert.That(Analyze(material, property).SurfaceAlphaUsage,
                    Is.EqualTo(ATOSurfaceAlphaUsage.None));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [Test]
        public void UnknownShaderNeverAcquiresSurfaceAlphaEquation()
        {
            var shader = Shader.Find("Unlit/Texture");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            try
            {
                var main = Analyze(material, "_MainTex");
                Assert.That(main.Safe, Is.False);
                Assert.That(main.SurfaceAlphaUsage, Is.EqualTo(ATOSurfaceAlphaUsage.UnsupportedComposite));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [Test]
        public void LilToonDirectMainAlphaRequiresEveryCombinerDisabled()
        {
            var shader = Shader.Find("Hidden/lilToonCutout");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 cutout shader is not installed in this test project");
            var material = new Material(shader);
            try
            {
                var main = Analyze(material, "_MainTex");
                if (!main.Safe) Assert.Ignore("The installed lilToon shader is not the verified package version/path");
                Assert.That(main.SurfaceAlphaUsage, Is.EqualTo(ATOSurfaceAlphaUsage.TextureAlpha));
                Assert.That(Analyze(material, "_AlphaMask").SurfaceAlphaUsage,
                    Is.EqualTo(ATOSurfaceAlphaUsage.None));

                material.SetFloat("_AlphaMaskMode", 2f);
                Assert.That(Analyze(material, "_MainTex").SurfaceAlphaUsage,
                    Is.EqualTo(ATOSurfaceAlphaUsage.UnsupportedComposite));
                Assert.That(Analyze(material, "_AlphaMask").SurfaceAlphaUsage,
                    Is.EqualTo(ATOSurfaceAlphaUsage.UnsupportedComposite));
                material.SetFloat("_AlphaMaskMode", 0f);

                Assert.That(Analyze(material, "_MainTex", null, null,
                        property => property == "_DissolveParams").SurfaceAlphaUsage,
                    Is.EqualTo(ATOSurfaceAlphaUsage.UnsupportedComposite));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [TestCase("Hidden/lilToonCutout", true, false)]
        [TestCase("Hidden/lilToonTransparent", false, true)]
        [TestCase("Hidden/lilToonTessellationTwoPassTransparentOutline", false, true)]
        [TestCase("Hidden/lilToonMultiRefraction", false, true)]
        [TestCase("Hidden/lilToonMultiFur", true, false)]
        [TestCase("Hidden/lilToonMultiGem", false, false)]
        [TestCase("_lil/lilToonMulti", false, false)]
        [TestCase("_lil/[Optional] lilToonFakeShadow", false, false)]
        [TestCase("SomePackage/Hidden/lilToonTransparent", false, false)]
        public void FixedLilPassMapIsExactAndExcludesDynamicOrFakeShadowShaders(string shaderName,
            bool expectedCutout, bool expectedBlend)
        {
            ShaderTextureAnalyzer.FixedLilPassAlphaFlags(shaderName, out var cutout, out var blend);
            Assert.That(cutout, Is.EqualTo(expectedCutout));
            Assert.That(blend, Is.EqualTo(expectedBlend));
        }

        [TestCase("Hidden/lilToonCutout", ATOAlphaMode.Cutout)]
        [TestCase("Hidden/lilToonTransparent", ATOAlphaMode.Blend)]
        public void VerifiedFixedLilPassCannotBeHiddenByStaleOpaqueMaterialMetadata(string shaderName,
            ATOAlphaMode expected)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 fixed-pass shader is not installed: " + shaderName);
            var material = new Material(shader);
            try
            {
                var main = Analyze(material, "_MainTex");
                if (!main.Safe) Assert.Ignore("The installed lilToon shader is not the verified package version/path");
                ForceRedundantOpaqueState(material);

                Assert.That(AnimationAnalyzer.DetectAlphaMode(material), Is.EqualTo(expected),
                    "compiled fixed-pass semantics must survive stale queue/tag/keyword/mode/Blend metadata");
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [TestCase("UNITY_UI_ALPHACLIP", ATOAlphaMode.Cutout)]
        [TestCase("UNITY_UI_CLIP_RECT", ATOAlphaMode.Blend)]
        public void LilReplacementKeywordsConservativelyEnterAlphaQualityModes(string keyword,
            ATOAlphaMode expected)
        {
            var shader = Shader.Find("Standard");
            if (shader == null) Assert.Ignore("Unity Standard shader is unavailable.");
            var material = new Material(shader);
            try
            {
                ForceRedundantOpaqueState(material);
                material.EnableKeyword(keyword);
                Assert.That(AnimationAnalyzer.DetectAlphaMode(material), Is.EqualTo(expected));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [TestCase("UNITY_UI_ALPHACLIP", ATOAlphaMode.Cutout)]
        [TestCase("UNITY_UI_CLIP_RECT", ATOAlphaMode.Blend)]
        public void VerifiedLilMultiReplacementKeywordSelectsCompiledAlphaMode(string keyword,
            ATOAlphaMode expected)
        {
            var shader = Shader.Find("_lil/lilToonMulti");
            if (shader == null) Assert.Ignore("Official lilToon 2.3.4 Multi shader is not installed.");
            var material = new Material(shader);
            try
            {
                var main = Analyze(material, "_MainTex");
                if (!main.Safe) Assert.Ignore("The installed lilToon Multi shader is not the verified package version/path");
                ForceRedundantOpaqueState(material);
                material.EnableKeyword(keyword);

                Assert.That(AnimationAnalyzer.DetectAlphaMode(material), Is.EqualTo(expected),
                    "the 2.3.4 replacement include maps this material keyword to LIL_RENDER");
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [Test]
        public void NonSurfaceAlphaUsesStraightRgbaDataKind()
        {
            var kind = AvatarAnalyzer.ResolveTextureKindForAlpha(ATOTextureKind.ColorAlpha, true,
                ATOSurfaceAlphaUsage.None, out var drivesAlpha, out var evaluatesChannels);
            Assert.That(kind, Is.EqualTo(ATOTextureKind.ColorRgbaData));
            Assert.That(drivesAlpha, Is.False);
            Assert.That(evaluatesChannels, Is.True);

            kind = AvatarAnalyzer.ResolveTextureKindForAlpha(ATOTextureKind.ColorAlpha, true,
                ATOSurfaceAlphaUsage.TextureAlpha, out drivesAlpha, out evaluatesChannels);
            Assert.That(kind, Is.EqualTo(ATOTextureKind.ColorAlpha));
            Assert.That(drivesAlpha, Is.True);
            Assert.That(evaluatesChannels, Is.False);
        }

        [Test]
        public void ExtensionCannotClearOrIntroduceUnsupportedSurfaceAlphaWithoutFallback()
        {
            Assert.That(AvatarAnalyzer.RequiresSurfaceAlphaFallback(true,
                ATOSurfaceAlphaUsage.UnsupportedComposite, ATOSurfaceAlphaUsage.TextureAlpha), Is.True,
                "an extension must not clear the built-in unsupported-composite conclusion");
            Assert.That(AvatarAnalyzer.RequiresSurfaceAlphaFallback(true,
                ATOSurfaceAlphaUsage.TextureAlpha, ATOSurfaceAlphaUsage.UnsupportedComposite), Is.True,
                "UnsupportedComposite is itself a fail-closed declaration even without RejectAsUnsafe");
            Assert.That(AvatarAnalyzer.RequiresSurfaceAlphaFallback(false,
                ATOSurfaceAlphaUsage.None, ATOSurfaceAlphaUsage.UnsupportedComposite), Is.False,
                "alpha semantics are irrelevant when the material has no alpha render state");
        }

        [Test]
        public void SurfaceAlphaAlwaysUsesAnAlphaPreservingTextureKind()
        {
            var kind = AvatarAnalyzer.ResolveTextureKindForAlpha(ATOTextureKind.ColorOpaque, true,
                ATOSurfaceAlphaUsage.TextureAlpha, out var drivesAlpha, out var evaluatesChannels);
            Assert.That(kind, Is.EqualTo(ATOTextureKind.ColorAlpha));
            Assert.That(drivesAlpha, Is.True);
            Assert.That(evaluatesChannels, Is.False);
        }

        private static void ForceRedundantOpaqueState(Material material)
        {
            material.SetOverrideTag("RenderType", "Opaque");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            foreach (var keyword in new[]
                     {
                         "_ALPHATEST_ON", "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON",
                         "UNITY_UI_ALPHACLIP", "UNITY_UI_CLIP_RECT"
                     })
                material.DisableKeyword(keyword);
            foreach (var property in new[] { "_Mode", "_Surface", "_AlphaClip", "_TransparentMode" })
                if (material.HasProperty(property)) material.SetFloat(property, 0f);
            foreach (var property in new[]
                     {
                         "_SrcBlend", "_SrcBlendAlpha", "_PreSrcBlend", "_PreSrcBlendAlpha",
                         "_OutlineSrcBlend", "_OutlineSrcBlendAlpha", "_FurSrcBlend", "_FurSrcBlendAlpha"
                     })
                if (material.HasProperty(property))
                    material.SetFloat(property, (float)UnityEngine.Rendering.BlendMode.One);
            foreach (var property in new[]
                     {
                         "_DstBlend", "_DstBlendAlpha", "_PreDstBlend", "_PreDstBlendAlpha",
                         "_OutlineDstBlend", "_OutlineDstBlendAlpha", "_FurDstBlend", "_FurDstBlendAlpha"
                     })
                if (material.HasProperty(property))
                    material.SetFloat(property, (float)UnityEngine.Rendering.BlendMode.Zero);
        }

        private static Texture2D NewClampTexture(FilterMode filter)
        {
            return new Texture2D(4, 4, TextureFormat.RGBA32, true, false)
            {
                filterMode = filter,
                wrapModeU = TextureWrapMode.Clamp,
                wrapModeV = TextureWrapMode.Clamp,
                anisoLevel = 1,
                mipMapBias = 0f
            };
        }

        private static ShaderTextureInfo Analyze(Material material, string property,
            Func<string, IEnumerable<Texture2D>> animatedTextures = null,
            Func<string, bool> textureAnimated = null,
            Func<string, bool> transformAnimated = null)
        {
            var results = new ShaderTextureAnalyzer().Analyze(material, transformAnimated ?? (_ => false),
                    animatedTextures, textureAnimated)
                .Where(value => string.Equals(value.PropertyName, property, StringComparison.Ordinal)).ToArray();
            Assert.That(results, Has.Length.EqualTo(1), "Shader does not declare expected texture property " + property);
            return results[0];
        }
    }
}
