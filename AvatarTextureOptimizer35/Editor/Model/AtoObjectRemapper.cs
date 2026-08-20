using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Global object remapper: records old→new object replacements collected across stages
    /// (texture dedupe, atlas generation, material dedupe, mesh rewrite). The references stage
    /// applies it to renderer materials, material texture properties, and animation curves. /
    /// 全局对象重映射器：记录跨阶段收集的 旧→新 对象替换（贴图去重、图集生成、材质去重、网格重写）。
    /// 引用阶段将其应用到渲染器材质、材质贴图属性与动画曲线。
    ///
    /// Note (verified in NDMF source): ObjectRegistry does NOT rewrite animation curve references;
    /// we apply this remap ourselves (modeled after AAO's ObjectMapping). /
    /// 注（已读 NDMF 源码确认）：ObjectRegistry 不会改写动画曲线引用；本映射由我们自行应用（参照 AAO ObjectMapping）。
    /// </summary>
    internal sealed class AtoObjectRemapper
    {
        private readonly Dictionary<Object, Object> _remap = new Dictionary<Object, Object>();

        /// <summary>Register a replacement. / 注册替换。</summary>
        public void Register(Object oldObject, Object newObject)
        {
            if (oldObject == null || newObject == null || oldObject == newObject) return;
            // Resolve through existing chains so A→B→C ends at C. / 沿已有链解析，使 A→B→C 最终到 C。
            _remap[oldObject] = Resolve(newObject);
        }

        /// <summary>Resolve an object through the remap chain (identity if unmapped). / 沿链解析对象（未映射则返回自身）。</summary>
        public Object Resolve(Object obj)
        {
            if (obj == null) return null;
            var seen = 0;
            while (_remap.TryGetValue(obj, out var next) && next != null && next != obj && seen++ < 64)
            {
                obj = next;
            }
            return obj;
        }

        public bool Has(Object obj) => obj != null && _remap.ContainsKey(obj);
    }
}
