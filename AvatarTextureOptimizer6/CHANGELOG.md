# Changelog

## [0.1.0] - 2026-08-19

- Initial release (development build).
- NDMF pass integration (runs after Modular Avatar, before Avatar Optimizer).
- UV island extraction / merging / normalization / multi-UV channel support.
- Perceptual quality scaling (MS-SSIM / SSIM / CIEDE2000 / alpha IoU & RMSE / normal angle p95 / grayscale RMSE) with Burst + GPU batch evaluation.
- Texture type groups & UV groups; atlas bin packing with Burst raster masks (4px granularity, full-scan BLF, 90-degree rotation).
- Candidate atlas pool (POT by default, experimental NPOT).
- GPU pull-push atlas filling, per-category safe compression formats, Mipmap/MipStreaming binding.
- Platform overrides (PC / Android / iOS).
- AAO UVUsageCompabilityAPI compatibility (reflection based, works without AAO).
- lilToon & generic shader property/keyword analysis.
- Texture / material deduplication, material slot merging.
- i18n (English / Simplified Chinese, user-extensible JSON).
- Progress, cancellation, detailed `[ATO]` logging and build report.
