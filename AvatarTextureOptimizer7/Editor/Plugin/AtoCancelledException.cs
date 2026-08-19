using System;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Thrown when the user cancels the bake / build. / 用户取消烘焙或构建时抛出。
    /// </summary>
    public sealed class AtoCancelledException : Exception
    {
        public AtoCancelledException() : base("ATO cancelled by user") { }
    }
}
