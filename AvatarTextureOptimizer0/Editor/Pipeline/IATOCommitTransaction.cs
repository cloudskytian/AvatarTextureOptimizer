using System;

namespace Fosa.AvatarTextureOptimizer.Editor.Pipeline
{
    /// <summary>
    /// Keeps Avatar mutations reversible until every post-commit pipeline step has succeeded. Implementations must
    /// make Complete non-throwing once the transaction has been returned from a successful Apply operation.
    /// 在全部提交后流水线步骤成功前保持 Avatar 改写可回滚；成功 Apply 并返回后，Complete 必须保证不抛异常。
    /// </summary>
    internal interface IATOCommitTransaction : IDisposable
    {
        void Complete();
        /// <summary>Returns true only when every Avatar/animation mutation was restored.</summary>
        bool Rollback();
    }

    /// <summary>
    /// Signals that Apply failed after a generated-object reference may have been written and at least one rollback
    /// operation also failed. External Texture/Mesh owners must retain their objects rather than create dangling refs.
    /// </summary>
    internal sealed class ATORollbackIncompleteException : Exception
    {
        public ATORollbackIncompleteException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
