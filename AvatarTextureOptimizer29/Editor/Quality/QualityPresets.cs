// Preset -> threshold mapping (rationale in docs/QualityPresets.md).
// 挡位到阈值的映射（依据见 docs/QualityPresets.md）。

using net.fosa.ato;

namespace net.fosa.ato.editor
{
    internal static class QualityPresets
    {
        internal static AtoQualityParams For(AtoQualityPreset p)
        {
            switch (p)
            {
                case AtoQualityPreset.NearLossless:
                    return new AtoQualityParams
                    {
                        msssimMin = 0.999f, deltaEMeanMax = 0.5f, deltaEP95Max = 1.5f,
                        normalAngleMeanMax = 0.5f, normalAngleP95Max = 2f,
                        alphaCutoutIoUMin = 0.999f, alphaBlendRmseMax = 1.5f / 255f,
                        grayRmseMax = 1f / 255f,
                    };
                case AtoQualityPreset.High:
                    return new AtoQualityParams
                    {
                        msssimMin = 0.99f, deltaEMeanMax = 1.0f, deltaEP95Max = 2.5f,
                        normalAngleMeanMax = 1f, normalAngleP95Max = 3f,
                        alphaCutoutIoUMin = 0.998f, alphaBlendRmseMax = 2f / 255f,
                        grayRmseMax = 1.5f / 255f,
                    };
                case AtoQualityPreset.Fast:
                    return new AtoQualityParams
                    {
                        msssimMin = 0.96f, deltaEMeanMax = 2.5f, deltaEP95Max = 5f,
                        normalAngleMeanMax = 2.5f, normalAngleP95Max = 6f,
                        alphaCutoutIoUMin = 0.99f, alphaBlendRmseMax = 5f / 255f,
                        grayRmseMax = 4f / 255f,
                    };
                case AtoQualityPreset.Balanced:
                default:
                    return new AtoQualityParams
                    {
                        msssimMin = 0.98f, deltaEMeanMax = 1.5f, deltaEP95Max = 3.5f,
                        normalAngleMeanMax = 1.5f, normalAngleP95Max = 4f,
                        alphaCutoutIoUMin = 0.995f, alphaBlendRmseMax = 3f / 255f,
                        grayRmseMax = 2.5f / 255f,
                    };
            }
        }

        /// <summary>Effective params for the session. / 会话生效参数。</summary>
        internal static AtoQualityParams Effective(AtoSession s)
        {
            return s.settings.preset == AtoQualityPreset.Custom
                ? s.settings.custom
                : For(s.settings.preset);
        }

        /// <summary>
        /// "Target quality == 1" semantics: NearLossless preset (or Custom equal to it)
        /// skips UV scaling entirely and copies pixels as-is (spec).
        /// 目标质量为1：跳过UV缩放，原样拷贝（需求书）。
        /// </summary>
        internal static bool IsQualityOne(AtoSession s)
        {
            var nl = For(AtoQualityPreset.NearLossless);
            if (s.settings.preset == AtoQualityPreset.NearLossless) return true;
            return s.settings.preset == AtoQualityPreset.Custom
                && AtoQualityParams.NearEquals(s.settings.custom, nl);
        }
    }
}
