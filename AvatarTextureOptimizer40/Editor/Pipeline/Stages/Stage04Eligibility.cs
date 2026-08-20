using System.Collections.Generic;
using UnityEngine;

namespace Fosa.Ato.Editor.Pipeline.Stages
{
    /// <summary>
    /// Stage 04: Apply final eligibility rules and build type groups. A texture that is fully
    /// whitelisted is excluded from atlas/quality work (but a same-UV non-whitelist peer still gets
    /// whole-texture scaling). Type group signature = bitmask of present special-map kinds across
    /// the UV identity (normal/mask/emission) plus sRGB + filterMode.
    /// 阶段 04：应用最终合格规则并建立类型组。完全白名单贴图不参与图集/质量处理（但同 UV 的非白名单
    /// 贴图仍做整图缩放）。类型组签名 = 该 UV 身份上出现的特殊贴图种类位掩码（法线/蒙版/自发光）
    /// 加上 sRGB 与 filterMode。
    /// </summary>
    internal sealed class Stage04Eligibility : IStage
    {
        public string Name => "ATO/04 Applying eligibility & groups";
        public float Weight => 1f;

        public void Run(AtoPipeline p)
        {
            // Build type groups keyed by signature / 按签名建类型组
            var bySig = new Dictionary<int, TypeGroup>();
            int NormalBit = 1, MaskBit = 2, EmissionBit = 4, DataBit = 8;

            foreach (var u in p.Usages.Values)
            {
                if (u.Whitelisted)
                {
                    u.AtlasAllowed = false;
                    continue;
                }
                int sig = 0;
                if (u.Kind == TextureKind.Normal) sig |= NormalBit;
                if (u.Kind == TextureKind.Mask) sig |= MaskBit;
                if (u.Kind == TextureKind.Emission) sig |= EmissionBit;
                if (u.Kind == TextureKind.Data) sig |= DataBit;
                sig = CombineSig(sig, u.SRGB, u.Filter);

                if (!bySig.TryGetValue(sig, out var g))
                {
                    g = new TypeGroup { Signature = sig, SRGB = u.SRGB, Filter = u.Filter };
                    bySig[sig] = g; p.TypeGroups.Add(g);
                }
                if (u.Kind == TextureKind.Normal) g.HasNormal = true;
                if (u.Kind == TextureKind.Mask) g.HasMask = true;
                if (u.Kind == TextureKind.Emission) g.HasEmission = true;
                if (!g.Textures.Contains(u)) g.Textures.Add(u);
            }

            p.Progress.Stage(Name, 1f);
            AtoLog.VIf(p.Settings.VerboseLogging, $"Built {p.TypeGroups.Count} type group(s).");
        }

        private static int CombineSig(int sig, bool srgb, FilterMode f)
            => (sig << 4) ^ (srgb ? 0x8 : 0) ^ ((int)f & 0x7);
    }
}
