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
        VisualElement m_SurgeFill, m_BeamFill, m_Flash;
        Label m_Tier, m_Ready;
        static Color s_FlashColor;
        static float s_FlashTime, s_FlashDuration;

        /// <summary>Full-screen flash (e.g. the eruption), fading out over <paramref name="duration"/> seconds.</summary>
        public static void Flash(Color color, float duration) { s_FlashColor = color; s_FlashDuration = duration; s_FlashTime = Time.unscaledTime; }

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
            MakeLabel(power, "SURGE", 14);
            m_SurgeFill = Bar(power, new Color(1f, 1f, 1f));
            MakeLabel(power, "BEAM", 12);
            m_BeamFill = Bar(power, new Color(0.7f, 0.7f, 0.75f));
            m_Tier = MakeLabel(power, "", 14);

            // "SURGE READY" prompt, centre-bottom.
            m_Ready = MakeLabel(m_Root, "", 30);
            m_Ready.style.position = Position.Absolute;
            m_Ready.style.bottom = new Length(18, LengthUnit.Percent);
            m_Ready.style.left = 0; m_Ready.style.right = 0;
            m_Ready.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_Ready.style.unityFontStyleAndWeight = FontStyle.Bold;

            // Full-screen flash (eruption).
            m_Flash = new VisualElement { pickingMode = PickingMode.Ignore };
            m_Flash.style.position = Position.Absolute;
            m_Flash.style.left = 0; m_Flash.style.right = 0; m_Flash.style.top = 0; m_Flash.style.bottom = 0;
            doc.Add(m_Flash);

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
                    if (kb.f4Key.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.FillSurgeMeter);
                    if (kb.f7Key.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.ToggleTier);
                    if (kb.f8Key.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.DummyRitual);
                    if (kb.leftBracketKey.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.AlignmentDown);
                    if (kb.rightBracketKey.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.AlignmentUp);
                }
            }
            if (m_Flash != null)
            {
                var a = s_FlashDuration > 0f ? Mathf.Clamp01(1f - (Time.unscaledTime - s_FlashTime) / s_FlashDuration) : 0f;
                var fc = s_FlashColor; fc.a = a * a;
                m_Flash.style.backgroundColor = fc;
            }
            if (m_Root == null) return;

            var inGame = NetSession.IsActive && !GameUI.MenuOpen;
            m_Root.style.display = inGame ? DisplayStyle.Flex : DisplayStyle.None;
            m_Overlay.style.display = m_ShowOverlay ? DisplayStyle.Flex : DisplayStyle.None;
            if (m_ShowOverlay)
                m_OverlayText.text = $"{m_FpsText}\nPing {Ping():0} ms\nLive debris {DebrisSystem.LiveDebris}  (total {DebrisSystem.TotalDebris})\n" +
                                     $"Broken chunks {DebrisSystem.BrokenChunks} / {DebrisSystem.ChunkCount}\nF4 fill surge  F5 reset  F6 dummy guard  F7 tier  F8 dummy ritual  F9 destroy  [ ] alignment";
            if (!inGame || !FlightCameraRig.HasLocalPlayer)
            {
                m_TargetPanel.style.display = DisplayStyle.None;
                m_Reticle.style.display = DisplayStyle.None;
                return;
            }

            var maxPower = ToLive.Power.PowerPresenter.LocalTier >= 2 ? 150f : 100f;
            m_PowerFill.style.width = new Length(Mathf.Clamp01(FlightCameraRig.LocalPower / maxPower) * 100f, LengthUnit.Percent);
            var action = FlightCameraRig.LocalAction;
            m_State.text = action == CombatAction.HeavyCharge ? $"CHARGING {FlightCameraRig.LocalCharge * 100f:0}%" : CombatAction.Name(action);
            var phase = ToLive.Power.PowerPresenter.LocalPhase;
            var meter = ToLive.Power.PowerPresenter.LocalMeter;
            var energy = ToLive.Power.PowerLook.Instance != null ? ToLive.Power.PowerLook.Instance.EnergyColor(ToLive.Power.PowerPresenter.LocalAlignment) : Color.white;
            var energyLdr = energy.maxColorComponent > 1f ? energy / energy.maxColorComponent : energy;
            m_SurgeFill.style.width = new Length(Mathf.Clamp01(meter) * 100f, LengthUnit.Percent);
            m_SurgeFill.style.backgroundColor = phase == ToLive.Power.SurgePhase.Burnout ? new Color(0.35f, 0.35f, 0.38f) : energyLdr;
            m_BeamFill.style.width = new Length(Mathf.Clamp01(ToLive.Power.PowerPresenter.LocalBeamEnergy) * 100f, LengthUnit.Percent);
            var align = ToLive.Power.PowerPresenter.LocalAlignment;
            m_Tier.text = (ToLive.Power.PowerPresenter.LocalTier >= 2 ? "TIER II" : "TIER I") + (align < -0.2f ? "   TRUST" : align > 0.2f ? "   DOMINANCE" : "   BALANCED") +
                          (phase != ToLive.Power.SurgePhase.None ? "   " + ToLive.Power.SurgePhase.Name(phase) : "");
            var ready = phase == ToLive.Power.SurgePhase.None && meter >= 0.999f;
            m_Ready.text = ready ? "SURGE READY  -  hold L3 + R3  (R)" : phase == ToLive.Power.SurgePhase.Burnout ? "BURNOUT" : "";
            m_Ready.style.color = ready ? energyLdr * (0.75f + 0.25f * Mathf.Sin(Time.time * 6f)) : new Color(0.6f, 0.6f, 0.62f);
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
