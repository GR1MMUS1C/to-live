using ToLive.CameraRig;
using ToLive.Combat;
using ToLive.Destruction;
using ToLive.Net;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using OV = ToLive.Opposition.OppositionView;

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
        // milestone 3B: heat, rival, incoming fire, unit health bars
        VisualElement m_HeatPanel, m_HeatFill, m_RivalPanel, m_RivalFill, m_Incoming, m_IncomingFill, m_IncomingArrow;
        readonly VisualElement[] m_HeatPips = new VisualElement[3];
        Label m_HeatLabel, m_RivalName, m_RivalState, m_IncomingText, m_Announce, m_Suppressed;
        readonly System.Collections.Generic.List<(VisualElement back, VisualElement fill)> m_UnitBars = new System.Collections.Generic.List<(VisualElement, VisualElement)>();
        byte m_SeenRivalDowns;
        bool m_RivalWasDown, m_RivalSeen;
        float m_AnnounceUntil;
        static readonly string[] s_HeatNames = { "CALM", "POLICE", "MILITARY", "ANTI-GOD" };
        static readonly string[] s_Difficulty = { "EASY", "NORMAL", "HARD" };
        // milestone 4A: the district meter, population, your standing and the cause feed
        VisualElement m_DistrictPanel, m_MeterMarker, m_MeterTrust, m_MeterFear;
        Label m_DistrictLabel, m_StandingLabel;
        readonly Label[] m_FeedLines = new Label[5];
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
            m_Suppressed = MakeLabel(power, "", 14);
            m_Suppressed.style.color = new Color(1f, 0.35f, 0.3f);

            BuildOppositionHud();
            BuildDistrictHud();

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

            // Debug overlay, top right (below the heat panel).
            m_Overlay = Panel(m_Root, 0, 130, 340);
            m_Overlay.style.left = StyleKeyword.Auto;
            m_Overlay.style.right = 24;
            m_OverlayText = MakeLabel(m_Overlay, "", 15);
        }

        void BuildDistrictHud()
        {
            // bottom centre: trust (cyan) <- meter -> dominance (crimson), population, your standing
            m_DistrictPanel = Panel(m_Root, 0, 0, 460);
            m_DistrictPanel.style.top = StyleKeyword.Auto;
            m_DistrictPanel.style.bottom = 22;
            m_DistrictPanel.style.left = new Length(50, LengthUnit.Percent);
            m_DistrictPanel.style.marginLeft = -230;
            m_DistrictLabel = MakeLabel(m_DistrictPanel, "", 14);
            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.style.height = 12; bar.style.flexDirection = FlexDirection.Row; bar.style.marginTop = 3; bar.style.marginBottom = 3;
            m_DistrictPanel.Add(bar);
            m_MeterTrust = new VisualElement { pickingMode = PickingMode.Ignore };
            m_MeterTrust.style.width = new Length(50, LengthUnit.Percent);
            m_MeterTrust.style.backgroundColor = new Color(0.25f, 0.75f, 0.95f, 0.75f);
            m_MeterFear = new VisualElement { pickingMode = PickingMode.Ignore };
            m_MeterFear.style.width = new Length(50, LengthUnit.Percent);
            m_MeterFear.style.backgroundColor = new Color(0.9f, 0.1f, 0.25f, 0.75f);
            bar.Add(m_MeterTrust); bar.Add(m_MeterFear);
            m_MeterMarker = new VisualElement { pickingMode = PickingMode.Ignore };
            m_MeterMarker.style.position = Position.Absolute;
            m_MeterMarker.style.width = 4; m_MeterMarker.style.height = 18; m_MeterMarker.style.top = -3;
            m_MeterMarker.style.backgroundColor = Color.white;
            bar.Add(m_MeterMarker);
            var ends = new VisualElement { pickingMode = PickingMode.Ignore };
            ends.style.flexDirection = FlexDirection.Row; ends.style.justifyContent = Justify.SpaceBetween;
            m_DistrictPanel.Add(ends);
            MakeLabel(ends, "TRUST", 11).style.color = new Color(0.5f, 0.85f, 1f);
            m_StandingLabel = MakeLabel(ends, "", 12);
            MakeLabel(ends, "DOMINANCE", 11).style.color = new Color(1f, 0.4f, 0.5f);

            // cause feed, above the meter
            for (var i = 0; i < m_FeedLines.Length; i++)
            {
                var l = MakeLabel(m_Root, "", 15);
                l.style.position = Position.Absolute;
                l.style.bottom = 100 + i * 20;
                l.style.left = 0; l.style.right = 0;
                l.style.unityTextAlign = TextAnchor.MiddleCenter;
                m_FeedLines[i] = l;
            }
        }

        void UpdateDistrictHud()
        {
            var present = ToLive.District.DistrictView.Present;
            m_DistrictPanel.style.display = present ? DisplayStyle.Flex : DisplayStyle.None;
            if (!present) { foreach (var l in m_FeedLines) l.text = ""; return; }
            var v = ToLive.District.DistrictView.Meter; // -1 trust .. +1 dominance
            var who = !ToLive.District.DistrictView.HasLeader ? "nobody holds it" :
                (ToLive.District.DistrictView.LeaderIsLocal ? "you hold it" : "held by another god") +
                (ToLive.District.DistrictView.LeaderAlignment < 0f ? " (trust)" : " (dominance)");
            m_DistrictLabel.text = $"DISTRICT   {who}   ·   people {ToLive.District.DistrictView.Alive} / {ToLive.District.DistrictView.Population}";
            m_MeterTrust.style.width = new Length((1f - v) * 50f, LengthUnit.Percent);
            m_MeterFear.style.width = new Length((1f + v) * 50f, LengthUnit.Percent);
            m_MeterMarker.style.left = new Length((v + 1f) * 50f, LengthUnit.Percent);
            m_StandingLabel.text = $"you: trust {ToLive.District.DistrictView.MyTrust:0}   fear {ToLive.District.DistrictView.MyFear:0}";
            var feed = ToLive.District.DistrictView.Feed;
            for (var i = 0; i < m_FeedLines.Length; i++)
            {
                var idx = feed.Count - 1 - i;
                if (idx < 0) { m_FeedLines[i].text = ""; continue; }
                var (text, time, good) = feed[idx];
                var age = Time.unscaledTime - time;
                var a = Mathf.Clamp01(1f - (age - 4f) / 2f);
                m_FeedLines[i].text = a > 0f ? text : "";
                m_FeedLines[i].style.color = good ? new Color(0.55f, 0.9f, 1f, a) : new Color(1f, 0.45f, 0.5f, a);
            }
        }

        void BuildOppositionHud()
        {
            // Heat: three pips + level name + progress to the next level, top right.
            m_HeatPanel = Panel(m_Root, 0, 24, 340);
            m_HeatPanel.style.left = StyleKeyword.Auto;
            m_HeatPanel.style.right = 24;
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            m_HeatPanel.Add(row);
            MakeLabel(row, "HEAT", 14).style.marginRight = 8;
            var heatColor = new Color(1f, 0.55f, 0.2f);
            for (var i = 0; i < 3; i++)
            {
                var pip = new VisualElement { pickingMode = PickingMode.Ignore };
                pip.style.width = 26; pip.style.height = 14; pip.style.marginRight = 5;
                pip.style.borderTopWidth = 2; pip.style.borderBottomWidth = 2; pip.style.borderLeftWidth = 2; pip.style.borderRightWidth = 2;
                pip.style.borderTopColor = heatColor; pip.style.borderBottomColor = heatColor; pip.style.borderLeftColor = heatColor; pip.style.borderRightColor = heatColor;
                row.Add(pip);
                m_HeatPips[i] = pip;
            }
            m_HeatLabel = MakeLabel(row, "", 15);
            m_HeatLabel.style.marginLeft = 6;
            m_HeatLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            m_HeatFill = Bar(m_HeatPanel, heatColor);
            m_HeatFill.parent.style.height = 5; m_HeatFill.style.height = 5;
            m_HeatFill.parent.style.marginTop = 4;

            // Rival name plate (when locked on or near), top centre under the target panel.
            m_RivalPanel = Panel(m_Root, 0, 86, 340);
            m_RivalPanel.style.left = new Length(50, LengthUnit.Percent);
            m_RivalPanel.style.marginLeft = -170;
            m_RivalName = MakeLabel(m_RivalPanel, "", 16);
            m_RivalName.style.unityFontStyleAndWeight = FontStyle.Bold;
            m_RivalName.style.color = new Color(0.82f, 0.6f, 1f);
            m_RivalFill = Bar(m_RivalPanel, new Color(0.6f, 0.3f, 0.95f));
            m_RivalState = MakeLabel(m_RivalPanel, "", 13);

            // Incoming fire (the anti-god weapon is locked on you): centre warning + edge arrow.
            m_Incoming = Panel(m_Root, 0, 0, 420);
            m_Incoming.style.top = new Length(22, LengthUnit.Percent);
            m_Incoming.style.left = new Length(50, LengthUnit.Percent);
            m_Incoming.style.marginLeft = -210;
            m_Incoming.style.backgroundColor = new Color(0.35f, 0f, 0f, 0.55f);
            m_IncomingText = MakeLabel(m_Incoming, "", 22);
            m_IncomingText.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_IncomingText.style.unityFontStyleAndWeight = FontStyle.Bold;
            m_IncomingFill = Bar(m_Incoming, new Color(1f, 0.2f, 0.15f));
            m_IncomingArrow = new VisualElement { pickingMode = PickingMode.Ignore };
            m_IncomingArrow.style.position = Position.Absolute;
            m_IncomingArrow.style.width = 34; m_IncomingArrow.style.height = 34;
            m_IncomingArrow.style.borderTopWidth = 6; m_IncomingArrow.style.borderRightWidth = 6;
            var red = new Color(1f, 0.2f, 0.15f);
            m_IncomingArrow.style.borderTopColor = red; m_IncomingArrow.style.borderRightColor = red;
            m_Root.Add(m_IncomingArrow);

            // Announcements (rival down / returns).
            m_Announce = MakeLabel(m_Root, "", 28);
            m_Announce.style.position = Position.Absolute;
            m_Announce.style.top = new Length(30, LengthUnit.Percent);
            m_Announce.style.left = 0; m_Announce.style.right = 0;
            m_Announce.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_Announce.style.unityFontStyleAndWeight = FontStyle.Bold;
            m_Announce.style.color = new Color(0.82f, 0.6f, 1f);
        }

        void UpdateOppositionHud()
        {
            var level = OV.HeatLevel;
            for (var i = 0; i < 3; i++)
                m_HeatPips[i].style.backgroundColor = i < level ? new Color(1f, 0.45f + 0.1f * i, 0.15f) : new Color(0, 0, 0, 0);
            m_HeatLabel.text = s_HeatNames[Mathf.Clamp(level, 0, 3)] + (OV.Units > 0 ? $"   units {OV.Units}" : "");
            m_HeatFill.style.width = new Length(Mathf.Clamp01(OV.HeatValue / Mathf.Max(1f, OV.HeatNext)) * 100f, LengthUnit.Percent);

            // rival name plate
            var showRival = OV.RivalPresent && (OV.RivalLocked || OV.RivalNear || OV.RivalDown);
            m_RivalPanel.style.display = showRival ? DisplayStyle.Flex : DisplayStyle.None;
            if (showRival)
            {
                m_RivalName.text = $"VANTA   rival ({s_Difficulty[Mathf.Clamp(OV.RivalDifficulty, 0, 2)]})";
                m_RivalFill.style.width = new Length(Mathf.Clamp01(OV.RivalPower / Mathf.Max(1f, OV.RivalMaxPower)) * 100f, LengthUnit.Percent);
                m_RivalState.text = OV.RivalDown ? $"DOWN - back in {OV.RivalRespawnIn:0} s"
                    : OV.RivalPhase != ToLive.Power.SurgePhase.None ? ToLive.Power.SurgePhase.Name(OV.RivalPhase) : "";
            }

            // announcements
            if (OV.RivalPresent)
            {
                if (!m_RivalSeen) { m_RivalSeen = true; m_SeenRivalDowns = OV.RivalDowns; m_RivalWasDown = OV.RivalDown; }
                if (OV.RivalDowns != m_SeenRivalDowns) { m_SeenRivalDowns = OV.RivalDowns; m_Announce.text = "RIVAL DOWN"; m_AnnounceUntil = Time.unscaledTime + 3f; }
                if (m_RivalWasDown && !OV.RivalDown) { m_Announce.text = "THE RIVAL RETURNS"; m_AnnounceUntil = Time.unscaledTime + 3f; }
                m_RivalWasDown = OV.RivalDown;
            }
            else m_RivalSeen = false;
            m_Announce.style.display = Time.unscaledTime < m_AnnounceUntil ? DisplayStyle.Flex : DisplayStyle.None;

            // incoming fire
            var cam = Camera.main;
            m_Incoming.style.display = OV.IncomingBeam ? DisplayStyle.Flex : DisplayStyle.None;
            m_IncomingArrow.style.display = DisplayStyle.None;
            if (OV.IncomingBeam)
            {
                var p = OV.IncomingProgress;
                m_IncomingText.text = OV.IncomingFiring ? "ANTI-GOD BEAM" : p > 0.75f ? "LOCKED - BREAK LINE OF SIGHT" : "ANTI-GOD WEAPON CHARGING";
                m_IncomingText.style.color = Color.Lerp(Color.white, new Color(1f, 0.3f, 0.25f), 0.5f + 0.5f * Mathf.Sin(Time.time * (6f + 18f * p)));
                m_IncomingFill.style.width = new Length(p * 100f, LengthUnit.Percent);
                if (cam != null && m_Root.panel != null)
                {
                    // arrow at the screen edge pointing at the weapon when it's off screen
                    var sp = cam.WorldToScreenPoint(OV.IncomingFrom);
                    var c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                    var d = new Vector2(sp.x, sp.y) - c;
                    if (sp.z < 0f) d = -d;
                    var onScreen = sp.z > 0f && sp.x > 0 && sp.x < Screen.width && sp.y > 0 && sp.y < Screen.height;
                    if (!onScreen && d.sqrMagnitude > 1f)
                    {
                        d.Normalize();
                        var edge = c + d * Mathf.Min(Screen.width, Screen.height) * 0.42f;
                        var pp = RuntimePanelUtils.ScreenToPanel(m_Root.panel, new Vector2(edge.x, Screen.height - edge.y));
                        m_IncomingArrow.style.display = DisplayStyle.Flex;
                        m_IncomingArrow.style.left = pp.x - 17; m_IncomingArrow.style.top = pp.y - 17;
                        m_IncomingArrow.style.rotate = new Rotate(new Angle(Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg - 45f, AngleUnit.Degree));
                    }
                }
            }

            m_Suppressed.text = OV.Suppressed ? "SURGE SUPPRESSED" : "";

            // small health bars over recently hit units
            var units = OV.DamagedUnits;
            while (m_UnitBars.Count < units.Count)
            {
                var back = new VisualElement { pickingMode = PickingMode.Ignore };
                back.style.position = Position.Absolute;
                back.style.width = 60; back.style.height = 6;
                back.style.backgroundColor = new Color(0, 0, 0, 0.6f);
                var fill = new VisualElement { pickingMode = PickingMode.Ignore };
                fill.style.height = 6;
                fill.style.backgroundColor = new Color(1f, 0.8f, 0.3f);
                back.Add(fill);
                m_Root.Add(back);
                m_UnitBars.Add((back, fill));
            }
            for (var i = 0; i < m_UnitBars.Count; i++)
            {
                var (back, fill) = m_UnitBars[i];
                if (i >= units.Count || cam == null || m_Root.panel == null) { back.style.display = DisplayStyle.None; continue; }
                var sp = cam.WorldToScreenPoint(units[i].pos);
                if (sp.z <= 0f) { back.style.display = DisplayStyle.None; continue; }
                var pp = RuntimePanelUtils.ScreenToPanel(m_Root.panel, new Vector2(sp.x, Screen.height - sp.y));
                back.style.display = DisplayStyle.Flex;
                back.style.left = pp.x - 30; back.style.top = pp.y - 3;
                fill.style.width = new Length(units[i].health * 100f, LengthUnit.Percent);
            }
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
                    // milestone 3B
                    if (kb.pageUpKey.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.HeatUp);
                    if (kb.pageDownKey.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.HeatDown);
                    if (kb.homeKey.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.SpawnEachUnit);
                    if (kb.endKey.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.ClearUnits);
                    if (kb.insertKey.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.RivalToggle);
                    if (kb.deleteKey.wasPressedThisFrame) DebugCommands.Send(DebugCommandRequest.RivalDifficulty);
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
                                     $"Broken chunks {DebrisSystem.BrokenChunks} / {DebrisSystem.ChunkCount}\nF4 fill surge  F5 reset  F6 dummy guard  F7 tier  F8 dummy ritual  F9 destroy  [ ] alignment\nPgUp/PgDn heat  Home spawn units  End clear units\nIns rival on/off  Del rival difficulty";
            UpdateOppositionHud();
            UpdateDistrictHud();
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
