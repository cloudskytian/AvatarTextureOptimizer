// 编译验证桩：VRCSDK3A 最小表面（VRCAvatarDescriptor）/ Compile-check stubs: minimal VRCSDK3A surface.
// 仅覆盖 ATO 代码使用的成员。Not shipped with the package.

using System;
using UnityEngine;

namespace VRC.SDK3.Avatars.Components
{
    public class VRCAvatarDescriptor : MonoBehaviour
    {
        [System.Serializable]
        public class CustomAnimLayer
        {
            public RuntimeAnimatorController animatorController;
            public AnimatorLayerType type;
        }

        public CustomAnimLayer[] baseAnimationLayers { get; set; }
        public CustomAnimLayer[] specialAnimationLayers { get; set; }
    }

    public enum AnimatorLayerType
    {
        Base = 0, Additive = 1, Gesture = 2, Action = 3, FX = 4,
        Sitting = 5, TPose = 6, IKPose = 7, Walking = 8, Swimming = 9,
        Locomotion = 10, Debug = 11,
    }
}
