using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BigWorldClient
{
    public class PlayerInput : MonoBehaviour
    {
        public PlayerInputActions InputActions { get; private set; }
        public PlayerInputActions.PlayerActions PlayerActions { get; private set; }

        private PlayerController player;

        private void Awake()
        {
            InputActions = new PlayerInputActions();
            PlayerActions = InputActions.Player;

            player = GetComponent<PlayerController>();

            PlayerActions.WalkToggle.started += _ =>
            {
                player.OnWalkToggleInput();
            };

            PlayerActions.Movement.started += OnMovementStarted;
            PlayerActions.Movement.canceled += _ =>
            {
                CameraController.Instance.DisableRecentering();
                player.OnMovementCanceledInput();
            };
            PlayerActions.Look.started += OnLookStarted;

            PlayerActions.Dash.started += _ => player.OnDashInput();
            PlayerActions.Jump.started += _ => player.OnJumpInput();
            PlayerActions.Sprint.performed += _ => player.OnSprintInput();
            PlayerActions.Movement.performed += _ => player.OnMovementPerformedInput();
        }

        private void OnEnable() => InputActions.Enable();
        private void OnDisable() => InputActions.Disable();

        private void Update()
        {
            player.MovementInput = PlayerActions.Movement.ReadValue<Vector2>();
            // CameraController.Instance.UpdateRecenteringState(player.MovementInput);
        }

        private void OnMovementStarted(InputAction.CallbackContext ctx)
        {
            player.OnMovementStartedInput();
        }

        private void OnLookStarted(InputAction.CallbackContext ctx)
        {
            
        }


    }
}
