using ToLive.CameraRig;
using ToLive.Input;
using ToLive.Net;
using ToLive.Player;
using Unity.Entities;
using Unity.NetCode;
using Unity.Scenes;
using UnityEngine;
using UnityEngine.UIElements;

namespace ToLive.UI
{
    /// <summary>
    /// Main menu (Host / Join / Quit), pause menu and the in-game overlay (gear, speed, ping),
    /// built with UI Toolkit (Unity's retained UI, roughly comparable to UMG). The layout is built
    /// in code here to keep Milestone 1 small; it can move to a UXML file later.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class GameUI : MonoBehaviour
    {
        [Tooltip("The SubScene holding the district, spawner and flight config.")]
        public SubScene GameplayScene;

        VisualElement m_Menu, m_Hud;
        TextField m_Address, m_Port;
        Label m_Status, m_Gear, m_Speed, m_Ping, m_Title;
        Button m_HostButton, m_JoinButton, m_ResumeButton, m_LeaveButton, m_ResetButton, m_DestroyButton;
        public static bool MenuOpen { get; private set; } = true;
        bool m_MenuOpen = true;
        float m_ConnectStartedAt = -1f;
        World m_World;
        EntityQuery m_ConnectionQuery, m_AckQuery;

        void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1;
            BuildMenu(root);
            BuildHud(root);
            ShowMenu(true);
        }

        void BuildMenu(VisualElement root)
        {
            m_Menu = new VisualElement();
            m_Menu.style.position = Position.Absolute;
            m_Menu.style.left = 0; m_Menu.style.right = 0; m_Menu.style.top = 0; m_Menu.style.bottom = 0;
            m_Menu.style.backgroundColor = new Color(0.05f, 0.05f, 0.07f, 0.85f);
            m_Menu.style.alignItems = Align.Center;
            m_Menu.style.justifyContent = Justify.Center;
            root.Add(m_Menu);

            var panel = new VisualElement();
            panel.style.width = 420;
            m_Menu.Add(panel);

            m_Title = new Label("TO LIVE");
            m_Title.style.fontSize = 56;
            m_Title.style.unityFontStyleAndWeight = FontStyle.Bold;
            m_Title.style.color = Color.white;
            m_Title.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_Title.style.marginBottom = 24;
            panel.Add(m_Title);

            m_ResumeButton = MakeButton(panel, "Resume", () => ShowMenu(false));
            m_ResetButton = MakeButton(panel, "Reset dummy + rebuild building (F5)", () => { DebugCommands.Send(ToLive.Combat.DebugCommandRequest.ResetDummyAndBuilding); ShowMenu(false); });
            m_DestroyButton = MakeButton(panel, "Stress test: destroy building (F9)", () => { DebugCommands.Send(ToLive.Combat.DebugCommandRequest.DestroyBuilding); ShowMenu(false); });
            m_LeaveButton = MakeButton(panel, "Leave game", LeaveGame);
            m_HostButton = MakeButton(panel, "Host", Host);

            m_Address = new TextField("Host IP") { value = "127.0.0.1" };
            m_Port = new TextField("Port") { value = NetSession.DefaultPort.ToString() };
            StyleField(m_Address); StyleField(m_Port);
            panel.Add(m_Address);
            panel.Add(m_Port);
            m_JoinButton = MakeButton(panel, "Join", Join);
            MakeButton(panel, "Quit", Quit);

            m_Status = new Label("");
            m_Status.style.color = new Color(1f, 0.8f, 0.4f);
            m_Status.style.marginTop = 12;
            m_Status.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_Status.style.whiteSpace = WhiteSpace.Normal;
            panel.Add(m_Status);
        }

        void BuildHud(VisualElement root)
        {
            m_Hud = new VisualElement();
            m_Hud.style.position = Position.Absolute;
            m_Hud.style.left = 24; m_Hud.style.bottom = 24;
            m_Hud.style.paddingLeft = 12; m_Hud.style.paddingRight = 12; m_Hud.style.paddingTop = 8; m_Hud.style.paddingBottom = 8;
            m_Hud.style.backgroundColor = new Color(0f, 0f, 0f, 0.45f);
            root.Add(m_Hud);
            m_Gear = HudLabel(m_Hud, 28);
            m_Speed = HudLabel(m_Hud, 22);
            m_Ping = HudLabel(m_Hud, 16);
        }

        static Label HudLabel(VisualElement parent, int size)
        {
            var l = new Label();
            l.style.color = Color.white;
            l.style.fontSize = size;
            parent.Add(l);
            return l;
        }

        static Button MakeButton(VisualElement parent, string text, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.height = 44;
            b.style.fontSize = 20;
            b.style.marginTop = 6;
            parent.Add(b);
            return b;
        }

