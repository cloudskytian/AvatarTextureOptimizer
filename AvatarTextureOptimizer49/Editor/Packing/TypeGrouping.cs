using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// A texture type group: all UV groups sharing the same "feature signature" — whether normal
    /// maps and/or linear aux (mask/grayscale) textures exist besides the primary color texture,
    /// plus the primary texture's color space and filter mode. Islands of a type group are packed
    /// together; each aux kind gets a mirrored atlas with identical layout (optionally uniformly
    /// downscaled when every instance has quality headroom). This solves the "10 textures atlased
    /// but only 1 has a normal map → 9/10 of the normal atlas wasted" problem.
    /// / 贴图类型组：签名相同的 UV 组集合（是否有法线/线性辅助贴图 + 主色 sRGB + filterMode）。
    /// 每种辅助类别生成同布局镜像图集（质量余量允许时可整体缩小），解决法线/蒙版图集空间浪费问题。
    /// </summary>
    internal class TypeGroup
    {
        internal string Key;
        internal readonly List<UvGroup> Groups = new List<UvGroup>();
        internal bool HasNormalAux;
        internal bool HasLinearAux; // mask / grayscale / linear color aux / 蒙版与灰度等线性辅助
        internal bool PrimarySrgb;
        internal FilterMode PrimaryFilter = FilterMode.Bilinear;

        private static bool IsPrimary(TexCategory c) =>
            c == TexCategory.Color || c == TexCategory.LinearColor;

        /// <summary>Build type groups from atlas-eligible UV groups. / 由可图集化的UV组构建类型组。</summary>
        internal static List<TypeGroup> Build(IEnumerable<UvGroup> groups, TextureStore store)
        {
            var dict = new Dictionary<string, TypeGroup>();
            foreach (var g in groups)
            {
                if (!g.atlasEligible || g.textures.Count == 0) continue;

                bool hasNormal = false, hasLinearAux = false, anyPrimary = false;
                foreach (var kv in g.textures)
                {
                    if (kv.Key == TexCategory.Normal) hasNormal = true;
                    else if (!IsPrimary(kv.Key)) hasLinearAux = true;
                    else anyPrimary = true;
                }

                var primary = g.textures.FirstOrDefault(kv => IsPrimary(kv.Key));
                var refTex = primary.Value != null ? primary.Value : g.textures.Keys.First();
                var info = store.GetImportInfo(refTex);

                var sb = new StringBuilder();
                sb.Append(hasNormal ? "N" : "-").Append(hasLinearAux ? "L" : "-")
                  .Append(anyPrimary ? (info.sRGB ? "S" : "s") : "x")
                  .Append('|').Append((int)refTex.filterMode);
                var key = sb.ToString();

                if (!dict.TryGetValue(key, out var tg))
                {
                    tg = new TypeGroup
                    {
                        Key = key,
                        HasNormalAux = hasNormal,
                        HasLinearAux = hasLinearAux,
                        PrimarySrgb = info.sRGB,
                        PrimaryFilter = refTex.filterMode,
                    };
                    dict[key] = tg;
                }
                tg.Groups.Add(g);
            }

            return dict.Values.ToList();
        }
    }
}
