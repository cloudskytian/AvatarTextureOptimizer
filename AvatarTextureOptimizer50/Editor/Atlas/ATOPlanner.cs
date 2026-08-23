// -----------------------------------------------------------------------------
// ATOPlanner.cs — island setup, eligibility, scale decisions, type groups & pack units.
// ATOPlanner.cs —— 岛初始化、资格判定、缩放决策、类型组与装箱单元。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOPlanner
    {
        /// <summary>Islands + eligibility + sampled textures + variants.
        /// 岛、资格、采样贴图与变体。</summary>
        public static void Plan(ATOBuildState st)
        {
            int wrapWarnCount = 0;

            foreach (var g in st.uvGroups)
            {
                // ---- eligibility: whitelist contamination / 白名单传染 ----
                foreach (var t in g.textures)
                    if (t.whitelisted)
                        g.MarkIneligible($"whitelisted texture '{t.source.name}' / 含白名单贴图");

                if (!st.settings.generateAtlas)
                    g.MarkIneligible("atlas generation disabled / 未启用图集生成");

                // ---- islands / 岛 ----
                g.islands.AddRange(ATOIslands.Extract(g.owner, g.channel, st));
                var wrapWarnings = new List<string>();
                foreach (var isl in g.islands)
                {
                    isl.group = g;
                    ATOIslands.Analyze(isl, g.owner, g.channel, wrapWarnings);
                }

                if (wrapWarnings.Count > 0)
                {
                    wrapWarnCount += wrapWarnings.Count;
                    foreach (var w in wrapWarnings.Take(4)) st.report.AddWarning(w);

                    // whitelist every texture sampled through this group, group falls back
                    // 该组采样的所有贴图白名单化，整组回退整图缩放
                    g.MarkIneligible("wrap-seam crossing islands / 存在跨wrap缝的岛");
                }

                ATOIslands.MergeOverlaps(g.islands);

                // ---- sampled textures per island / 每岛采样贴图 ----
                AssignSampledTextures(g, st);
            }

            if (wrapWarnCount > 0)
                ATOLog.Warn($"wrap normalization warnings: {wrapWarnCount}");

            // ---- classify textures / 贴图分类 ----
            ClassifyTextures(st);

            // ---- whole-texture path for ineligible groups & textures / 非图集路径 ----
            if (!st.settings.generateAtlas)
            {
                foreach (var t in st.textures.Where(t => !t.whitelisted))
                    ATOWholeScale.Process(t, st);
                return;
            }

            // ---- decide island scales / 决定岛缩放 ----
            int i = 0, total = st.uvGroups.Count;
            foreach (var g in st.uvGroups)
            {
                if (!g.eligibleForAtlas) continue;
                foreach (var isl in g.islands)
                    if (isl.sampledTextures.Count > 0)
                        ATOQuality.DecideIslandScale(isl, st);
                st.progress?.Report(0.5f + 0.5f * (++i / (float)Mathf.Max(1, total)),
                    $"quality {i}/{total}");
            }

            // ---- build pack units by type group / 按类型组建装箱单元 ----
            var groups = new Dictionary<TypeGroupKey, List<PackUnit>>();
            foreach (var g in st.uvGroups)
            {
                if (!g.eligibleForAtlas) continue;

                // base textures = Main-role textures of rest materials on this group
                // 基础贴图 = 本组各槽静态材质的主色
                var slotBases = new Dictionary<int, TexInfo>();
                var slotVariants = new Dictionary<int, List<TexInfo>>();
                for (int slot = 0; slot < g.owner.slotMaterials.Count; slot++)
                {
                    var restMat = slot < g.owner.initialMaterial.Count ? g.owner.initialMaterial[slot] : null;
                    if (restMat == null || !st.materialAnalysis.TryGetValue(restMat, out var ra))
                        continue;

                    var mainUse = ra.uses.FirstOrDefault(u =>
                        u.role == TexRole.Main && u.uvChannel == g.channel && u.texture != null);
                    if (mainUse == null) continue;
                    var baseInfo = st.GetOrCreateTex(mainUse.texture);
                    if (baseInfo.whitelisted) continue;
                    slotBases[slot] = baseInfo;

                    var vars = new List<TexInfo>();
                    foreach (var m2 in g.owner.slotMaterials[slot])
                    {
                        if (m2 == null || m2 == restMat) continue;
                        if (!st.materialAnalysis.TryGetValue(m2, out var ra2)) continue;
                        var mu2 = ra2.uses.FirstOrDefault(u =>
                            u.role == TexRole.Main && u.uvChannel == g.channel && u.texture != null);
                        if (mu2 == null) continue;
                        var vi = st.GetOrCreateTex(mu2.texture);
                        if (!vi.whitelisted && vi != baseInfo && !vars.Contains(vi)) vars.Add(vi);
                    }

                    if (vars.Count > 0) slotVariants[slot] = vars;
                }

                var unitsOfBase = new Dictionary<TexInfo, PackUnit>();
                foreach (var isl in g.islands)
                {
                    if (isl.sampledTextures.Count == 0) continue;
                    // primary slot of the island = its first triangle's submesh / 主槽=首个三角形子网格
                    int slot = isl.triangles.Count > 0 ? isl.triangles[0].subMesh : -1;
                    if (slot < 0 || !slotBases.TryGetValue(slot, out var baseTex)) continue;

                    if (!unitsOfBase.TryGetValue(baseTex, out var unit))
                    {
                        unit = new PackUnit { baseTex = baseTex };
                        unitsOfBase[baseTex] = unit;
                    }

                    if (!unit.islands.Contains(isl)) unit.islands.Add(isl);
                    if (slotVariants.TryGetValue(slot, out var vars))
                        foreach (var v in vars)
                            if (!isl.VariantTexs().Contains(v)) isl.VariantTexs().Add(v);
                }

                foreach (var (baseTex, unit) in unitsOfBase)
                {
                    // type signature / 类型签名
                    bool hasN = unit.islands.Any(x => x.sampledTextures.Any(t => t.role == TexRole.Normal));
                    bool hasG = unit.islands.Any(x => x.sampledTextures.Any(t => t.role == TexRole.Gray));
                    bool hasE = unit.islands.Any(x => x.sampledTextures.Any(t => t.role == TexRole.ExtraColor));
                    var key = new TypeGroupKey(hasN, hasG, hasE, baseTex.IsSRGB,
                        baseTex.importSnap?.filterMode ?? FilterMode.Bilinear);

                    unit.typeKey = key;
                    foreach (var isl in unit.islands) isl.unitBase = baseTex;
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<PackUnit>();
                    list.Add(unit);

                    baseTex.atlasified = true;
                }

                g.atlasified = unitsOfBase.Count > 0;
                if (unitsOfBase.Count == 0)
                    g.MarkIneligible("no packable base textures / 无可装箱主色");
            }

            // ---- pack per type group / 逐类型组装箱 ----
            st.atlases.Clear();
            foreach (var (key, units) in groups)
            {
                var results = ATOPacker.PackTypeGroup(units, key, st.settings, st);
                foreach (var r in results) r.typeKey = key;
                st.atlases.AddRange(results);
            }

            // renumber ids & fix island references / 重编图集号并修正岛引用
            var idMap = new Dictionary<int, int>();
            for (int idx = 0; idx < st.atlases.Count; idx++)
            {
                var old = st.atlases[idx].id;
                st.atlases[idx].id = idx;
                idMap[old] = idx;
            }

            foreach (var g in st.uvGroups)
            foreach (var isl in g.islands.Concat(g.islands.SelectMany(x => x.mergedDuplicates)))
                if (idMap.TryGetValue(isl.atlasId, out var newId)) isl.atlasId = newId;

            st.report.atlasCount = st.atlases.Count;
            st.report.islandCount = st.uvGroups.Sum(g => g.islands.Count);
            st.report.pureColorIslandCount = st.uvGroups.Sum(g => g.islands.Count(x => x.pureColor));
            st.report.losslessIslandCount = st.uvGroups.Sum(g => g.islands.Count(x => x.losslessCopy));

            // ---- whole-scale fallback for non-atlased groups / 非图集组整图缩放 ----
            var processed = new HashSet<TexInfo>();
            foreach (var g in st.uvGroups)
            {
                if (g.eligibleForAtlas && g.atlasified) continue;
                foreach (var t in g.textures)
                {
                    // Textures also atlased elsewhere keep their atlas version; others get a
                    // whole-scaled copy for this group's original UV layout.
                    // 已在其他组图集化的贴图保留图集版；其余为本组原始 UV 布局生成整图缩放副本。
                    if (t.whitelisted || processed.Contains(t)) continue;
                    processed.Add(t);
                    ATOWholeScale.Process(t, st);
                }
            }

            foreach (var t in st.textures)
                if (!t.whitelisted && !t.atlasified && !processed.Contains(t) && t.usages.Count > 0)
                    ATOWholeScale.Process(t, st);
        }

        // ================================================================= //

        private static void AssignSampledTextures(UvGroupInfo g, ATOBuildState st)
        {
            foreach (var isl in g.islands)
            {
                var slots = isl.triangles.Select(t => t.subMesh).Distinct();
                foreach (var slot in slots)
                {
                    if (slot < 0 || slot >= g.owner.slotMaterials.Count) continue;
                    foreach (var m in g.owner.slotMaterials[slot])
                    {
                        if (m == null) continue;
                        if (!st.materialAnalysis.TryGetValue(m, out var ra)) continue;

                        foreach (var u in ra.uses)
                        {
                            if (u.texture == null || u.transformed || u.uvChannel != g.channel) continue;
                            var info = st.GetOrCreateTex(u.texture);
                            if (info.whitelisted) continue;
                            if (!isl.sampledTextures.Any(x => x.tex == info))
                                isl.sampledTextures.Add((info, u.role));
                        }
                    }
                }
            }
        }

        /// <summary>Classify every texture by class (content + role + alpha usage).
        /// 按类别（内容+角色+alpha 用途）对每张贴图分类。</summary>
        private static void ClassifyTextures(ATOBuildState st)
        {
            foreach (var t in st.textures)
            {
                bool anyNormal = t.usages.Any(u => u.role == TexRole.Normal);
                bool anyGray = t.usages.Any(u => u.role == TexRole.Gray);
                bool alphaUsed = t.alphaUsage.Count > 0;

                if (anyNormal) t.texClass = TexClass.NormalMap;
                else if (anyGray) t.texClass = TexClass.GrayMask;
                else
                {
                    // content alpha detection / 内容 alpha 检测
                    var buf = ATOQuality.GetBuffer(t, st);
                    bool hasAlpha = alphaUsed;
                    if (buf != null && !hasAlpha)
                    {
                        int step = Mathf.Max(1, buf.pixels.Length / 16384);
                        for (int i = 0; i < buf.pixels.Length; i += step)
                            if (buf.pixels[i].a < 250) { hasAlpha = true; break; }
                    }

                    t.alphaContent = hasAlpha;
                    t.texClass = hasAlpha ? TexClass.AlbedoAlpha : TexClass.AlbedoOpaque;
                }

                // extensions / 扩展钩子
                if (ATOApi.HasTextureFilters && !t.whitelisted)
                    ATOApi.RunTextureFilters(t, st);
            }

            st.report.textureCount = st.textures.Count;
            st.report.whitelistedTextureCount = st.textures.Count(x => x.whitelisted);
        }
    }
}
