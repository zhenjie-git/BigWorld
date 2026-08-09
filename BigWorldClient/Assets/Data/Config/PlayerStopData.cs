using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient
{
    [Serializable]
    public class PlayerStopData
    {
        [field: SerializeField] [field: Range(0f, 15f)] public float LightDecelerationForce { get; private set; } = 5f;
        [field: SerializeField] [field: Range(0f, 15f)] public float MediumDecelerationForce { get; private set; } = 6.5f;
        [field: SerializeField] [field: Range(0f, 15f)] public float HardDecelerationForce { get; private set; } = 5f;

        [field: SerializeField] public DisplacementCurveAsset LightDisplacementCurveX { get; private set; }
        [field: SerializeField] public DisplacementCurveAsset LightDisplacementCurveZ { get; private set; }
        [field: SerializeField] public DisplacementCurveAsset MediumDisplacementCurveX { get; private set; }
        [field: SerializeField] public DisplacementCurveAsset MediumDisplacementCurveZ { get; private set; }
        [field: SerializeField] public DisplacementCurveAsset HardDisplacementCurveX { get; private set; }
        [field: SerializeField] public DisplacementCurveAsset HardDisplacementCurveZ { get; private set; }
    }
}
