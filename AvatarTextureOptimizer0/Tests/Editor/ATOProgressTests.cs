using System;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using NUnit.Framework;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class ATOProgressTests
    {
        [TearDown]
        public void TearDown()
        {
            ATOProgress.End();
        }

        [Test]
        public void Checkpoint_ThrowsWhenInjectedProbeCancels()
        {
            ATOProgress.Begin(() => true);

            Assert.Throws<OperationCanceledException>(() => ATOProgress.Checkpoint("test cancellation"));
        }

        [Test]
        public void End_DeactivatesInjectedProbe()
        {
            var calls = 0;
            ATOProgress.Begin(() => { calls++; return false; });
            ATOProgress.Checkpoint("active");
            Assert.AreEqual(1, calls);

            ATOProgress.End();
            ATOProgress.Checkpoint("inactive");
            Assert.AreEqual(1, calls);
        }

        [Test]
        public void Begin_RejectsNestedProgressScope()
        {
            ATOProgress.Begin(() => false);

            Assert.Throws<InvalidOperationException>(() => ATOProgress.Begin(() => false));
        }
    }
}
