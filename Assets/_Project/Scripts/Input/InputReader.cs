using UnityEngine;
using UnityEngine.InputSystem;

namespace ToLive.Input
{
    /// <summary>
    /// Reads the "ToLive" Input Actions asset every frame and stores the values in static fields
    /// that ECS systems and the camera can read. (Unreal analogy: Enhanced Input actions bound in
    /// the PlayerController; here the "controller" is this component plus GatherPlayerInputSystem.)
    /// Rebind keys/buttons by double-clicking Assets/_Project/Input/ToLive.inputactions.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class InputReader : MonoBehaviour
    {
        public InputActionAsset Actions;

        public static Vector2 Move;
        public static Vector2 LookStick;   // right stick, -1..1 (rate)
        public static Vector2 LookDelta;   // mouse delta in pixels this frame
        public static float Vertical;
        public static float Thrust;
        public static bool Boost;
        public static bool MenuPressedThisFrame;
        public static bool UsingGamepad;

        // Combat. Presses are counted until the ECS input system consumes them, so a press is never
        // lost even if no simulation tick ran in that frame.
        public static int LightPresses, DashPresses, LockOnPresses;
        public static bool HeavyHeld, GuardHeld;

        InputAction m_Move, m_LookStick, m_LookDelta, m_Thrust, m_Ascend, m_Descend, m_Boost, m_Menu;
        InputAction m_Light, m_Heavy, m_Dash, m_Guard, m_LockOn;

        void OnEnable()
        {
            if (Actions == null)
            {
                Debug.LogError("[ToLive] InputReader has no Input Actions asset assigned.");
                enabled = false;
                return;
            }
            var map = Actions.FindActionMap("Player", true);
            m_Move = map.FindAction("Move", true);
            m_LookStick = map.FindAction("LookStick", true);
            m_LookDelta = map.FindAction("LookDelta", true);
            m_Thrust = map.FindAction("Thrust", true);
            m_Ascend = map.FindAction("Ascend", true);
            m_Descend = map.FindAction("Descend", true);
            m_Boost = map.FindAction("Boost", true);
            m_Menu = map.FindAction("Menu", true);
            m_Light = map.FindAction("Light", true);
            m_Heavy = map.FindAction("Heavy", true);
            m_Dash = map.FindAction("Dash", true);
            m_Guard = map.FindAction("Guard", true);
            m_LockOn = map.FindAction("LockOn", true);
            map.Enable();
        }

        void OnDisable()
        {
            Actions?.FindActionMap("Player")?.Disable();
        }

        void Update()
        {
            Move = Vector2.ClampMagnitude(m_Move.ReadValue<Vector2>(), 1f);
            LookStick = m_LookStick.ReadValue<Vector2>();
            LookDelta = m_LookDelta.ReadValue<Vector2>();
            Vertical = Mathf.Clamp(m_Ascend.ReadValue<float>() - m_Descend.ReadValue<float>(), -1f, 1f);
            Thrust = Mathf.Clamp01(m_Thrust.ReadValue<float>());
            Boost = m_Boost.IsPressed();
            MenuPressedThisFrame = m_Menu.WasPressedThisFrame();
            if (m_Light.WasPressedThisFrame()) LightPresses++;
            if (m_Dash.WasPressedThisFrame()) DashPresses++;
            if (m_LockOn.WasPressedThisFrame()) LockOnPresses++;
            HeavyHeld = m_Heavy.IsPressed();
            GuardHeld = m_Guard.IsPressed();
            if (Gamepad.current != null && Gamepad.current.wasUpdatedThisFrame && Gamepad.current.leftStick.ReadValue().sqrMagnitude + Gamepad.current.rightStick.ReadValue().sqrMagnitude > 0.05f)
                UsingGamepad = true;
            else if (Mouse.current != null && Mouse.current.delta.ReadValue().sqrMagnitude > 4f)
                UsingGamepad = false;
        }
    }
}
