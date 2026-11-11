using ToLive.CameraRig;
using ToLive.Combat;
using ToLive.Destruction;
using ToLive.Net;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace ToLive.UI
{
    /// <summary>
    /// Combat HUD (power bar, lock-on target, state) and the F3 debug/perf overlay, plus the debug
    /// keys: F3 overlay, F5 reset dummy + rebuild building, F9 stress-destroy the building.
    /// Adds its elements to the same UI Toolkit document as <see cref="GameUI"/>.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class CombatHud : MonoBehaviour
    {
        VisualElement m_Root, m_PowerFill, m_TargetPanel, m_TargetFill, m_Reticle, m_Overlay;
        Label m_State, m_Center, m_OverlayText;
        bool m_ShowOverlay;
        float m_Accum, m_MaxMs;
        int m_Frames;
        string m_FpsText = "";
        World m_World;
        EntityQuery m_AckQuery;

        public static float SmoothedFps { get; private set; }
        public static float SmoothedFrameMs { get; private set; }

        void Start()
        {
            var doc = GetComponent<UIDocument>().rootVisualElement;
            m_Root = new VisualElement { pickingMode = PickingMode.Ignore };
            m_Root.style.position = Position.Absolute;
            m_Root.style.left = 0; m_Root.style.right = 0; m_Root.style.top = 0; m_Root.style.bottom = 0;
            doc.Add(m_Root);

            // Power bar, top left.
            var power = Panel(m_Root, 24, 24, 360);
            MakeLabel(power, "POWER", 14);
            m_PowerFill = Bar(power, new Color(0.3f, 0.75f, 1f));
            m_State = MakeLabel(power, "", 16);

            // Lock-on target, top centre.
            m_TargetPanel = Panel(m_Root, 0, 24, 300);
            m_TargetPanel.style.left = new Length(50, LengthUnit.Percent);
            m_TargetPanel.style.marginLeft = -150;
            MakeLabel(m_TargetPanel, "TARGET", 14);
            m_TargetFill = Bar(m_TargetPanel, new Color(1f, 0.35f, 0.25f));

            // Reticle drawn over the locked target.
            m_Reticle = new VisualElement { pickingMode = PickingMode.Ignore };
            m_Reticle.style.position = Position.Absolute;
            m_Reticle.style.width = 36; m_Reticle.style.height = 36;
            m_Reticle.style.borderTopWidth = 3; m_Reticle.style.borderBottomWidth = 3;
            m_Reticle.style.borderLeftWidth = 3; m_Reticle.style.borderRightWidth = 3;
            var red = new Color(1f, 0.35f, 0.25f);
            m_Reticle.style.borderTopColor = red; m_Reticle.style.borderBottomColor = red;
            m_Reticle.style.borderLeftColor = red; m_Reticle.style.borderRightColor = red;
            m_Reticle.style.rotate = new Rotate(45);
            m_Root.Add(m_Reticle);

            // Big centre message (KO).
            m_Center = MakeLabel(m_Root, "", 40);
            m_Center.style.position = Position.Absolute;
            m_Center.style.top = new Length(40, LengthUnit.Percent);
            m_Center.style.left = 0; m_Center.style.right = 0;
            m_Center.style.unityTextAlign = TextAnchor.MiddleCenter;

            // Debug overlay, top right.
            m_Overlay = Panel(m_Root, 0, 24, 340);
            m_Overlay.style.left = StyleKeyword.Auto;
            m_Overlay.style.right = 24;
            m_OverlayText = MakeLabel(m_Overlay, "", 15);
        }

        static VisualElement Panel(VisualElement parent, float left, float top, float width)
        {
            var p = new VisualElement { pickingMode = PickingMode.Ignore };
            p.style.position = Position.Absolute;
            p.style.left = left; p.style.top = top; p.style.width = width;
            p.style.paddingLeft = 10; p.style.paddingRight = 10;
            p.style.paddingTop = 6; p.style.paddingBottom = 6;
            p.style.backgroundColor = new Color(0, 0, 0, 0.45f);
            parent.Add(p);
            return p;
        }

        static Label MakeLabel(VisualElement parent, string text, int size)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.style.color = Color.white;
            l.style.fontSize = size;
            parent.Add(l);
            return l;
        }

        static VisualElement Bar(VisualElement parent, Color color)
        {
            var back = new VisualElement();
            back.style.height = 12;
            back.style.backgroundColor = new Color(1, 1, 1, 0.15f);
            parent.Add(back);
            var fill = new VisualElement();
            fill.style.height = 12;
            fill.style.width = new Length(100, LengthUnit.Percent);
            fill.style.backgroundColor = color;
            back.Add(fill);
            return fill;
        }

        void Update()
        {
            // Frame timing (always measured, shown when the overlay is on).
            var ms = Time.unscaledDeltaTime * 1000f;
            m_Accum += Time.unscaledDeltaTime; m_Frames++; m_MaxMs = Mathf.Max(m_MaxMs, ms);
            if (m_Accum >= 0.5f)
            {
                SmoothedFps = m_Frames / m_Accum;
                SmoothedFrameMs = m_Accum * 1000f / m_Frames;
                m_FpsText = $"FPS {SmoothedFps:0}   frame {SmoothedFrameMs:0.0} ms (max {m_MaxMs:0.0})";
                m_Accum = 0f; m_Frames = 0; m_MaxMs = 0f;
            }

            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.f3Key.wasPressedThisFrame) m_ShowOverlay = !m_ShowOverlay;
                if (NetSession.IsActive && !GameUI.MenuOpen)
                {
                    if (kb.f5Key.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.ResetDummyAndBuilding);
                    if (kb.f9Key.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.DestroyBuilding);
                    if (kb.f6Key.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.ToggleDummyGuard);
                }
            }
            if (m_Root == null) return;

            var inGame = NetSession.IsActive && !GameUI.MenuOpen;
            m_Root.style.display = inGame ? DisplayStyle.Flex : DisplayStyle.None;
            m_Overlay.style.display = m_ShowOverlay ? DisplayStyle.Flex : DisplayStyle.None;
            if (m_ShowOverlay)
                m_OverlayText.text = $"{m_FpsText}\nPing {Ping():0} ms\nLive debris {DebrisSystem.LiveDebris}  (total {DebrisSystem.TotalDebris})\n" +
                                     $"Broken chunks {DebrisSystem.BrokenChunks} / {DebrisSystem.ChunkCount}\nF5 reset + rebuild   F6 dummy guard   F9 destroy building";
            if (!inGame || !FlightCameraRig.HasLocalPlayer)
            {
                m_TargetPanel.style.display = DisplayStyle.None;
                m_Reticle.style.display = DisplayStyle.None;
                return;
            }

            const float maxPower = 100f;
            m_PowerFill.style.width = new Length(Mathf.Clamp01(FlightCameraRig.LocalPower / maxPower) * 100f, LengthUnit.Percent);
            var action = FlightCameraRig.LocalAction;
            m_State.text = action == CombatAction.HeavyCharge ? $"CHARGING {FlightCameraRig.LocalCharge * 100f:0}%" : CombatAction.Name(action);
            m_Center.text = action == CombatAction.KnockedOut ? "KNOCKED OUT\nrespawning..." : "";

            if (FlightCameraRig.HasLockTarget)
            {
                m_TargetPanel.style.display = DisplayStyle.Flex;
                m_TargetFill.style.width = new Length(Mathf.Clamp01(FlightCameraRig.LockTargetPower / maxPower) * 100f, LengthUnit.Percent);
                var cam = Camera.main;
                var sp = cam != null ? cam.WorldToScreenPoint(FlightCameraRig.LockTargetPosition) : Vector3.back;
                var panel = m_Root.panel;
                if (sp.z > 0f && panel != null)
                {
                    var p = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(sp.x, Screen.height - sp.y));
                    m_Reticle.style.display = DisplayStyle.Flex;
                    m_Reticle.style.left = p.x - 18; m_Reticle.style.top = p.y - 18;
                }
                else m_Reticle.style.display = DisplayStyle.None;
            }
            else
            {
                m_TargetPanel.style.display = DisplayStyle.None;
                m_Reticle.style.display = DisplayStyle.None;
            }
        }

        float Ping()
        {
            var client = ClientServerBootstrap.ClientWorld;
            if (client == null) return 0f;
            if (m_World != client)
            {
                m_World = client;
                m_AckQuery = client.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkSnapshotAck>());
            }
            if (m_AckQuery.CalculateEntityCount() != 1) return 0f;
            m_AckQuery.CompleteDependency();
            return m_AckQuery.GetSingleton<NetworkSnapshotAck>().EstimatedRTT;
        }
    }
}
