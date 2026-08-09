using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient
{
    public interface IState
    {
        public PlayerMovementStateType Type { get; }
        public void Enter();
        public void Exit();
        public void Update();
        public void PhysicsUpdate();
        public void OnAnimationEnterEvent();
        public void OnAnimationExitEvent();
        public void OnAnimationTransitionEvent();
    }
}
