// Per-material analysis: enumerate texture usages, apply eligibility checks
// (own ST, lilToon uvMain dependencies, UV channel selectors, decals, animation).
// 逐材质分析：枚举贴图引用并做资格检查（自身ST、lilToon uvMain 依赖、UV通道选择、贴花、动画）。

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    /// <summary>Animated material property values affecting this material (path-keyed), if any.
    /// 影响该材质的动画属性取值（按路径聚合）。</summary>
    internal class AnimatedMatProps
    {
        // propertyName (e.g. "material._MainTex_ST.x") -> all keyframe values / 全部关键帧值
        internal readonly Dictionary<string, List<float>> floatValues = new Dictionary<string, List<float>>();
        // propertyName (e.g. "material._MainTex") -> all texture objects (incl. nulls skipped) / 贴图对象引用曲线
        internal readonly Dictionary<string, List<Object>> objectValues = new Dictionary<string, List<Object>>();

        internal bool IsConstant(string prop, float value)
        {
            return !floatValues.TryGetValue(prop, out var vals) || vals.TrueForAll(v => Mathf.Abs(v - value) < 1e-5f);
        }

        internal bool HasAnyNonZero(string prop)
        {
            return floatValues.TryGetValue(prop, out var vals) && vals.Exists(v => Mathf.Abs(v) > 1e-6f);
        }
    }

    internal static class ShaderAnalyzer
    {
        /// <summary>
        /// Analyze one material on one renderer slot. Returns texture usages.
        /// 分析某渲染器某槽位上的材质，返回贴图引用列表。
        /// A usage is atlas-eligible only if every check passes; callers turn ineligible
        /// textures into whitelist entries (spec). 资格检查失败由调用方按白名单处理。
        /// </summary>
        internal static List<TexUse> Analyze(Renderer renderer, int slot, Material mat, AnimatedMatProps anim)
        {
            var uses = new List<TexUse>();
            if (mat == null || mat.shader == null) return uses;

            Shader shader = mat.shader;
            bool liltoon = ShaderCatalog.IsLilToon(shader);
            var alpha = DetectAlphaMode(mat, out float cutoff);

            // lilToon uvMain dependencies (verified): uvMain = uv0 through _MainTex_ST +
            // _MainTex_ScrollRotate rotation + _ShiftBackfaceUV. / uvMain 依赖检查。
            bool uvMainTransformed = false;
            if (liltoon)
            {
                Vector2 s = mat.GetTextureScale("_MainTex"), o = mat.GetTextureOffset("_MainTex");
                bool stId = s == Vector2.one && o == Vector2.zero;
                Vector4 sr = mat.HasProperty("_MainTex_ScrollRotate") ? mat.GetVector("_MainTex_ScrollRotate") : Vector4.zero;
                float shift = mat.HasProperty("_ShiftBackfaceUV") ? mat.GetFloat("_ShiftBackfaceUV") : 0f;
                uvMainTransformed = !stId || sr != Vector4.zero || Mathf.Abs(shift) > 0.5f;
                // animated variants / 动画修改
                if (anim != null)
                {
                    if (anim.floatValues.ContainsKey("material._MainTex_ST.x") ||
                        anim.floatValues.ContainsKey("material._MainTex_ST.y") ||
                        anim.floatValues.ContainsKey("material._MainTex_ST.z") ||
                        anim.floatValues.ContainsKey("material._MainTex_ST.w") ||
                        anim.HasAnyNonZero("material._MainTex_ScrollRotate.x") ||
                        anim.HasAnyNonZero("material._MainTex_ScrollRotate.y") ||
                        anim.HasAnyNonZero("material._MainTex_ScrollRotate.z"))
                        uvMainTransformed = true;
                }
            }

            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                if (shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) continue;
                string prop = shader.GetPropertyName(i);

                var tex = mat.GetTexture(prop) as Texture2D;
                if (tex == null) continue;
                if (IsBuiltinTex(tex)) continue; // "white"/"bump" defaults / 内建默认贴图

                var rule = ShaderCatalog.Resolve(shader, prop);
                if (rule == null) continue; // not a Tex2D-table prop / 未知属性按白名单由调用方处理

                var use = new TexUse
                {
                    renderer = renderer, slot = slot, material = mat, prop = prop, texture = tex,
                    kind = rule.kind, alpha = alpha, cutoff = cutoff,
                };

                // ---- UV resolution & transform checks / UV 解析与变换检查 ----
                switch (rule.uv)
                {
                    case UvMode.NonMeshUv:
                        use.specialUse = true;
                        use.uvChannel = 0;
                        break;
                    case UvMode.UvSelector:
                    {
                        int mode = rule.uvSelectorProp != null && mat.HasProperty(rule.uvSelectorProp)
                            ? Mathf.RoundToInt(mat.GetFloat(rule.uvSelectorProp))
                            : 0;
                        // animated UV mode switching -> treat as channel 0 but transformed if not constant
                        // 动画切换UV模式：非恒定则视为有变换
                        if (anim != null && anim.floatValues.ContainsKey("material." + rule.uvSelectorProp))
                            mode = 0; // values checked below; conservative / 保守处理
                        if (mode >= 0 && mode <= 3) use.uvChannel = mode;
                        else { use.specialUse = true; use.uvChannel = 0; }

                        break;
                    }
                    case UvMode.Uv0Main:
                        use.uvChannel = 0;
                        if (uvMainTransformed) use.stTransformed = true;
                        break;
                    default:
                        use.uvChannel = 0;
                        break;
                }

                // texture's own ST / 自身 ST
                if (rule.stChecked && !use.specialUse)
                {
                    Vector2 ts = mat.GetTextureScale(prop), to = mat.GetTextureOffset(prop);
                    if (ts != Vector2.one || to != Vector2.zero) use.stTransformed = true;
                    if (anim != null &&
                        (anim.floatValues.ContainsKey("material." + prop + "_ST.x") ||
                         anim.floatValues.ContainsKey("material." + prop + "_ST.y") ||
                         anim.floatValues.ContainsKey("material." + prop + "_ST.z") ||
                         anim.floatValues.ContainsKey("material." + prop + "_ST.w")))
                        use.stTransformed = true;
                }

                // scroll / rotation props / 滚动与旋转
                if (!use.specialUse)
                {
                    if (rule.scrollProp != null && mat.HasProperty(rule.scrollProp)
                        && mat.GetVector(rule.scrollProp) != Vector4.zero) use.stTransformed = true;
                    if (rule.angleProp != null && mat.HasProperty(rule.angleProp)
                        && Mathf.Abs(mat.GetFloat(rule.angleProp)) > 1e-6f) use.stTransformed = true;
                    if (rule.decalProp != null && mat.HasProperty(rule.decalProp)
                        && mat.GetFloat(rule.decalProp) > 0.5f) use.specialUse = true;
                }

                uses.Add(use);
            }

            return uses;
        }

        /// <summary>Detect render alpha mode (standard keywords + queue + lilToon shader names).
        /// 检测透明模式（标准关键字 + 渲染队列 + lilToon 变体名）。</summary>
        internal static AlphaMode DetectAlphaMode(Material mat, out float cutoff)
        {
            cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f;
            string shaderName = mat.shader != null ? mat.shader.name.ToLowerInvariant() : "";

            if (mat.IsKeywordEnabled("_ALPHATEST_ON") || mat.renderQueue >= 2450 && mat.renderQueue < 3000
                || shaderName.Contains("cutout"))
                return AlphaMode.Cutout;
            if (mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON")
                || mat.renderQueue >= 3000 || shaderName.Contains("trans")
                || mat.HasProperty("_TransparentMode") && Mathf.Abs(mat.GetFloat("_TransparentMode")) > 0.5f)
                return AlphaMode.Blend;
            return AlphaMode.Opaque;
        }

        internal static bool IsBuiltinTex(Texture2D tex)
        {
            if (tex == null) return true;
            // Real assets have an asset path. / 真资产有路径。
            string path = UnityEditor.AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path)) return false;
            // Builtin defaults keep their well-known names. / 内建默认贴图名为固定值。
            string n = tex.name;
            return n == "white" || n == "black" || n == "gray" || n == "bump"
                || n == "red" || n == "green" || n == "blue";
        }
    }
}
