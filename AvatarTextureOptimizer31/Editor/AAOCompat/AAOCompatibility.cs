// AAOCompatibility.cs
// Registers UV channel evacuation for Avatar Optimizer compatibility.
// ATO modifies UV0 to point to atlases, so we evacuate the original UV0
// to an unused UV channel so AAO's UV-dependent features still work.
// 为 Avatar Optimizer 兼容性注册 UV 通道迁移。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Fosa.AvatarTextureOptimizer.Core;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.AAOCompat
{
    /// <summary>
    /// Integrates with AAO's UVUsageCompabilityAPI (note: the spelling is intentional,
    /// matching AAO's original API name). Registers UV evacuation so AAO can use the
    /// original UV coordinates for its mesh optimization features.
    /// 与 AAO 的 UVUsageCompabilityAPI 集成。
    /// </summary>
    internal sealed class AAOCompatibility
    {
        private readonly GameObject _avatarRoot;
        private readonly List<UVGroup> _uvGroups;
        private readonly ATOLogger _log;

        // The AAO API type, found via reflection (gracefully handles AAO not being installed)
        private static Type _uvUsageAPIType;
        private static MethodInfo _isTexCoordUsedMethod;
        private static MethodInfo _registerEvacuationMethod;

        static AAOCompatibility()
        {
            try
            {
                _uvUsageAPIType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");

                if (_uvUsageAPIType != null)
                {
                    _isTexCoordUsedMethod = _uvUsageAPIType.GetMethod("IsTexCoordUsed",
                        BindingFlags.Public | BindingFlags.Static);
                    _registerEvacuationMethod = _uvUsageAPIType.GetMethod("RegisterTexCoordEvacuation",
                        BindingFlags.Public | BindingFlags.Static);
                }
            }
            catch { }
        }

        public static bool IsAAOInstalled => _uvUsageAPIType != null;

        internal AAOCompatibility(GameObject avatarRoot, List<UVGroup> uvGroups, ATOLogger log)
        {
            _avatarRoot = avatarRoot;
            _uvGroups = uvGroups;
            _log = log;
        }

        /// <summary>
        /// For each SkinnedMeshRenderer whose UV0 was modified by ATO,
        /// finds an unused UV channel and registers evacuation with AAO.
        /// 为每个 UV0 被 ATO 修改的 SMR 注册 UV 迁移。
        /// </summary>
        internal void RegisterEvacuation()
        {
            if (!IsAAOInstalled || _registerEvacuationMethod == null)
            {
                _log.Verbose("AAO not installed or UVUsageCompabilityAPI not available. Skipping evacuation. / AAO 未安装，跳过迁移。");
                return;
            }

            int registered = 0;

            var renderers = _avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var smr in renderers)
            {
                if (smr.sharedMesh == null) continue;

                // Check if ATO modified UV0 on this renderer
                bool modified = _uvGroups.Any(ug =>
                    ug.Islands.Any(i => i.SourceRenderer == smr && i.UVChannel == 0));

                if (!modified) continue;

                // Find an unused UV channel for evacuation
                int evacChannel = FindUnusedUVChannel(smr);
                if (evacChannel < 0 || evacChannel > 7)
                {
                    _log.Warning($"Cannot find unused UV channel for evacuation on {smr.gameObject.name}. " +
                        "AAO UV compatibility may be compromised. / 无法找到空闲 UV 通道用于迁移。");
                    continue;
                }

                // Check if the evacuation channel is safe (not used by AAO)
                try
                {
                    bool usedByAAO = (bool)_isTexCoordUsedMethod.Invoke(null,
                        new object[] { smr, evacChannel });
                    if (usedByAAO)
                    {
                        _log.Verbose($"UV channel {evacChannel} is used by AAO on {smr.gameObject.name}, trying next.");
                        continue;
                    }

                    _registerEvacuationMethod.Invoke(null,
                        new object[] { smr, 0, evacChannel }); // original=0, saved=evacChannel

                    // Copy UV0 data to the evacuation channel on the mesh
                    CopyUVToChannel(smr.sharedMesh, 0, evacChannel);

                    registered++;
                    _log.Verbose($"Evacuated UV0 → UV{evacChannel} on {smr.gameObject.name} for AAO compatibility.");
                }
                catch (Exception ex)
                {
                    _log.Verbose($"UV evacuation failed on {smr.gameObject.name}: {ex.Message}");
                }
            }

            if (registered > 0)
                _log.Info($"AAO UV compatibility: registered {registered} evacuations. / 注册了 {registered} 个 UV 迁移。");
        }

        private int FindUnusedUVChannel(SkinnedMeshRenderer smr)
        {
            var mesh = smr.sharedMesh;
            if (mesh == null) return -1;

            for (int ch = 1; ch < 8; ch++)
            {
                var uvs = new List<Vector2>();
                mesh.GetUVs(ch, uvs);
                if (uvs.Count == 0)
                    return ch;
            }
            return -1;
        }

        private void CopyUVToChannel(Mesh mesh, int fromChannel, int toChannel)
        {
            var uvs = new List<Vector2>();
            mesh.GetUVs(fromChannel, uvs);
            if (uvs.Count > 0)
                mesh.SetUVs(toChannel, uvs);
        }
    }
}
