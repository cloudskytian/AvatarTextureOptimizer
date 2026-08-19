using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Collects whitelist entries, renderers, material textures; deduplicates textures by
    /// pixel content + import settings; and builds TextureEntry models. / 收集白名单、渲染器与材质贴图；
    /// 按像素内容 + 导入设置去重；构建 TextureEntry 模型。
    /// </summary>
    public static class TextureCollection
    {
        private const string TagEditorOnly = "EditorOnly";

        public static bool IsEditorOnly(GameObject go)
        {
            return go != null && go.CompareTag(TagEditorOnly);
        }

        /// <summary>Collect whitelisted objects/textures from all TextureWhitelist components. / 收集白名单对象与贴图。</summary>
        public static void CollectWhitelist(GameObject avatar, ATOState state)
        {
            var comps = avatar.GetComponentsInChildren<TextureWhitelist>(true);
            foreach (var wl in comps)
            {
                foreach (var obj in wl.objects)
                {
                    if (obj == null) continue;
                    state.whitelistedObjects.Add(obj);
                    CollectTexturesUnder(obj, state.whitelistedTextures);
                }
                if (wl.includeChildren)
                {
                    // whitelist all textures referenced by the subtree / 白名单化子树引用的全部贴图
                    var renderers = wl.GetComponentsInChildren<Renderer>(true);
                    foreach (var r in renderers)
                        foreach (var m in r.sharedMaterials)
                            if (m != null) CollectTexturesFromMaterial(m, state.whitelistedTextures);
                }
            }
            ATOLogger.Info($"whitelist: {state.whitelistedObjects.Count} objects, {state.whitelistedTextures.Count} textures");
        }

        private static void CollectTexturesUnder(UnityEngine.Object obj, HashSet<Texture2D> into)
        {
            switch (obj)
            {
                case Texture2D t: into.Add(t); break;
                case Material m: CollectTexturesFromMaterial(m, into); break;
                case Mesh mesh: break; // meshes reference no textures directly / 网格不直接引用贴图
                case GameObject go:
                    var renderers = go.GetComponentsInChildren<Renderer>(true);
                    foreach (var r in renderers)
                        foreach (var m in r.sharedMaterials)
                            if (m != null) CollectTexturesFromMaterial(m, into);
                    break;
                case AnimationClip clip:
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        foreach (var kf in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                        {
                            if (kf.value is Texture2D t2) into.Add(t2);
                            if (kf.value is Material m2) CollectTexturesFromMaterial(m2, into);
                        }
                    }
                    break;
            }
        }

        private static void CollectTexturesFromMaterial(Material m, HashSet<Texture2D> into)
        {
            if (m == null || m.shader == null) return;
            var so = new SerializedObject(m);
            var props = so.FindProperty("m_SavedProperties.m_TexEnvs");
            if (props == null) return;
            for (int i = 0; i < props.arraySize; i++)
            {
                var texRef = props.GetArrayElementAtIndex(i).FindPropertyRelative("second.m_Texture");
                if (texRef != null && texRef.objectReferenceValue is Texture2D t) into.Add(t);
            }
        }

        /// <summary>
        /// Collect renderers (enabled or potentially animation-enabled) and their material textures,
        /// plus animation-referenced materials/textures, dedup, and build the state texture table.
        /// 收集渲染器（启用或可能被动画启用）及其材质贴图，以及动画引用的材质/贴图，去重并构建状态贴图表。
        /// </summary>
        public static void Collect(GameObject avatar, ATOState state, AnimationAnalysis.Result anim)
        {
            var renderers = avatar.GetComponentsInChildren<Renderer>(true)
                .Where(r => (r is SkinnedMeshRenderer || r is MeshRenderer) && !IsEditorOnly(r.gameObject))
                .ToList();

            state.renderers.Clear();
            state.renderers.AddRange(renderers);

            // gather distinct textures + references / 收集不重复贴图及其引用
            var byTexture = new Dictionary<Texture2D, TextureEntry>();

            void AddReference(Texture2D tex, TextureReference reference)
            {
                if (tex == null) return;
                if (!byTexture.TryGetValue(tex, out var entry))
                {
                    entry = new TextureEntry { texture = tex };
                    byTexture[tex] = entry;
                }
                if (reference != null) entry.references.Add(reference);
            }

            foreach (var r in renderers)
            {
                var materials = r.sharedMaterials;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    var mat = materials[slot];
                    if (mat == null || mat.shader == null) continue;
                    foreach (var b in EnumerateTextureProperties(mat, slot, state))
                        AddReference(b.texture, b.reference);

                    // animation-switched materials on this slot / 该槽动画切入的材质
                    if (anim.materialSwitches.TryGetValue((r, slot), out var switched))
                        foreach (var sm in switched)
                            foreach (var b in EnumerateTextureProperties(sm, slot, state))
                                AddReference(b.texture, b.reference);
                }
            }

            // animation-referenced materials not bound to a renderer / 未绑定渲染器的动画材质
            foreach (var m in anim.animatedMaterials)
            {
                if (m == null || m.shader == null) continue;
                foreach (var b in EnumerateTextureProperties(m, 0, state))
                    AddReference(b.texture, b.reference);
            }

            // animation-referenced textures without material context / 无材质上下文的动画贴图
            foreach (var t in anim.animatedTextures)
                AddReference(t, null);

            // hash + classify + whitelist + dedup / 哈希 + 分类 + 白名单 + 去重
            var entries = byTexture.Values.ToList();
            var dedupMap = new Dictionary<(long, string), TextureEntry>();

            foreach (var e in entries)
            {
                var key = HashTexture(e.texture);
                e.width = key.width;
                e.height = key.height;
                e.pixelHash = key.hash;
                e.importSignature = ImportSignature(e.texture);
                e.isLinear = IsLinear(e.texture);
                e.filterMode = e.texture != null ? e.texture.filterMode : FilterMode.Bilinear;
                e.whitelisted = state.whitelistedTextures.Contains(e.texture) ||
                                state.whitelistedObjects.Contains(e.texture);

                if (e.references.Count > 0)
                {
                    var first = e.references[0];
                    e.category = ShaderAnalysis.Classify(e.texture, first.propertyName, first.material);
                    e.hasAlpha = ShaderAnalysis.HasAlpha(e.texture);
                }
                else
                {
                    // heuristic for context-less animated textures / 无上下文动画贴图的启发式分类
                    e.category = ClassifyByName(e.texture);
                    e.hasAlpha = ShaderAnalysis.HasAlpha(e.texture);
                }

                // normal encoding + gray channel mask / 法线编码 + 灰度通道掩码
                if (e.category == ATOTextureCategory.Normal)
                    e.normalEncoding = NormalEncoding(e.texture);
                if (e.category == ATOTextureCategory.Gray)
                    e.grayChannelMask = GrayChannelMask(e.references.Count > 0 ? e.references[0].propertyName : e.texture.name);

                var dedupKey = (e.pixelHash, e.importSignature);
                if (dedupMap.TryGetValue(dedupKey, out var canonical))
                {
                    if (e.whitelisted) canonical.whitelisted = true; // whitelist propagates / 白名单传播
                    foreach (var r2 in e.references) canonical.references.Add(r2);
                    RemapRegistry.Register(e.texture, canonical);
                    ATOLogger.InfoDetail($"dedup: {e.texture.name} -> {canonical.texture.name}");
                }
                else
                {
                    dedupMap[dedupKey] = e;
                }
            }

            state.textureEntries.Clear();
            foreach (var e in dedupMap.Values) state.textureEntries[e.texture] = e;
            state.textures.Clear();
            state.textures.AddRange(dedupMap.Values);

            // prepare CPU-readable copies for pixel operations / 为像素操作准备 CPU 可读副本
            foreach (var e in state.textures)
                if (e.texture != null) e.readable = ReadableCopy(e.texture);

            ATOLogger.Info($"collected {renderers.Count} renderers, {state.textures.Count} unique textures (after dedup)");
        }

        /// <summary>
        /// Return a CPU-readable copy of a texture (via RenderTexture readback if not readable).
        /// 返回贴图的 CPU 可读副本（不可读时经 RenderTexture 回读）。
        /// </summary>
        public static Texture2D ReadableCopy(Texture2D tex)
        {
            if (tex == null) return null;
            if (tex.isReadable) return tex;
            try
            {
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                Graphics.Blit(tex, rt);
                var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, !IsLinear(tex));
                copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                copy.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                return copy;
            }
            catch (System.Exception e)
            {
                ATOLogger.Warn($"failed to read pixels of {tex.name}: {e.Message}", tex);
                return tex;
            }
        }

        private static ATOTextureCategory ClassifyByName(Texture2D tex)
        {
            if (tex == null) return ATOTextureCategory.OpaqueColor;
            string n = tex.name.ToLowerInvariant();
            if (n.Contains("normal") || n.Contains("bump") || n.Contains("nrm")) return ATOTextureCategory.Normal;
            if (n.Contains("mask") || n.Contains("ao") || n.Contains("rough") || n.Contains("metal") ||
                n.Contains("smooth") || n.Contains("gloss")) return ATOTextureCategory.Gray;
            if (ShaderAnalysis.HasAlpha(tex)) return ATOTextureCategory.TransparentColor;
            return ATOTextureCategory.OpaqueColor;
        }

        /// <summary>
        /// Determine the normal map encoding from the importer's platform format.
        /// 依据导入器平台格式判定法线贴图编码。
        /// </summary>
        private static int NormalEncoding(Texture2D tex)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
            if (importer == null) return 0;
            var p = importer.GetPlatformTextureSettings("Standalone");
            switch (p.format)
            {
                case TextureImporterFormat.BC5: return 1;
                case TextureImporterFormat.BC7:
                case TextureImporterFormat.ASTC_4x4:
                case TextureImporterFormat.ASTC_5x5:
                case TextureImporterFormat.ASTC_6x6:
                case TextureImporterFormat.ASTC_8x8:
                case TextureImporterFormat.ASTC_10x10:
                case TextureImporterFormat.ASTC_12x12:
                case TextureImporterFormat.RGBA32:
                case TextureImporterFormat.RGBA16:
                    return 2;
                default: return 0; // DXT5/BC3 → DXT5nm / DXT5/BC3 → DXT5nm
            }
        }

        /// <summary>
        /// Infer which channels a gray/mask texture uses, from its property name.
        /// 依据属性名推断灰度/蒙版贴图使用的通道。
        /// </summary>
        private static int GrayChannelMask(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("metallic") || n.Contains("metalness") || n.Contains("roughness"))
                return 1 | 2;                       // metallic(R) + smoothness? conservative: R+G
            if (n.Contains("occlusion") || n.Contains("ao")) return 2;   // G
            if (n.Contains("smoothness") || n.Contains("gloss") || n.Contains("alpha")) return 8; // A
            if (n.Contains("mask") || n.Contains("emission")) return 1;  // R
            return 7; // default: RGB / 默认 RGB
        }

        private sealed class PropertyBinding
        {
            public Texture2D texture;
            public TextureReference reference;
        }

        /// <summary>
        /// Enumerate texture properties of a material that are eligible (identity ST, no decal usage).
        /// 枚举材质中符合条件的贴图属性（ST 为单位、非贴花等特殊用途）。
        /// </summary>
        private static List<PropertyBinding> EnumerateTextureProperties(Material mat, int slot, ATOState state)
        {
            var result = new List<PropertyBinding>();
            var so = new SerializedObject(mat);
            var texEnvs = so.FindProperty("m_SavedProperties.m_TexEnvs");
            if (texEnvs == null) return result;

            for (int i = 0; i < texEnvs.arraySize; i++)
            {
                var nameProp = texEnvs.GetArrayElementAtIndex(i).FindPropertyRelative("first");
                var texProp = texEnvs.GetArrayElementAtIndex(i).FindPropertyRelative("second.m_Texture");
                var scaleProp = texEnvs.GetArrayElementAtIndex(i).FindPropertyRelative("second.m_Scale");
                var offsetProp = texEnvs.GetArrayElementAtIndex(i).FindPropertyRelative("second.m_Offset");

                if (nameProp == null || texProp == null) continue;
                var tex = texProp.objectReferenceValue as Texture2D;
                if (tex == null) continue;

                string propName = nameProp.stringValue;

                // ST transform check: any scale/offset/rotation not identity => treat as whitelist / ST 变换检查
                Vector2 scale = scaleProp != null ? scaleProp.vector2Value : Vector2.one;
                Vector2 offset = offsetProp != null ? offsetProp.vector2Value : Vector2.zero;
                bool stIdentity = (scale == Vector2.one && offset == Vector2.zero);

                if (!stIdentity)
                {
                    state.whitelistedTextures.Add(tex);
                    ATOLogger.SkipWarning($"texture {tex.name} has non-identity ST transform on material {mat.name}.{propName}", mat);
                    continue;
                }

                // uv channel: default UV0; liltoon "2nd"/"3rd" maps use UV1/UV2; explicit "uvN" hints respected.
                // UV 通道：默认 UV0；liltoon 的 "2nd"/"3rd" 贴图用 UV1/UV2；显式 "uvN" 提示被尊重。
                int uvChannel = 0;
                string pl = propName.ToLowerInvariant();
                if (pl.Contains("uv3")) uvChannel = 3;
                else if (pl.Contains("uv2") || pl.Contains("3rd")) uvChannel = 2;
                else if (pl.Contains("uv1") || pl.Contains("2nd")) uvChannel = 1;

                var reference = new TextureReference
                {
                    material = mat,
                    propertyName = propName,
                    uvChannel = uvChannel,
                    st = new Vector4(scale.x, scale.y, offset.x, offset.y),
                    stIsIdentity = true,
                };

                result.Add(new PropertyBinding { texture = tex, reference = reference });
            }

            return result;
        }

        // ---- hashing & import signature ---- / 哈希与导入签名

        private static (int width, int height, long hash) HashTexture(Texture2D tex)
        {
            if (tex == null) return (0, 0, 0);
            var readable = ReadPixels(tex, out int w, out int h, out Color32[] pixels);
            if (!readable) return (tex.width, tex.height, 0);
            unchecked
            {
                long hash = 1469598103934665603L; // FNV-1a offset
                foreach (var c in pixels)
                {
                    hash ^= c.r; hash *= 1099511628211L;
                    hash ^= c.g; hash *= 1099511628211L;
                    hash ^= c.b; hash *= 1099511628211L;
                    hash ^= c.a; hash *= 1099511628211L;
                }
                return (w, h, hash);
            }
        }

        private static bool ReadPixels(Texture2D tex, out int w, out int h, out Color32[] pixels)
        {
            w = tex.width; h = tex.height; pixels = null;
            try
            {
                if (tex.isReadable)
                {
                    pixels = tex.GetPixels32();
                    return true;
                }

                // read back via RenderTexture / 通过 RenderTexture 回读
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                Graphics.Blit(tex, rt);
                var tmp = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                tmp.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                tmp.Apply();
                pixels = tmp.GetPixels32();
                UnityEngine.Object.DestroyImmediate(tmp);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                return true;
            }
            catch (Exception e)
            {
                ATOLogger.Warn($"failed to read pixels of {tex.name}: {e.Message}", tex);
                return false;
            }
        }

        private static string ImportSignature(Texture2D tex)
        {
            var importer = UnityEditor.AssetImporter.GetAtPath(UnityEditor.AssetDatabase.GetAssetPath(tex)) as UnityEditor.TextureImporter;
            if (importer == null) return "n/a";
            var p = importer.GetPlatformTextureSettings("Standalone");
            return $"{importer.textureType}|{importer.sRGBTexture}|{importer.mipmapEnabled}|{importer.wrapMode}|{importer.filterMode}|{importer.anisoLevel}|{importer.textureFormat}|{p.format}|{p.maxTextureSize}";
        }

        private static bool IsLinear(Texture2D tex)
        {
            var importer = UnityEditor.AssetImporter.GetAtPath(UnityEditor.AssetDatabase.GetAssetPath(tex)) as UnityEditor.TextureImporter;
            return importer != null && !importer.sRGBTexture;
        }
    }

    /// <summary>
    /// Maps duplicate textures to their canonical entry for later reference remapping. / 将重复贴图映射到规范项，供后续引用重映射。
    /// </summary>
    public static class RemapRegistry
    {
        private static readonly Dictionary<Texture2D, TextureEntry> Map = new Dictionary<Texture2D, TextureEntry>();

        public static void Register(Texture2D dup, TextureEntry canonical) => Map[dup] = canonical;
        public static TextureEntry Get(Texture2D dup) => Map.TryGetValue(dup, out var c) ? c : null;
        public static void Clear() => Map.Clear();
    }
}
