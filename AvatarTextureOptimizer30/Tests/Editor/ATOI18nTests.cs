// ATOI18nTests.cs — i18n 解析单元测试 / i18n parser unit tests.

using System.Collections.Generic;
using NUnit.Framework;

namespace Fosa.AvatarTextureOptimizer
{
    [TestFixture]
    public class ATOI18nTests
    {
        [Test]
        public void ParseJsonObject_Basic()
        {
            var into = new Dictionary<string, string>();
            ATOI18n.ParseJsonObject("{ \"a\": \"hello\", \"b\": \"world\" }", into);
            Assert.AreEqual("hello", into["a"]);
            Assert.AreEqual("world", into["b"]);
        }

        [Test]
        public void ParseJsonObject_Escapes()
        {
            var into = new Dictionary<string, string>();
            ATOI18n.ParseJsonObject("{ \"k\": \"line1\\nline2 with \\\"quotes\\\"\" }", into);
            Assert.AreEqual("line1\nline2 with \"quotes\"", into["k"]);
        }

        [Test]
        public void ParseJsonObject_SkipsNestedObjects()
        {
            var into = new Dictionary<string, string>();
            ATOI18n.ParseJsonObject("{ \"keep\": \"v\", \"nested\": { \"x\": 1 }, \"arr\": [1, 2, 3], \"tail\": \"t\" }", into);
            Assert.AreEqual("v", into["keep"]);
            Assert.AreEqual("t", into["tail"]);
            Assert.AreEqual(2, into.Count);
        }

        [Test]
        public void ParseJsonObject_HandlesChinese()
        {
            var into = new Dictionary<string, string>();
            ATOI18n.ParseJsonObject("{ \"k\": \"生成图集\" }", into);
            Assert.AreEqual("生成图集", into["k"]);
        }
    }
}
