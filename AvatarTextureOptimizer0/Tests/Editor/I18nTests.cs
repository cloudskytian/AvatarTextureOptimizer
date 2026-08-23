using Fosa.AvatarTextureOptimizer.Editor.Inspector;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using NUnit.Framework;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class I18nTests
    {
        [Test]
        public void ExplicitLanguagesLoadTheirJsonTables()
        {
            Assert.AreEqual("Language", ATOI18n.Get(ATOLanguage.English, "field.language"));
            Assert.AreEqual("语言", ATOI18n.Get(ATOLanguage.SimplifiedChinese, "field.language"));
        }

        [Test]
        public void SuccessSummariesUseLocalizedAtlasAndWholeTextureTemplates()
        {
            Assert.AreEqual(
                "ATO complete: 12 islands, 3 atlas textures, 45.7% estimated texture-area saving in 890 ms.",
                ATOPipeline.FormatSummary(ATOLanguage.English, true, 12, 3, 45.67, 890));
            Assert.AreEqual(
                "ATO 完成（整图模式）：处理 12 个岛，生成 3 张优化贴图，预计贴图面积节省 45.7%，耗时 890 毫秒。",
                ATOPipeline.FormatSummary(ATOLanguage.SimplifiedChinese, false, 12, 3, 45.67, 890));
        }

        [Test]
        public void MissingKeyFallsBackToKeyWithoutThrowing()
        {
            Assert.AreEqual("missing.test.key", ATOI18n.Get(ATOLanguage.SimplifiedChinese, "missing.test.key"));
        }
    }
}
