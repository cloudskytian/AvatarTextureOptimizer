namespace Fosa.AvatarTextureOptimizer.API
{
    /// <summary>
    /// Third-party / built-in shader analyser.
    /// Return false to let the next analyser try.
    /// 第三方或内置着色器分析器。返回 false 则交给下一个分析器。
    /// </summary>
    public interface IAtoShaderAnalyzer
    {
        /// <summary>Stable id used in logs. / 日志中的稳定标识。</summary>
        string Id { get; }

        /// <summary>Higher runs first. Built-ins use 0. / 越大越先执行。内置为 0。</summary>
        int Priority { get; }

        bool TryAnalyze(AtoShaderAnalyzeContext context, out AtoShaderAnalysis analysis);
    }
}
