# ATO extension API

Namespace: `Fosa.ATO.Editor`

## Shader analyzer

```csharp
using Fosa.ATO.Editor;
using UnityEngine;

public class MyAnalyzer : IAtoShaderAnalyzer
{
    public AtoShaderInfo Analyze(Material material)
    {
        if (material == null || material.shader == null) return null; // decline
        if (!material.shader.name.Contains("MyFork")) return null;
        var info = new AtoShaderInfo { AlphaMode = AtoAlphaMode.Cutout, Cutoff = 0.5f };
        info.Slots.Add(new AtoShaderSlot {
            PropertyName = "_MainTex", UvChannel = 0, Class = AtoTextureClass.Opaque
        });
        return info; // first non-null extra analyzer wins after built-in lilToon/standard
    }
}

[UnityEditor.InitializeOnLoad]
static class Register {
    static Register() => AtoApi.RegisterShaderAnalyzer(new MyAnalyzer());
}
```

## Bake events

- `AtoApi.BeforeAnalyze` / `AfterAnalyze` / `BeforeApply` / `AfterApply`
- `AtoApi.AtlasCreated` — `(AtoBakeContext, Texture2D)` after each committed atlas

`AtoBakeContext` exposes avatar root, component, resolved settings, report, texture refs.

Do not write shader parameters other than texture assignments if you want ATO’s safety guarantees.

## i18n

Drop `Localization/<bcp47>.json` flat objects. NDMF error keys also need `:description` and `:hint`.
