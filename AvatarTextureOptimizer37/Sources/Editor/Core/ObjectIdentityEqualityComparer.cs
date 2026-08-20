// ============================================================================
// ATO - Unity Object identity comparer
// ATO - Unity Object 引用相等比较器
//
// Unity's Object overrides ==/Equals as identity comparison, but relying on
// implicit behavior is fragile; use an explicit comparer for hash sets.
// Unity 的 Object 重载了 ==/Equals 为引用比较，但依赖隐式行为并不稳妥；哈希
// 集合显式使用本比较器。
// ============================================================================

#region

using System.Collections.Generic;
using Object = UnityEngine.Object;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Core
{
    public sealed class ObjectIdentityEqualityComparer : IEqualityComparer<Object>
    {
        public static readonly ObjectIdentityEqualityComparer Instance = new();

        public int GetHashCode(Object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);

        // Unity's overloaded == is a null-safe identity comparison, which is
        // exactly the semantics we need. Unity 重载的 == 即空安全的引用比较，
        // 正是所需语义。
        public bool Equals(Object x, Object y) => x == y;
    }
}
