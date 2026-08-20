// IATOExtensionContext.cs
// Public API interfaces for third-party extension and customization.
// 供第三方扩展和自定义的公共 API 接口。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.API
{
    /// <summary>
    /// Provides hooks for advanced users and third-party developers to customize the pipeline.
    /// 为高级用户和第三方开发者提供自定义管线的钩子。
    /// </summary>
    public interface IATOExtensionPoint
    {
        /// <summary>Called before scanning begins. Return false to skip standard scanning. / 扫描开始前调用。返回 false 跳过标准扫描。</summary>
        bool OnPreScan(IATOPipelineContext context);

        /// <summary>Called after UV-to-texture mappings are built. / UV-贴图映射建立后调用。</summary>
        void OnMappingsBuilt(IATOPipelineContext context);

        /// <summary>Called before atlas packing begins. Return modified island list or null to use original. / 图集装箱前调用。</summary>
        List<IslandRef> OnPrePack(IATOPipelineContext context, List<IslandRef> islands);

        /// <summary>Called after atlas generation, before mesh rebaking. / 图集生成后、网格重烘前调用。</summary>
        void OnPostAtlas(IATOPipelineContext context);
    }

    /// <summary>Reference to a UV island in the pipeline. / 管线中 UV 岛的引用。</summary>
    public struct IslandRef
    {
        public int SourceTextureInstanceId;
        public int UVChannel;
        public Rect BoundingBox;
    }

    /// <summary>Read-only access to the pipeline context for extensions. / 扩展用只读管线上下文。</summary>
    public interface IATOPipelineContext
    {
        GameObject AvatarRoot { get; }
        QualityPreset CurrentPreset { get; }
        AdvancedSettings Settings { get; }
        bool GenerateAtlas { get; }
    }
}
