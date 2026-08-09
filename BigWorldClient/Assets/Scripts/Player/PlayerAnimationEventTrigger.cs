using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient
{
    public class PlayerAnimationEventTrigger : MonoBehaviour
    {
        private PlayerController controller;

        private void Awake()
        {
            controller = transform.GetComponentInParent<PlayerController>();
        }

        public void TriggerOnMovementStateAnimationEnterEvent()
        {
            controller.OnMovementStateAnimationEnterEvent();
        }

        public void TriggerOnMovementStateAnimationExitEvent()
        {
            controller.OnMovementStateAnimationExitEvent();
        }

        public void TriggerOnMovementStateAnimationTransitionEvent()
        {
            controller.OnMovementStateAnimationTransitionEvent();
        }
    }
}
