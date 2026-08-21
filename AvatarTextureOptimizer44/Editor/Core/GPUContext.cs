// GPUContext.cs - RenderTexture / ComputeShader pool with strict lifetime, for speed without leaks.
// RenderTexture/ComputeShader 池，严格生命周期管理，追求速度且不泄漏。
// Everything acquired must be released; Dispose() is called in the pipeline finally-block.
// 所有申请必须释放；管线 finally 中调用 Dispose()。
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.ATO.Editor.Core
{
    public sealed class GPUContext : IDisposable
    {
        private readonly List<RenderTexture> _temps = new List<RenderTexture>();   // from GetTemporary / 来自池
        private readonly List<RenderTexture> _owned = new List<RenderTexture>();   // created by us / 自建
        private readonly Dictionary<string, ComputeShader> _shaders = new Dictionary<string, ComputeShader>();
        private readonly Dictionary<string, Material> _mats = new Dictionary<string, Material>();

        public bool IsAvailable => SystemInfo.supportsComputeShaders && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>Pooled temp RT; released together in Dispose. / 池化临时RT，随Dispose统一归还。</summary>
        public RenderTexture Temp(int w, int h, RenderTextureFormat fmt, bool mip = false, string name = "ATO_TMP")
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, fmt, RenderTextureReadWrite.Linear);
            if (rt == null) throw new InvalidOperationException("[ATO] RenderTexture.GetTemporary failed / 获取临时RT失败");
            if (mip) { rt.useMipMap = true; rt.autoGenerateMips = false; }
            rt.name = name;
            _temps.Add(rt);
            return rt;
        }

        /// <summary>Owned RT destroyed in Dispose. / 自建RT，随Dispose销毁。</summary>
        public RenderTexture Owned(int w, int h, RenderTextureFormat fmt, bool uav = false, bool mip = false, string name = "ATO_OWN")
        {
            var rt = new RenderTexture(w, h, 0, fmt, RenderTextureReadWrite.Linear)
            { name = name, enableRandomWrite = uav, useMipMap = mip, autoGenerateMips = false };
            rt.Create();
            _owned.Add(rt);
            return rt;
        }

        /// <summary>Load a compute shader shipped in this package by name. / 按名称加载包内compute shader。</summary>
        public ComputeShader Compute(string name)
        {
            if (_shaders.TryGetValue(name, out var c) && c != null) return c;
            string pkgRoot = Path.GetFullPath(Localization.PackageRoot.Folder);
            string projRoot = Path.GetDirectoryName(Application.dataPath);
            string path = null;
            foreach (var g in AssetDatabase.FindAssets($"t:ComputeShader {name}"))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (Path.GetFileNameWithoutExtension(p) != name) continue;
                string dir = Path.GetFullPath(Path.Combine(projRoot, Path.GetDirectoryName(p) ?? ""));
                if (dir.StartsWith(pkgRoot, StringComparison.OrdinalIgnoreCase)) { path = p; break; }
            }
            c = path != null ? AssetDatabase.LoadAssetAtPath<ComputeShader>(path) : null;
            if (c == null) throw new InvalidOperationException($"[ATO] compute shader missing / 缺少计算着色器: {name}");
            _shaders[name] = c; // asset shared, not destroyed / 共享资产不销毁
            return c;
        }

        /// <summary>Cached hidden material for blit-style ops. / 用于类Blit操作的缓存隐藏材质。</summary>
        public Material Mat(string name, string shaderSrc)
        {
            if (_mats.TryGetValue(name, out var m) && m != null) return m;
            var sh = UnityEngine.Shader.Find(shaderSrc) ?? UnityEngine.Shader.Find("Hidden/ATO/" + name);
            if (sh == null) throw new InvalidOperationException($"[ATO] shader missing / 缺少着色器: {name}");
            m = new Material(sh) { name = "ATO_" + name, hideFlags = HideFlags.DontSave };
            _mats[name] = m;
            return m;
        }

        public void Dispose()
        {
            foreach (var rt in _temps) if (rt != null) RenderTexture.ReleaseTemporary(rt);
            _temps.Clear();
            foreach (var rt in _owned) if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
            _owned.Clear();
            foreach (var m in _mats.Values) if (m != null) UnityEngine.Object.DestroyImmediate(m);
            _mats.Clear();
            _shaders.Clear();
        }
    }
}
