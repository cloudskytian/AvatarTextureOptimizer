using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    internal sealed class AtlasBuildResult
    {
        public readonly Dictionary<PageLayerKey, Texture2D> BaseLayers = new Dictionary<PageLayerKey, Texture2D>();
        public readonly Dictionary<GroupMaterialLayerKey, Texture2D> MaterialVariants = new Dictionary<GroupMaterialLayerKey, Texture2D>();
        public readonly Dictionary<TextureBindingRecord, Texture2D> AnimatedTextureVariants = new Dictionary<TextureBindingRecord, Texture2D>();
        // A page may reference a transient texture deduplicated from an earlier successful page. Output references
        // therefore do not imply rollback ownership. Only textures created by this result belong here.
        // 页面可以引用前页的 transient 去重纹理；输出引用不等于回滚所有权，只有本结果新建对象才进入此集合。
        internal readonly HashSet<Texture2D> OwnedTextures = new HashSet<Texture2D>();
        public IEnumerable<Texture2D> AllTextures => BaseLayers.Values.Concat(MaterialVariants.Values)
            .Concat(AnimatedTextureVariants.Values).Where(value => value != null);
        public long OutputPixels;

        internal void DestroyOwnedTransient()
        {
            foreach (var texture in OwnedTextures.Where(value => value != null).Distinct().ToArray())
                if (!EditorUtility.IsPersistent(texture)) UnityEngine.Object.DestroyImmediate(texture);
            OwnedTextures.Clear();
        }

        public void DestroyTransient()
        {
            foreach (var texture in AllTextures.Concat(OwnedTextures).Where(value => value != null).Distinct().ToArray())
                if (!EditorUtility.IsPersistent(texture)) UnityEngine.Object.DestroyImmediate(texture);
            OwnedTextures.Clear();
        }
    }

    internal readonly struct PageLayerKey : IEquatable<PageLayerKey>
    {
        public readonly int Page, Layer;
        public PageLayerKey(int page, int layer) { Page = page; Layer = layer; }
        public bool Equals(PageLayerKey other) => Page == other.Page && Layer == other.Layer;
        public override bool Equals(object obj) => obj is PageLayerKey other && Equals(other);
        public override int GetHashCode() => (Page * 397) ^ Layer;
    }

    internal readonly struct GroupMaterialLayerKey : IEquatable<GroupMaterialLayerKey>
    {
        public readonly UvGroupRecord Group; public readonly Material Material; public readonly int Layer;
        public GroupMaterialLayerKey(UvGroupRecord group, Material material, int layer) { Group = group; Material = material; Layer = layer; }
        public bool Equals(GroupMaterialLayerKey other) => ReferenceEquals(Group, other.Group) && Material == other.Material && Layer == other.Layer;
        public override bool Equals(object obj) => obj is GroupMaterialLayerKey other && Equals(other);
        public override int GetHashCode() => ((Group.GetHashCode() * 397) ^ (Material == null ? 0 : Material.GetHashCode())) * 397 ^ Layer;
    }
}
