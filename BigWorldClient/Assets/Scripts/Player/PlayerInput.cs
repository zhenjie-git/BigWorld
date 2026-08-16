using System.Collections.Generic;
using BigWorldClient.Network;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BigWorldClient
{
    /// <summary>
    /// 纯输入采集器：不判断状态、不决定切换、不发送协议。
    /// 只把输入事件和当前移动向量放进队列，由 MovePredictor 统一消费。
    /// </summary>
    public class PlayerInput : MonoBehaviour
    {
        public PlayerInputActions InputActions { get; private set; }
        public PlayerInputActions.PlayerActions PlayerActions { get; private set; }
        public Vector2 CurrentMovement { get; private set; }

        private readonly Queue<PlayerInputCommand> _commands = new();

        private void Awake()
        {
            InputActions = new PlayerInputActions();
            PlayerActions = InputActions.Player;

            PlayerActions.WalkToggle.started += _ => Enqueue(PlayerInputCommandKind.WalkToggle);
            PlayerActions.Movement.started += _ =>
            {
                Enqueue(PlayerInputCommandKind.MovementStarted);
            };
            PlayerActions.Movement.canceled += _ =>
            {
                CameraController.Instance.DisableRecentering();
                Enqueue(PlayerInputCommandKind.MovementCanceled);
            };
            PlayerActions.Movement.performed += _ => Enqueue(PlayerInputCommandKind.MovementPerformed);
            PlayerActions.Look.started += _ => { };
            PlayerActions.Dash.started += _ => Enqueue(PlayerInputCommandKind.Dash);
            PlayerActions.Jump.started += _ => Enqueue(PlayerInputCommandKind.Jump);
            PlayerActions.Sprint.performed += _ => Enqueue(PlayerInputCommandKind.Sprint);
        }

        private void OnEnable() => InputActions.Enable();
        private void OnDisable() => InputActions.Disable();

        private void Update()
        {
            CurrentMovement = PlayerActions.Movement.ReadValue<Vector2>();
        }

        public List<PlayerInputCommand> DrainCommands()
        {
            var list = new List<PlayerInputCommand>(_commands.Count);
            while (_commands.Count > 0)
                list.Add(_commands.Dequeue());
            return list;
        }

        private void Enqueue(PlayerInputCommandKind kind)
        {
            long tick = GameNetworkManager.Instance?.Clock?.TickNow() ?? 0;
            _commands.Enqueue(new PlayerInputCommand
            {
                Kind = kind,
                Movement = PlayerActions.Movement.ReadValue<Vector2>(),
                Tick = tick,
            });
        }
    }
}