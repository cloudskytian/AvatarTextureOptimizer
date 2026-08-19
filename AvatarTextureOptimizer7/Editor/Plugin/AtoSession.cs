using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Per-build working set. Holds caches and is disposed on success, failure or cancel.
    /// 单次构建的工作集。成功、失败或取消时释放缓存。
    /// </summary>
    public sealed class AtoSession : IDisposable
    {
        public BuildContext Context;
        public AvatarTextureOptimizer Component;
        public AtoLog Log = new AtoLog();
        public AtoLanguageMode Language;
        public AtoPlatform Platform;
        public AtoPlatformSettings PlatformSettings;
        public AtoQualityThresholds Quality;
        public bool GenerateAtlas;
        public bool Lossless;
        public int MinPadding;
        public int MinAtlas = 64;
        public int MaxAtlas = 8192;
        public bool Npot;
        public float MinPxPerMeter;
        public float MaxPxPerMeter;

        public AnimatorServicesContext Animators;

        public readonly List<Object> TempObjects = new List<Object>();
        public readonly TextureDecodeCache DecodeCache = new TextureDecodeCache();
        public readonly Dictionary<Texture, Texture> TextureRemap = new Dictionary<Texture, Texture>();
        public readonly Dictionary<Material, Material> MaterialRemap = new Dictionary<Material, Material>();
        public readonly HashSet<Texture2D> WhitelistTextures = new HashSet<Texture2D>();
        public readonly HashSet<Object> WhitelistObjects = new HashSet<Object>();
        public readonly List<string> Warnings = new List<string>();
        public readonly AtoBuildReport Report = new AtoBuildReport();

        bool _disposed;
        float _progress;
        string _phase = "";

        public string T(string key) => AtoLoc.T(Language, key);
        public string T(string key, params object[] args) => AtoLoc.T(Language, key, args);

        public void SetProgress(string locKey, float t)
        {
            _phase = T(locKey);
            _progress = Mathf.Clamp01(t);
            if (EditorUtility.DisplayCancelableProgressBar("[ATO] " + _phase, _phase, _progress))
            {
                throw new AtoCancelledException();
            }
        }

        public void SetProgress(string locKey, float t, string detail)
        {
            _phase = T(locKey);
            _progress = Mathf.Clamp01(t);
            if (EditorUtility.DisplayCancelableProgressBar("[ATO] " + _phase, detail ?? _phase, _progress))
            {
                throw new AtoCancelledException();
            }
        }

        public void Track(Object obj)
        {
            if (obj != null) TempObjects.Add(obj);
        }

        public void Save(Object obj)
        {
            if (obj == null || Context == null) return;
            Context.AssetSaver.SaveAsset(obj);
            Track(obj);
        }

        public void WarnNdmf(string key, params object[] args)
        {
            var msg = T(key, args);
            Warnings.Add(msg);
            Log.Warn(msg);
            ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.NonFatal, key, args);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                DecodeCache.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            EditorUtility.ClearProgressBar();
        }
    }

    public sealed class AtoBuildReport
    {
        public int SourceTextures;
        public int OutputTextures;
        public int AtlasCount;
        public int IslandCount;
        public long SourcePixels;
        public long OutputPixels;
        public readonly List<string> AtlasLines = new List<string>();
        public double Seconds;

        public float SavedPercent
        {
            get
            {
                if (SourcePixels <= 0) return 0f;
                return (float)(100.0 * (1.0 - (double)OutputPixels / SourcePixels));
            }
        }
    }
}
