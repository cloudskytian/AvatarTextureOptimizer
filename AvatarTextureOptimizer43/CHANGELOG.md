# Changelog

## 0.1.2

- Persist generated atlases as PNG + TextureImporter when the NDMF container path is available, so mipmaps and Streaming Mipmaps are bound the way VRChat expects. Falls back to NDMF sub-asset + CompressTexture.
- Burst 4px shape raster (`AtoRasterJob.Run`) for islands with ≥16 triangles; CPU fallback on failure.
- Shader analyzer bake cache; lilToon `_Use*` skip actually applied.
- `AtoApi.AtlasCreated` fired after each committed atlas.
- GPU pull-push retried in Compose.

## 0.1.1

- Pixel LRU cache, GPU resample/pull-push with CPU fallback.
- Atlas builder: whitelist UV siblings, parallel sheets, submesh-aware stamping, secondary atlas downscale.
- Incoming dedup writes `texRemap`; rebind follows chains.
- Normal decode-resample-renormalize-encode.
- Opaque slot merge rewrites material-slot animation indices.

## 0.1.0

- Initial NDMF plugin (after MA, before AAO).
