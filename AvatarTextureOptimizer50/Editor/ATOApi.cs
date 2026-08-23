// -----------------------------------------------------------------------------
// ATOApi.cs — editor-side extension dispatch + third-party helpers.
// ATOApi.cs —— 编辑器侧扩展分发与第三方辅助。
// -----------------------------------------------------------------------------

using System;
using net.fosa.ato.editor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Editor-side API registry. Third parties subscribe via
    /// net.fosa.ato.ATOExtensionHost (runtime assembly) — this class dispatches.
    /// 编辑器侧 API 注册表。第三方经 runtime 的 ATOExtensionHost 订阅；本类负责分发。</summary>
    public static class ATOApi
    {
        internal static bool HasTextureFilters => net.fosa.ato.ATOExtensionHost.FilterCount > 0;

        /// <summary>Run user filters over one candidate. / 对候选贴图运行用户过滤器。</summary>
        internal static void RunTextureFilters(TexInfo info, ATOBuildState st)
        {
            var candidate = new TextureCandidate(info);
            foreach (var f in net.fosa.ato.ATOExtensionHost.TextureFilters)
            {
                try
                {
                    f?.Invoke(candidate);
                }
                catch (Exception e)
                {
                    ATOLog.Warn($"texture filter threw: {e.Message}");
                }
            }

            if (candidate.SkipRequested) info.MarkWhitelist(candidate.Reason ?? "3rd-party filter");
        }

        private sealed class TextureCandidate : net.fosa.ato.IATOTextureCandidate
        {
            private readonly TexInfo _info;
            public bool SkipRequested;
            public string Reason;

            public TextureCandidate(TexInfo info) { _info = info; }

            public Texture2D Texture => _info.source;
            public System.Collections.Generic.IReadOnlyList<Material> ReferencingMaterials
            {
                get
                {
                    var list = new System.Collections.Generic.List<Material>();
                    foreach (var kv in _info.usedByMaterials) list.Add(kv.Key);
                    return list;
                }
            }

            public string SkipReason => _info.whitelisted
                ? string.Join("; ", _info.whitelistReasons)
                : null;

            public void Skip(string reason)
            {
                SkipRequested = true;
                if (string.IsNullOrEmpty(Reason)) Reason = reason;
            }
        }

    }
}
