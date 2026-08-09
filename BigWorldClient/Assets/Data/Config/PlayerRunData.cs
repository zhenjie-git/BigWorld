using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient
{
    [Serializable]
    public class PlayerRunData
    {
        [field: SerializeField] [field: Range(1f, 2f)] public float SpeedModifier { get; private set; } = 1f;

        [field: SerializeField] public DisplacementCurveAsset DisplacementCurveX { get; private set; }
        [field: SerializeField] public DisplacementCurveAsset DisplacementCurveZ { get; private set; }
    }
}
