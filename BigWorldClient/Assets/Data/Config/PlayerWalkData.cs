using System;
using UnityEngine;

namespace BigWorldClient
{
    [Serializable]
    public class PlayerWalkData
    {
        [field: SerializeField] [field: Range(0f, 1f)] public float SpeedModifier { get; private set; } = 0.225f;

        [field: SerializeField] public DisplacementCurveAsset DisplacementCurveX { get; private set; }
        [field: SerializeField] public DisplacementCurveAsset DisplacementCurveZ { get; private set; }
    }
}
