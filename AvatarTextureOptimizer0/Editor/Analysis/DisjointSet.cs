namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    internal sealed class DisjointSet
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;
        public DisjointSet(int count) { _parent = new int[count]; _rank = new byte[count]; for (var i = 0; i < count; i++) _parent[i] = i; }
        public int Find(int value) { while (_parent[value] != value) { _parent[value] = _parent[_parent[value]]; value = _parent[value]; } return value; }
        public void Union(int a, int b)
        {
            a = Find(a); b = Find(b); if (a == b) return;
            if (_rank[a] < _rank[b]) { var swap = a; a = b; b = swap; }
            _parent[b] = a; if (_rank[a] == _rank[b]) _rank[a]++;
        }
    }
}
