# Extension API / 扩展接口

Register from `[InitializeOnLoad]`:

```csharp
using Fosa.AvatarTextureOptimizer.API;
using UnityEditor;

[InitializeOnLoad]
static class MyAtoHook
{
    static MyAtoHook()
    {
        AtoExtensions.RegisterShaderAnalyzer(new MyShaderAnalyzer());
        AtoExtensions.RegisterQualityHook(new MyQualityHook());
        AtoExtensions.RegisterAtlasHook(new MyAtlasHook());
    }
}
```

`IAtoShaderAnalyzer.TryAnalyze` must return `false` to fall through to built-ins.
Return `true` with `SkipReason != None` to whitelist a material safely.

`IAtoQualityHook.Accept` can veto a candidate scale.

`IAtoAtlasHook.OnAtlasBuilt` is notified after each atlas is saved.

Never mutate shader parameters other than Texture2D references.
