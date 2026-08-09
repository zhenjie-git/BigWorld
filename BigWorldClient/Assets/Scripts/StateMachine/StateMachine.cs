using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient
{
    public abstract class StateMachine
    {
        protected IState currentState;
        protected PlayerStateTransitionTable transitionTable;

        public bool CanTransition(IState to)
        {
            if (transitionTable == null || currentState == null || to == null)
                return true;

            return transitionTable.CanTransition(currentState.Type, to.Type);
        }

        public void SetTransitionTable(PlayerStateTransitionTable table)
        {
            transitionTable = table;
            table.BuildLookup();
        }

        public virtual void ChangeState(IState newState)
        {
            currentState?.Exit();
            currentState = newState;
            newState.Enter();
        }

        public void Update()
        {
            currentState?.Update();
        }

        public void PhysicsUpdate()
        {
            currentState?.PhysicsUpdate();
        }

        public void OnAnimationEnterEvent()
        {
            currentState?.OnAnimationEnterEvent();
        }

        public void OnAnimationExitEvent()
        {
            currentState?.OnAnimationExitEvent();
        }

        public void OnAnimationTransitionEvent()
        {
            currentState?.OnAnimationTransitionEvent();
        }
    }
}
