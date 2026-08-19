namespace Fosa.AvatarTextureOptimizer.API
{
    /// <summary>
    /// Optional hook invoked after an atlas is produced. / 图集生成后的可选钩子。
    /// </summary>
    public interface IAtoAtlasHook
    {
        string Id { get; }

        void OnAtlasBuilt(string atlasName, UnityEngine.Texture2D atlas, int islandCount, float utilization);
    }
}
