using System;
using System.Collections.Generic;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>EN: Allocation-light disjoint set. ZH: 低分配并查集。</summary>
    internal sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;
        public UnionFind(int count) { _parent = new int[count]; _rank = new byte[count]; for (var i = 0; i < count; i++) _parent[i] = i; }
        public int Find(int value)
        {
            var root = value;
            while (_parent[root] != root) root = _parent[root];
            while (_parent[value] != value) { var next = _parent[value]; _parent[value] = root; value = next; }
            return root;
        }
        public void Union(int a, int b)
        {
            a = Find(a); b = Find(b); if (a == b) return;
            if (_rank[a] < _rank[b]) _parent[a] = b;
            else { _parent[b] = a; if (_rank[a] == _rank[b]) _rank[a]++; }
        }
        public Dictionary<int, List<int>> Groups()
        {
            var output = new Dictionary<int, List<int>>();
            for (var i = 0; i < _parent.Length; i++)
            {
                var root = Find(i);
                if (!output.TryGetValue(root, out var list)) output[root] = list = new List<int>();
                list.Add(i);
            }
            return output;
        }
    }
}
