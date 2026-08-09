using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient
{
    [Serializable]
    public class PlayerJumpData
    {
        [field: SerializeField] public PlayerRotationData RotationData { get; private set; }

        [field: Header("JumpUp Displacement Curves")]
        [field: SerializeField] public DisplacementCurveAsset JumpUpCurveX { get; private set; }
        [field: SerializeField] public DisplacementCurveAsset JumpUpCurveY { get; private set; }
        [field: SerializeField] public DisplacementCurveAsset JumpUpCurveZ { get; private set; }

        [field: Header("JumpDown Displacement Curves")]
        [field: SerializeField] public DisplacementCurveAsset JumpDownCurveX { get; private set; }
        [field: SerializeField] public DisplacementCurveAsset JumpDownCurveY { get; private set; }
        [field: SerializeField] public DisplacementCurveAsset JumpDownCurveZ { get; private set; }
    }
}
