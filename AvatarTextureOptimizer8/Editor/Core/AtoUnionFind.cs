// AtoUnionFind.cs
// Tiny union-find used by island segmentation and overlap merging.
// 岛分割与重叠合并使用的小型并查集。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System.Collections.Generic;

namespace net.fosa.ato
{
    internal sealed class AtoUnionFind
    {
        private readonly List<int> _parent = new List<int>();

        internal int Add()
        {
            _parent.Add(_parent.Count);
            return _parent.Count - 1;
        }

        internal int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        internal void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a != b) _parent[a] = b;
        }

        internal int Count => _parent.Count;
    }
}