        static void StyleField(TextField f)
        {
            f.style.fontSize = 18;
            f.style.marginTop = 6;
            f.labelElement.style.color = Color.white;
            f.labelElement.style.minWidth = 90;
        }

        void ShowMenu(bool open)
        {
            m_MenuOpen = open;
            MenuOpen = open;
            var inSession = NetSession.IsActive;
            StyleEnum<DisplayStyle> inSessionStyle(bool _) => inSession ? DisplayStyle.Flex : DisplayStyle.None;
            InputReader.LightPresses = InputReader.DashPresses = InputReader.LockOnPresses = 0;
            if (m_ResetButton != null) m_ResetButton.style.display = inSessionStyle(open);
            if (m_DestroyButton != null) m_DestroyButton.style.display = inSessionStyle(open);
            m_Menu.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            m_Hud.style.display = inSession && !open ? DisplayStyle.Flex : DisplayStyle.None;
            m_ResumeButton.style.display = inSession ? DisplayStyle.Flex : DisplayStyle.None;
            m_LeaveButton.style.display = inSession ? DisplayStyle.Flex : DisplayStyle.None;
            m_HostButton.style.display = inSession ? DisplayStyle.None : DisplayStyle.Flex;
            m_JoinButton.style.display = inSession ? DisplayStyle.None : DisplayStyle.Flex;
            m_Address.style.display = inSession ? DisplayStyle.None : DisplayStyle.Flex;
            m_Port.style.display = inSession ? DisplayStyle.None : DisplayStyle.Flex;

            GameplayInputGate.Blocked = open || !inSession;
            UnityEngine.Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            UnityEngine.Cursor.visible = open;
        }

        bool TryGetPort(out ushort port)
        {
            if (ushort.TryParse(m_Port.value.Trim(), out port) && port > 0) return true;
            m_Status.text = "Port must be a number between 1 and 65535.";
            return false;
        }

        public void Host()
        {
            if (!TryGetPort(out var port)) return;
            NetSession.Host(port, GameplayScene);
            m_Status.text = $"Hosting on port {port}…";
            m_ConnectStartedAt = Time.realtimeSinceStartup;
            ShowMenu(false);
        }

        public void JoinAddress(string address)
        {
            m_Address.value = address;
            Join();
        }

        public void Join()
        {
            if (!TryGetPort(out var port)) return;
            if (!NetSession.Join(m_Address.value, port, GameplayScene, out var error))
            {
                m_Status.text = error;
                return;
            }
            m_Status.text = $"Connecting to {m_Address.value}:{port}…";
            m_ConnectStartedAt = Time.realtimeSinceStartup;
            ShowMenu(false);
        }

        public void LeaveGame()
        {
            NetSession.Leave();
            m_World = null;
            m_ConnectStartedAt = -1f;
            m_Status.text = "";
            ShowMenu(true);
        }

        static void Quit()
        {
            NetSession.Leave();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        void Update()
        {
            if (InputReader.MenuPressedThisFrame && NetSession.IsActive)
                ShowMenu(!m_MenuOpen);

            if (!NetSession.IsActive)
                return;

            // Detect failed / dropped connections and bounce back to the menu.
            var client = ClientServerBootstrap.ClientWorld;
            if (client != null && m_ConnectStartedAt >= 0f && Time.realtimeSinceStartup - m_ConnectStartedAt > 1f)
            {
                EnsureQueries(client);
                if (m_ConnectionQuery.IsEmpty)
                {
                    var msg = "Disconnected (could not reach the host, or the host left).";
                    LeaveGame();
                    m_Status.text = msg;
                    return;
                }
            }

            if (m_MenuOpen) return;
            m_Gear.text = FlightCameraRig.HasLocalPlayer ? FlightGear.Name(FlightCameraRig.LocalGear) : "CONNECTING…";
            var speed = FlightCameraRig.LocalSpeed;
            m_Speed.text = $"{speed:0} m/s" + (speed >= 343f ? "  (SUPERSONIC)" : "");
            m_Ping.text = $"Ping {GetPingMs():0} ms";
        }

        void EnsureQueries(World client)
        {
            if (m_World == client) return;
            m_World = client;
            m_ConnectionQuery = client.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
            m_AckQuery = client.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkSnapshotAck>());
        }

        float GetPingMs()
        {
            var client = ClientServerBootstrap.ClientWorld;
            if (client == null) return 0f;
            EnsureQueries(client);
            if (m_AckQuery.CalculateEntityCount() != 1) return 0f;
            m_AckQuery.CompleteDependency();
            return m_AckQuery.GetSingleton<NetworkSnapshotAck>().EstimatedRTT;
        }

        void OnApplicationQuit()
        {
            NetSession.Leave();
        }
    }
}
