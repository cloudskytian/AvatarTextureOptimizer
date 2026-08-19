using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// The full ATO processing pipeline, executed in a single NDMF pass with staged progress
    /// reporting and cancellation support. / 完整 ATO 处理管线，在单个 NDMF pass 中执行，
    /// 带分阶段进度显示与取消支持。
    /// </summary>
    public static class ATOProcessor
    {
        public static void Run(nadena.dev.ndmf.BuildContext ctx)
        {
            var state = ctx.GetState<ATOState>();
            state.cancellation = new System.Threading.CancellationTokenSource();

            try
            {
                ValidateComponent(ctx, state);

                state.platform = ResolvePlatform();
                var avatar = ctx.AvatarRootObject;
                state.settings = FindComponent(avatar).GetSettings(state.platform);

                ATOLogger.Info($"start processing avatar '{avatar.name}' for platform {state.platform}, " +
                               $"preset {state.settings.qualityPreset}, atlas {(state.settings.generateAtlas ? "on" : "off")}");

                Progress("Collecting whitelist", 0.05f, state);
                using (ATOLogger.Timed("whitelist"))
                    TextureCollection.CollectWhitelist(avatar, state);

                Progress("Scanning animations", 0.10f, state);
                AnimationAnalysis.Result anim;
                using (ATOLogger.Timed("animation scan"))
                    anim = AnimationAnalysis.Scan(avatar);

                Progress("Collecting textures", 0.18f, state);
                RemapRegistry.Clear();
                using (ATOLogger.Timed("collect + dedup"))
                    TextureCollection.Collect(avatar, state, anim);

                Progress("Extracting UV islands", 0.26f, state);
                state.animatedMaterialSlots.Clear();
                foreach (var k in anim.materialSwitches.Keys) state.animatedMaterialSlots.Add(k);
                using (ATOLogger.Timed("island extraction + UV groups"))
                    BuildUvGroups(avatar, state, anim);

                Progress("Scaling islands to target quality", 0.40f, state);
                using (ATOLogger.Timed("quality scaling"))
                    ScaleGroups(state, anim);

                Progress("Packing atlases", 0.60f, state);
                using (ATOLogger.Timed("atlas packing"))
                    PackAtlases(state, ctx);

                Progress("Applying results", 0.80f, state);
                using (ATOLogger.Timed("apply"))
                    ApplyResults(state, ctx);

                Progress("Deduplicating materials and textures", 0.90f, state);
                using (ATOLogger.Timed("dedupe"))
                    Deduplicate(state, ctx);

                using (ATOLogger.Timed("compression + mipstreaming"))
                    ApplyCompressionAndMip(state);

                Progress("Finalizing report", 0.98f, state);
                state.EmitReport();
                ATOLogger.Info($"done. {state.atlases.Count} atlas(es), {state.uvGroups.Count} UV group(s).");

                EditorUtility.ClearProgressBar();
            }
            catch (SkipBuildException)
            {
                EditorUtility.ClearProgressBar();
            }
            catch (System.OperationCanceledException)
            {
                ATOLogger.Warn("cancelled by user; temporary assets on disk are kept.");
                EditorUtility.ClearProgressBar();
                throw;
            }
            catch (Exception e)
            {
                ATOLogger.Error("failed: " + e);
                EditorUtility.ClearProgressBar();
                throw;
            }
            finally
            {
                state.cancellation.Dispose();
                state.cancellation = null;
            }
        }

        // =====================================================================================
        // Stage 0 — validation
        // =====================================================================================

        private static void ValidateComponent(nadena.dev.ndmf.BuildContext ctx, ATOState state)
        {
            var avatar = ctx.AvatarRootObject;
            var comps = avatar.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (comps.Length == 0)
            {
                state.initialized = false;
                throw new SkipBuildException();
            }
            if (comps.Length > 1)
                state.Abort("multiple AvatarTextureOptimizer components found; only one is allowed per avatar.");
            if (comps[0].GetComponent<VRCAvatarDescriptor>() == null)
                state.Abort("AvatarTextureOptimizer must be on the same object as VRCAvatarDescriptor.");
            state.initialized = true;
        }

        private static AvatarTextureOptimizer FindComponent(GameObject avatar) =>
            avatar.GetComponentInChildren<AvatarTextureOptimizer>(true);

        private static ATOPlatform ResolvePlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return ATOPlatform.Android;
                case BuildTarget.iOS: return ATOPlatform.iOS;
                default: return ATOPlatform.PC;
            }
        }

        public sealed class SkipBuildException : Exception { }

        // =====================================================================================
        // Stage 4 — UV groups
        // =====================================================================================

        private static void BuildUvGroups(GameObject avatar, ATOState state, AnimationAnalysis.Result anim)
        {
            AssignSpecialFlags(state);
            state.uvGroups.Clear();

            foreach (var renderer in state.renderers)
            {
                bool enabled = renderer.enabled || anim.animatedRenderers.Contains(renderer);
                if (!enabled)
                {
                    ATOLogger.InfoDetail($"skipping disabled renderer {renderer.name}");
                    continue;
                }

                var mesh = GetSharedMesh(renderer);
                if (mesh == null) continue;

                var materials = renderer.sharedMaterials;

                // (slot, channel) → set of textures / （槽,通道）→ 贴图集合
                var slotChannelTextures = new Dictionary<(int, int), List<TextureEntry>>();

                for (int slot = 0; slot < materials.Length; slot++)
                {
                    var mat = materials[slot];
                    if (mat != null && mat.shader != null)
                        foreach (var r in GetReferencesForMaterial(state, mat))
                            AddToSlot(slotChannelTextures, slot, r.uvChannel, ResolveEntry(state, r.texture));

                    if (anim.materialSwitches.TryGetValue((renderer, slot), out var switched))
                        foreach (var sm in switched)
                            if (sm != null && sm.shader != null)
                                foreach (var r in GetReferencesForMaterial(state, sm))
                                    AddToSlot(slotChannelTextures, slot, r.uvChannel, ResolveEntry(state, r.texture));
                }

                if (slotChannelTextures.Count == 0) continue;

                // extract islands per channel (once) / 每通道提取一次岛
                var islandsByChannel = new Dictionary<int, List<UvIsland>>();
                foreach (var key in slotChannelTextures.Keys)
                {
                    if (!islandsByChannel.ContainsKey(key.Item2))
                        islandsByChannel[key.Item2] = IslandExtraction.Extract(mesh, key.Item2);
                }

                foreach (var kv in slotChannelTextures)
                {
                    int slot = kv.Key.Item1;
                    int channel = kv.Key.Item2;
                    var textures = kv.Value;

                    foreach (var island in islandsByChannel[channel])
                    {
                        if (island.submesh != slot) continue;

                        if (island.outOfRangeNeedsRepeat)
                        {
                            foreach (var t in textures) state.whitelistedTextures.Add(t.texture);
                            ATOLogger.SkipWarning(
                                $"island on {renderer.name} UV{channel} crosses wrap seam; textures whitelisted", renderer);
                            continue;
                        }

                        var group = new UvGroup
                        {
                            id = $"{renderer.name}/UV{channel}/S{slot}/I{island.islandIndex}",
                            renderer = renderer,
                            sourceMesh = mesh,
                            island = island,
                        };
                        group.textures.AddRange(textures);
                        state.uvGroups.Add(group);
                    }
                }
            }

            ATOLogger.Info($"built {state.uvGroups.Count} UV group(s)");
        }

        private static void AddToSlot(Dictionary<(int, int), List<TextureEntry>> map,
            int slot, int channel, TextureEntry entry)
        {
            if (entry == null) return;
            if (!map.TryGetValue((slot, channel), out var list)) { list = new List<TextureEntry>(); map[(slot, channel)] = list; }
            if (!list.Contains(entry)) list.Add(entry);
        }

        private static void AssignSpecialFlags(ATOState state)
        {
            foreach (var e in state.textures)
            {
                if (e.category.IsColor())
                {
                    var flags = ATOSpecialFlags.None;
                    foreach (var r in e.references)
                    {
                        if (MaterialHasSpecial(r.material, r.uvChannel, r.propertyName, true, state)) flags |= ATOSpecialFlags.HasNormal;
                        if (MaterialHasSpecial(r.material, r.uvChannel, r.propertyName, false, state)) flags |= ATOSpecialFlags.HasMask;
                    }
                    e.specialFlags = flags;
                }
            }
        }

        private static bool MaterialHasSpecial(Material mat, int uvChannel, string ownProperty, bool normal, ATOState state)
        {
            foreach (var r in GetReferencesForMaterial(state, mat))
            {
                if (r.uvChannel != uvChannel || r.propertyName == ownProperty) continue;
                if (normal && ShaderAnalysis.IsNormalProperty(r.propertyName, r.texture, mat)) return true;
                if (!normal && ShaderAnalysis.IsMaskProperty(r.propertyName)) return true;
            }
            return false;
        }

        // =====================================================================================
        // Stage 5 — scaling (per UV group, unified)
        // =====================================================================================

        private static void ScaleGroups(ATOState state, AnimationAnalysis.Result anim)
        {
            var q = IslandScaler.Resolve(state.settings);

            foreach (var group in state.uvGroups)
            {
                CheckCancel(state);
                if (group.textures.Any(t => t.whitelisted)) continue;

                float animScale = 1f;
                if (anim.maxScale.TryGetValue(group.renderer.transform, out var s)) animScale = s;

                group.scale = IslandScaler.ComputeGroupScale(group, q, state.settings, animScale);
                ATOLogger.InfoDetail($"group {group.id}: scale ({group.scale.x:F3}, {group.scale.y:F3})");
            }
        }

        // =====================================================================================
        // Stage 6 — packing
        // =====================================================================================

        private static void PackAtlases(ATOState state, nadena.dev.ndmf.BuildContext ctx)
        {
            if (!state.settings.generateAtlas)
            {
                ATOLogger.Info("atlas generation disabled; textures will be scaled directly.");
                state.fallbackGroups.AddRange(state.uvGroups.Where(g => !g.textures.Any(t => t.whitelisted)));
                return;
            }

            var packable = state.uvGroups.Where(g => !g.textures.Any(t => t.whitelisted)).ToList();
            var atlases = AtlasPacker.Pack(packable, state.settings, out var fallback);
            state.atlases.AddRange(atlases);
            state.fallbackGroups.AddRange(fallback);

            foreach (var atlas in atlases)
            {
                CheckCancel(state);
                AtlasCompositor.Compose(atlas, state.settings, ctx);
            }

            ATOLogger.Info($"packed {atlases.Count} atlas(es), {fallback.Count} group(s) fallback");
        }

        // =====================================================================================
        // Stage 7 — apply
        // =====================================================================================

        private static void ApplyResults(ATOState state, nadena.dev.ndmf.BuildContext ctx)
        {
            var groupsByRenderer = state.uvGroups
                .Where(g => g.placements.Count > 0)
                .GroupBy(g => g.renderer)
                .ToList();

            foreach (var rendererGroup in groupsByRenderer)
            {
                CheckCancel(state);
                var renderer = rendererGroup.Key;
                var original = GetSharedMesh(renderer);
                if (original == null) continue;

                var clone = Object.Instantiate(original);
                clone.name = original.name;
                ctx.AssetSaver.SaveAsset(clone);

                // AAO UV evacuation before rewriting / 改写前疏散 AAO 使用的 UV 通道
                if (renderer is SkinnedMeshRenderer smr)
                {
                    foreach (var channel in rendererGroup.Select(g => g.island.uvChannel).Distinct())
                    {
                        if (AAOCompatibility.IsTexCoordUsed(smr, channel))
                        {
                            int spare = FindSpareChannel(smr, channel);
                            if (spare >= 0)
                            {
                                var orig = IslandExtraction.GetUVs(original, channel);
                                IslandExtraction.SetUVs(clone, spare, (Vector2[])orig.Clone());
                                AAOCompatibility.RegisterTexCoordEvacuation(smr, channel, spare);
                                ATOLogger.Info($"evacuated UV{channel} -> UV{spare} on {renderer.name} for AAO compatibility");
                            }
                        }
                    }
                }

                foreach (var group in rendererGroup)
                    ApplyIslandUV(clone, group);

                if (renderer is SkinnedMeshRenderer s) s.sharedMesh = clone;
                else if (renderer is MeshRenderer m) m.GetComponent<MeshFilter>().sharedMesh = clone;
                nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(original, clone);
            }

            // assign atlas textures to materials / 将图集贴图赋给材质
            foreach (var atlas in state.atlases)
                foreach (var placement in atlas.islands)
                {
                    var entry = placement.source;
                    if (entry == null || entry.whitelisted) continue;
                    foreach (var r in entry.references)
                        if (r.material != null) r.material.SetTexture(r.propertyName, atlas.texture);
                }

            // fallback groups → scale whole textures directly / 回退组 → 直接缩放整张贴图
            ScaleWholeTextures(state, ctx);
        }

        private static void ApplyIslandUV(Mesh mesh, UvGroup group)
        {
            var placement = group.placements.Values.FirstOrDefault();
            if (placement == null) return;

            var island = group.island;
            var uv = IslandExtraction.GetUVs(mesh, island.uvChannel);
            if (uv == null) return;

            var dst = placement.dstRect;
            var indices = mesh.triangles;

            foreach (var tri in island.triangleIndices)
                for (int e = 0; e < 3; e++)
                {
                    int idx = tri * 3 + e;
                    if (idx >= indices.Length) continue;
                    int vi = indices[idx];
                    if (vi < 0 || vi >= uv.Length) continue;

                    var old = uv[vi];
                    float nx = (old.x - island.bounds.x) / Mathf.Max(0.0001f, island.bounds.width);
                    float ny = (old.y - island.bounds.y) / Mathf.Max(0.0001f, island.bounds.height);

                    Vector2 mapped;
                    switch (placement.rotation)
                    {
                        case 90: mapped = new Vector2(dst.x + (1f - ny) * dst.width, dst.y + nx * dst.height); break;
                        case 180: mapped = new Vector2(dst.x + (1f - nx) * dst.width, dst.y + (1f - ny) * dst.height); break;
                        case 270: mapped = new Vector2(dst.x + ny * dst.width, dst.y + (1f - nx) * dst.height); break;
                        default: mapped = new Vector2(dst.x + nx * dst.width, dst.y + ny * dst.height); break;
                    }
                    uv[vi] = mapped;
                }

            IslandExtraction.SetUVs(mesh, island.uvChannel, uv);
        }

        private static void ScaleWholeTextures(ATOState state, nadena.dev.ndmf.BuildContext ctx)
        {
            var q = IslandScaler.Resolve(state.settings);
            var seen = new HashSet<TextureEntry>();

            foreach (var group in state.fallbackGroups)
            {
                foreach (var tex in group.textures)
                {
                    if (tex.whitelisted || !seen.Add(tex)) continue;
                    var region = new Rect(0, 0, tex.width, tex.height);
                    float cutoff = 0.5f;
                    if (tex.references.Count > 0)
                    {
                        var (_, cutout, c) = ShaderAnalysis.GetRenderMode(tex.references[0].material);
                        if (cutout) cutoff = c;
                    }
                    var result = IslandScaler.ScaleSingleForWholeTexture(tex, region, q, cutoff);
                    if (result.skipped) continue;

                    var scaled = TextureOps.Scale(tex.readable ?? tex.texture, result.newWidth, result.newHeight);
                    var newTex = CreateTextureAsset($"ATO_{tex.DisplayName}", scaled, tex.isLinear, state, ctx);
                    nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(tex.texture, newTex);
                    foreach (var r in tex.references)
                        if (r.material != null) r.material.SetTexture(r.propertyName, newTex);
                    tex.texture = newTex;
                    ATOLogger.Info($"scaled whole texture {tex.DisplayName} -> {result.newWidth}x{result.newHeight}");
                }
            }
        }

        // =====================================================================================
        // Stage 8 — deduplication
        // =====================================================================================

        private static void Deduplicate(ATOState state, nadena.dev.ndmf.BuildContext ctx)
        {
            var materialMap = new Dictionary<string, Material>();
            var materialRemap = new Dictionary<Material, Material>();

            foreach (var renderer in state.renderers)
            {
                var materials = renderer.sharedMaterials;
                var newMats = new Material[materials.Length];
                bool changed = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    var m = materials[i];
                    if (m == null) { newMats[i] = null; continue; }
                    if (materialRemap.TryGetValue(m, out var existing)) { newMats[i] = existing; changed = true; continue; }

                    var sig = MaterialSignature(m);
                    if (materialMap.TryGetValue(sig, out var canonical) && CanMerge(state, renderer, i, m, canonical))
                    {
                        materialRemap[m] = canonical;
                        newMats[i] = canonical;
                        changed = true;
                        ATOLogger.InfoDetail($"material dedup: {m.name} -> {canonical.name}");
                    }
                    else
                    {
                        materialMap[sig] = m;
                        newMats[i] = m;
                    }
                }

                if (changed)
                {
                    renderer.sharedMaterials = newMats;
                    RemapAnimationObjectReferences(state, ctx, materialRemap.ToDictionary(kv => (UnityEngine.Object)kv.Key, kv => (UnityEngine.Object)kv.Value));
                }
            }

            // texture dedup / 贴图去重
            var texMap = new Dictionary<string, Texture2D>();
            var texRemap = new Dictionary<Texture2D, Texture2D>();
            foreach (var tex in state.textures)
            {
                if (tex == null || tex.texture == null) continue;
                var sig = tex.pixelHash + "|" + tex.importSignature;
                if (texMap.TryGetValue(sig, out var canonical)) texRemap[tex.texture] = canonical;
                else texMap[sig] = tex.texture;
            }
            if (texRemap.Count > 0)
                RemapAnimationObjectReferences(state, ctx, texRemap.ToDictionary(kv => (UnityEngine.Object)kv.Key, kv => (UnityEngine.Object)kv.Value));
        }

        /// <summary>
        /// Full material signature: shader, textures, colors, vectors, floats (via SerializedObject).
        /// 完整材质签名：shader、贴图、颜色、向量、浮点（经 SerializedObject）。
        /// </summary>
        private static string MaterialSignature(Material m)
        {
            var sb = new System.Text.StringBuilder(m.shader != null ? m.shader.name : "null");
            var so = new SerializedObject(m);

            AppendProps(so, "m_SavedProperties.m_TexEnvs", sb, (el) =>
            {
                var name = el.FindPropertyRelative("first").stringValue;
                var tex = el.FindPropertyRelative("second.m_Texture").objectReferenceValue;
                var scale = el.FindPropertyRelative("second.m_Scale").vector2Value;
                var offset = el.FindPropertyRelative("second.m_Offset").vector2Value;
                sb.Append('|').Append(name).Append('=').Append(tex != null ? tex.GetInstanceID() : 0)
                  .Append(',').Append(scale.x).Append(',').Append(scale.y).Append(',').Append(offset.x).Append(',').Append(offset.y);
            });

            AppendProps(so, "m_SavedProperties.m_Colors", sb, (el) =>
            {
                sb.Append('|').Append(el.FindPropertyRelative("first").stringValue).Append('=')
                  .Append(el.FindPropertyRelative("second").colorValue.ToString());
            });

            AppendProps(so, "m_SavedProperties.m_Floats", sb, (el) =>
            {
                sb.Append('|').Append(el.FindPropertyRelative("first").stringValue).Append('=')
                  .Append(el.FindPropertyRelative("second").floatValue);
            });

            AppendProps(so, "m_SavedProperties.m_Ints", sb, (el) =>
            {
                sb.Append('|').Append(el.FindPropertyRelative("first").stringValue).Append('=')
                  .Append(el.FindPropertyRelative("second").intValue);
            });

            sb.Append('|').Append(m.renderQueue);
            sb.Append('|').Append(string.Join(",", m.enabledKeywords.Select(k => k.name).OrderBy(n => n)));
            return sb.ToString();
        }

        private static void AppendProps(SerializedObject so, string path, System.Text.StringBuilder sb, Action<SerializedProperty> onEach)
        {
            var props = so.FindProperty(path);
            if (props == null || !props.isArray) return;
            for (int i = 0; i < props.arraySize; i++)
            {
                var el = props.GetArrayElementAtIndex(i);
                if (el != null) onEach(el);
            }
        }

        /// <summary>
        /// A material slot may be merged only if the animation does not switch it individually.
        /// 仅当动画未单独切换该材质槽时才允许合并。
        /// </summary>
        private static bool CanMerge(ATOState state, Renderer renderer, int slot, Material a, Material b)
        {
            if (a == b) return false;
            return !state.animatedMaterialSlots.Contains((renderer, slot));
        }

        private static void RemapAnimationObjectReferences(ATOState state, nadena.dev.ndmf.BuildContext ctx,
            Dictionary<UnityEngine.Object, UnityEngine.Object> map)
        {
            try
            {
                var animCtx = ctx.Extension<nadena.dev.ndmf.AnimatorServicesContext>();
                animCtx.AnimationIndex.RewriteObjectCurves(obj => map.TryGetValue(obj, out var n) ? n : obj);
                ATOLogger.Info($"rewrote {map.Count} object reference(s) in animations");
            }
            catch (Exception e)
            {
                ATOLogger.InfoDetail("animation object-reference rewrite skipped: " + e.Message);
            }
        }

        // =====================================================================================
        // Stage 9 — compression & mipstreaming
        // =====================================================================================

        private static void ApplyCompressionAndMip(ATOState state)
        {
            bool mip = state.settings.mipmaps;
            var allTextures = state.atlases.Select(a => a.texture)
                .Concat(state.textures.Select(t => t.texture))
                .Where(t => t != null)
                .Distinct()
                .ToList();

            foreach (var tex in allTextures)
            {
                var path = AssetDatabase.GetAssetPath(tex);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;
                importer.mipmapEnabled = mip;
                importer.streamingMipmaps = mip;   // bound to mipmap per VRChat requirement / 与 Mipmap 绑定（VRChat 要求）
                importer.isReadable = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }
            ATOLogger.Info($"mipmap + mipstreaming: {(mip ? "enabled" : "disabled")} on {allTextures.Count} texture(s)");
        }

        // =====================================================================================
        // helpers
        // =====================================================================================

        private static Mesh GetSharedMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (r is MeshRenderer mr)
            {
                var mf = mr.GetComponent<MeshFilter>();
                return mf != null ? mf.sharedMesh : null;
            }
            return null;
        }

        private static int FindSpareChannel(SkinnedMeshRenderer smr, int used)
        {
            for (int c = 7; c >= 0; c--)
                if (c != used && !AAOCompatibility.IsTexCoordUsed(smr, c)) return c;
            return -1;
        }

        private static List<TextureReference> GetReferencesForMaterial(ATOState state, Material mat)
        {
            var result = new List<TextureReference>();
            foreach (var t in state.textures)
                foreach (var r in t.references)
                    if (r.material == mat && !result.Contains(r)) result.Add(r);
            return result;
        }

        private static TextureEntry ResolveEntry(ATOState state, Texture2D tex)
        {
            var remap = RemapRegistry.Get(tex);
            if (remap != null) return remap;
            return state.textureEntries.TryGetValue(tex, out var e) ? e : null;
        }

        private static Texture2D CreateTextureAsset(string name, Texture2D src, bool linear, ATOState state, nadena.dev.ndmf.BuildContext ctx)
        {
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, linear);
            tex.name = name;
            tex.SetPixels32(src.GetPixels32());
            tex.Apply();
            ctx.AssetSaver.SaveAsset(tex);
            return tex;
        }

        private static void CheckCancel(ATOState state)
        {
            if (state.cancellation != null && state.cancellation.Token.IsCancellationRequested)
                throw new System.OperationCanceledException("[ATO] cancelled");
        }

        private static void Progress(string what, float p, ATOState state)
        {
            bool cancelled = EditorUtility.DisplayCancelableProgressBar("Avatar Texture Optimizer", what, p);
            if (cancelled) state.cancellation?.Cancel();
            CheckCancel(state);
        }
    }
}
