using System.Collections;
using UnityEngine;

namespace BigWorldClient.UI.Framework
{
    public abstract class UIEffectBase : MonoBehaviour
    {
        public abstract IEnumerator PlayShowEffect();
        public abstract IEnumerator PlayHideEffect();
    }
}
