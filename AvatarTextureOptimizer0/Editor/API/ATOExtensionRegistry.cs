using System;
using System.Collections.Generic;
using System.Linq;

namespace Fosa.AvatarTextureOptimizer.Editor.API
{
    public static class ATOExtensionRegistry
    {
        private static readonly List<IATOExtension> Extensions = new List<IATOExtension>();
        public static IReadOnlyList<IATOExtension> Registered => Extensions.AsReadOnly();

        public static void Register(IATOExtension extension)
        {
            if (extension == null) throw new ArgumentNullException(nameof(extension));
            if (Extensions.Contains(extension)) return;
            Extensions.Add(extension);
            // LINQ OrderBy is stable: equal-priority extensions retain registration order instead of depending on
            // List.Sort's unspecified tie ordering. / 同优先级扩展严格保持注册顺序。
            var ordered = Extensions.OrderBy(value => value.Priority).ToArray();
            Extensions.Clear(); Extensions.AddRange(ordered);
        }

        public static void Unregister(IATOExtension extension) => Extensions.Remove(extension);
        internal static IATOExtension[] Snapshot() => Extensions.Where(x => x != null).ToArray();
    }
}
