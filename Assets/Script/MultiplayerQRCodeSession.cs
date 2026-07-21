using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UI;

public class MultiplayerQRCodeSession : MonoBehaviour
{
    private const string SpawnRequestMessage = "QRCodeSpawnRequest";
    private const string SpawnBroadcastMessage = "QRCodeSpawnBroadcast";
    private const string BattleStateBroadcastMessage = "QRCodeBattleStateBroadcast";
    private const ushort DefaultPort = 7777;

    private static MultiplayerQRCodeSession s_instance;

    [SerializeField] private string connectAddress = "127.0.0.1";
    [SerializeField] private ushort port = DefaultPort;
    [SerializeField] private bool showRuntimeGui = true;
    [SerializeField] private bool showVrControlPanel = true;
    [SerializeField] private float vrPanelDistance = 1.35f;
    [SerializeField] private float vrPanelHeightOffset = -0.12f;
    [SerializeField] private float gazeSelectSeconds = 1.1f;
    [SerializeField] private float battleStateBroadcastInterval = 0.1f;

    private NetworkManager networkManager;
    private UnityTransport transport;
    private bool handlersRegistered;
    private bool connectionHandlersRegistered;
    private float nextBattleStateBroadcastTime;
    private Canvas vrCanvas;
    private RectTransform vrPanel;
    private Text vrStatusText;
    private VrPanelButton gazedButton;
    private float gazedButtonSince = -1f;
    private readonly List<VrPanelButton> vrButtons = new List<VrPanelButton>();
    private readonly List<SpawnRecord> serverSpawnRecords = new List<SpawnRecord>();

    private class VrPanelButton
    {
        public RectTransform RectTransform;
        public Image Background;
        public Text Label;
        public string LabelText;
        public Action Click;
    }

    private struct SpawnRecord
    {
        public string SpawnId;
        public ulong OwnerClientId;
        public string Key;
        public Vector3 Position;
        public Quaternion Rotation;
        public int Health;
    }

    public static MultiplayerQRCodeSession Instance
    {
        get
        {
            if (s_instance)
            {
                return s_instance;
            }

            var existing = FindAnyObjectByType<MultiplayerQRCodeSession>();
            if (existing)
            {
                s_instance = existing;
                return s_instance;
            }

            var obj = new GameObject(nameof(MultiplayerQRCodeSession));
            DontDestroyOnLoad(obj);
            s_instance = obj.AddComponent<MultiplayerQRCodeSession>();
            return s_instance;
        }
    }

    public bool IsNetworkActive => networkManager && networkManager.IsListening;

    public bool IsServer => networkManager && networkManager.IsServer;

    public ulong LocalClientId => networkManager ? networkManager.LocalClientId : 0UL;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateSessionOnLoad()
    {
        _ = Instance;
    }

    private void Awake()
    {
        if (s_instance && s_instance != this)
        {
            Destroy(gameObject);
            return;
        }

        s_instance = this;
        DontDestroyOnLoad(gameObject);
        EnsureNetworkManager();
    }

    private void Update()
    {
        RegisterMessageHandlersIfReady();
        BroadcastBattleStateIfNeeded();
        UpdateVrControlPanel();
    }

    public void StartHost()
    {
        EnsureNetworkManager();
        ConfigureTransport("0.0.0.0");
        networkManager.StartHost();
        RegisterMessageHandlersIfReady();
        Debug.Log("<<< Multiplayer host started. >>>");
    }

    public void StartClient()
    {
        EnsureNetworkManager();
        ConfigureTransport(connectAddress);
        networkManager.StartClient();
        RegisterMessageHandlersIfReady();
        Debug.Log($"<<< Multiplayer client connecting to {connectAddress}:{port}. >>>");
    }

    public void Shutdown()
    {
        if (networkManager && networkManager.IsListening)
        {
            networkManager.Shutdown();
        }

        handlersRegistered = false;
        nextBattleStateBroadcastTime = 0f;
        serverSpawnRecords.Clear();
    }

    public void RequestSpawnModel(string key, Vector3 position, Quaternion rotation, int health)
    {
        if (!IsNetworkActive)
        {
            return;
        }

        if (IsServer)
        {
            SpawnFromServer(LocalClientId, key, position, rotation, health);
            return;
        }

        using var writer = new FastBufferWriter(512, Allocator.Temp);
        writer.WriteValueSafe(key ?? string.Empty);
        writer.WriteValueSafe(position);
        writer.WriteValueSafe(rotation);
        writer.WriteValueSafe(health);
        networkManager.CustomMessagingManager.SendNamedMessage(
            SpawnRequestMessage,
            NetworkManager.ServerClientId,
            writer,
            NetworkDelivery.ReliableSequenced);
    }

    private void EnsureNetworkManager()
    {
        if (networkManager)
        {
            return;
        }

        networkManager = FindAnyObjectByType<NetworkManager>();
        if (!networkManager)
        {
            var obj = new GameObject("NetworkManager");
            DontDestroyOnLoad(obj);
            networkManager = obj.AddComponent<NetworkManager>();
        }

        transport = networkManager.GetComponent<UnityTransport>();
        if (!transport)
        {
            transport = networkManager.gameObject.AddComponent<UnityTransport>();
        }

        networkManager.NetworkConfig.NetworkTransport = transport;
        networkManager.NetworkConfig.EnableSceneManagement = false;
        networkManager.NetworkConfig.ConnectionApproval = false;

        if (!connectionHandlersRegistered)
        {
            networkManager.OnClientConnectedCallback += OnClientConnected;
            connectionHandlersRegistered = true;
        }
    }

    private void ConfigureTransport(string address)
    {
        if (!transport)
        {
            return;
        }

        transport.SetConnectionData(address, port);
    }

    private void RegisterMessageHandlersIfReady()
    {
        if (handlersRegistered || !networkManager || !networkManager.IsListening || networkManager.CustomMessagingManager == null)
        {
            return;
        }

        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(SpawnRequestMessage, OnSpawnRequestReceived);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(SpawnBroadcastMessage, OnSpawnBroadcastReceived);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(BattleStateBroadcastMessage, OnBattleStateBroadcastReceived);
        handlersRegistered = true;
    }

    private void OnSpawnRequestReceived(ulong senderClientId, FastBufferReader reader)
    {
        if (!IsServer)
        {
            return;
        }

        reader.ReadValueSafe(out string key);
        reader.ReadValueSafe(out Vector3 position);
        reader.ReadValueSafe(out Quaternion rotation);
        reader.ReadValueSafe(out int health);
        SpawnFromServer(senderClientId, key, position, rotation, health);
    }

    private void SpawnFromServer(ulong ownerClientId, string key, Vector3 position, Quaternion rotation, int health)
    {
        var spawnId = $"{ownerClientId}:{key}";
        if (serverSpawnRecords.Any(record => string.Equals(record.SpawnId, spawnId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var record = new SpawnRecord
        {
            SpawnId = spawnId,
            OwnerClientId = ownerClientId,
            Key = key,
            Position = position,
            Rotation = rotation,
            Health = health
        };
        serverSpawnRecords.Add(record);
        BroadcastSpawn(record);
    }

    private void BroadcastSpawn(SpawnRecord record)
    {
        ApplySpawn(record.SpawnId, record.OwnerClientId, record.Key, record.Position, record.Rotation, record.Health);

        foreach (var clientId in networkManager.ConnectedClientsIds)
        {
            if (clientId == networkManager.LocalClientId)
            {
                continue;
            }

            SendSpawnRecord(clientId, record);
        }
    }

    private void SendSpawnRecord(ulong clientId, SpawnRecord record)
    {
        using var writer = new FastBufferWriter(512, Allocator.Temp);
        writer.WriteValueSafe(record.SpawnId ?? string.Empty);
        writer.WriteValueSafe(record.OwnerClientId);
        writer.WriteValueSafe(record.Key ?? string.Empty);
        writer.WriteValueSafe(record.Position);
        writer.WriteValueSafe(record.Rotation);
        writer.WriteValueSafe(record.Health);
        networkManager.CustomMessagingManager.SendNamedMessage(
            SpawnBroadcastMessage,
            clientId,
            writer,
            NetworkDelivery.ReliableSequenced);
    }

    private void OnClientConnected(ulong clientId)
    {
        if (!IsServer || clientId == networkManager.LocalClientId)
        {
            return;
        }

        foreach (var record in serverSpawnRecords)
        {
            SendSpawnRecord(clientId, record);
        }
    }

    private void OnSpawnBroadcastReceived(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out string spawnId);
        reader.ReadValueSafe(out ulong ownerClientId);
        reader.ReadValueSafe(out string key);
        reader.ReadValueSafe(out Vector3 position);
        reader.ReadValueSafe(out Quaternion rotation);
        reader.ReadValueSafe(out int health);
        ApplySpawn(spawnId, ownerClientId, key, position, rotation, health);
    }

    private static void ApplySpawn(string spawnId, ulong ownerClientId, string key, Vector3 position, Quaternion rotation, int health)
    {
        var manager = FindAnyObjectByType<QRCodeManager>();
        if (!manager)
        {
            Debug.LogWarning("<<< No QRCodeManager found to apply network spawn. >>>");
            return;
        }

        manager.SpawnNetworkedModel(spawnId, key, position, rotation, health, ownerClientId);
    }

    private void BroadcastBattleStateIfNeeded()
    {
        if (!IsServer || !networkManager || !networkManager.IsListening || Time.time < nextBattleStateBroadcastTime)
        {
            return;
        }

        nextBattleStateBroadcastTime = Time.time + Mathf.Max(0.02f, battleStateBroadcastInterval);

        var manager = FindAnyObjectByType<QRCodeManager>();
        if (!manager)
        {
            return;
        }

        var snapshots = manager.GetNetworkBattleSnapshots();
        if (snapshots.Count == 0)
        {
            return;
        }

        foreach (var clientId in networkManager.ConnectedClientsIds)
        {
            if (clientId == networkManager.LocalClientId)
            {
                continue;
            }

            SendBattleState(clientId, snapshots);
        }
    }

    private void SendBattleState(ulong clientId, List<QRCodeManager.NetworkBattleSnapshot> snapshots)
    {
        using var writer = new FastBufferWriter(8192, Allocator.Temp);
        writer.WriteValueSafe(snapshots.Count);

        foreach (var snapshot in snapshots)
        {
            writer.WriteValueSafe(snapshot.SpawnId ?? string.Empty);
            writer.WriteValueSafe(snapshot.Position);
            writer.WriteValueSafe(snapshot.Rotation);
            writer.WriteValueSafe(snapshot.CurrentHealth);
            writer.WriteValueSafe(snapshot.MaxHealth);
            writer.WriteValueSafe(snapshot.HasLost);
            writer.WriteValueSafe(snapshot.IsAttacking);
        }

        networkManager.CustomMessagingManager.SendNamedMessage(
            BattleStateBroadcastMessage,
            clientId,
            writer,
            NetworkDelivery.UnreliableSequenced);
    }

    private void OnBattleStateBroadcastReceived(ulong senderClientId, FastBufferReader reader)
    {
        var manager = FindAnyObjectByType<QRCodeManager>();
        if (!manager)
        {
            return;
        }

        reader.ReadValueSafe(out int count);
        count = Mathf.Clamp(count, 0, 64);

        for (var i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out string spawnId);
            reader.ReadValueSafe(out Vector3 position);
            reader.ReadValueSafe(out Quaternion rotation);
            reader.ReadValueSafe(out float currentHealth);
            reader.ReadValueSafe(out float maxHealth);
            reader.ReadValueSafe(out bool hasLost);
            reader.ReadValueSafe(out bool isAttacking);
            manager.ApplyNetworkBattleSnapshot(
                spawnId,
                position,
                rotation,
                currentHealth,
                maxHealth,
                hasLost,
                isAttacking);
        }
    }

    private void UpdateVrControlPanel()
    {
        if (!showVrControlPanel)
        {
            if (vrCanvas)
            {
                vrCanvas.gameObject.SetActive(false);
            }
            return;
        }

        var camera = Camera.main;
        if (!camera)
        {
            return;
        }

        EnsureVrControlPanel(camera.transform);
        UpdateVrPanelStatus();
        UpdateGazeSelection(camera);
    }

    private void EnsureVrControlPanel(Transform cameraTransform)
    {
        if (vrCanvas)
        {
            if (!vrCanvas.gameObject.activeSelf)
            {
                vrCanvas.gameObject.SetActive(true);
            }

            var toPanel = vrCanvas.transform.position - cameraTransform.position;
            if (Vector3.Dot(cameraTransform.forward, toPanel.normalized) < 0.35f ||
                toPanel.magnitude > vrPanelDistance * 2.2f ||
                toPanel.magnitude < vrPanelDistance * 0.45f)
            {
                PlaceVrPanel(cameraTransform);
            }
            return;
        }

        var root = new GameObject("Multiplayer QR VR Panel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        DontDestroyOnLoad(root);
        vrCanvas = root.GetComponent<Canvas>();
        vrCanvas.renderMode = RenderMode.WorldSpace;
        vrCanvas.sortingOrder = 200;

        var scaler = root.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 12f;

        vrPanel = root.GetComponent<RectTransform>();
        vrPanel.sizeDelta = new Vector2(440f, 330f);

        var panelImage = root.AddComponent<Image>();
        panelImage.color = new Color(0.015f, 0.02f, 0.035f, 0.88f);

        CreateVrText(vrPanel, "Title", "Multiplayer QR Session", new Vector2(0f, 126f), 26, FontStyle.Bold);
        vrStatusText = CreateVrText(vrPanel, "Status", string.Empty, new Vector2(0f, 88f), 17, FontStyle.Normal);
        CreateVrText(vrPanel, "Hint", "Look at a button to select", new Vector2(0f, -130f), 15, FontStyle.Normal);

        vrButtons.Clear();
        vrButtons.Add(CreateVrButton(vrPanel, "Start Host", new Vector2(0f, 36f), StartHost));
        vrButtons.Add(CreateVrButton(vrPanel, "Start Client", new Vector2(0f, -28f), StartClient));
        vrButtons.Add(CreateVrButton(vrPanel, "Shutdown", new Vector2(0f, -92f), Shutdown));

        PlaceVrPanel(cameraTransform);
    }

    private void PlaceVrPanel(Transform cameraTransform)
    {
        if (!vrCanvas)
        {
            return;
        }

        var position = cameraTransform.position +
                       cameraTransform.forward * Mathf.Max(0.6f, vrPanelDistance) +
                       Vector3.up * vrPanelHeightOffset;
        vrCanvas.transform.position = position;
        vrCanvas.transform.rotation = Quaternion.LookRotation(position - cameraTransform.position, Vector3.up);
        vrCanvas.transform.localScale = Vector3.one * 0.0025f;
    }

    private void UpdateVrPanelStatus()
    {
        if (!vrStatusText)
        {
            return;
        }

        if (networkManager && networkManager.IsListening)
        {
            var mode = networkManager.IsHost ? "Host" : networkManager.IsServer ? "Server" : "Client";
            vrStatusText.text = $"Online: {mode} / ClientId {networkManager.LocalClientId}";
            return;
        }

        vrStatusText.text = $"Offline / Client target: {connectAddress}:{port}";
    }

    private void UpdateGazeSelection(Camera camera)
    {
        var hitButton = GetGazedButton(camera);
        if (hitButton == null)
        {
            gazedButton = null;
            gazedButtonSince = -1f;
            ResetVrButtonHighlights(null, 0f);
            return;
        }

        if (gazedButton != hitButton)
        {
            gazedButton = hitButton;
            gazedButtonSince = Time.time;
        }

        var holdDuration = Mathf.Max(0.1f, gazeSelectSeconds);
        var progress = Mathf.Clamp01((Time.time - gazedButtonSince) / holdDuration);
        ResetVrButtonHighlights(hitButton, progress);

        if (progress < 1f)
        {
            return;
        }

        gazedButtonSince = Time.time + 0.45f;
        hitButton.Click?.Invoke();
    }

    private VrPanelButton GetGazedButton(Camera camera)
    {
        if (!vrPanel)
        {
            return null;
        }

        var ray = new Ray(camera.transform.position, camera.transform.forward);
        var plane = new Plane(vrCanvas.transform.forward, vrCanvas.transform.position);
        if (!plane.Raycast(ray, out var distance))
        {
            return null;
        }

        var hitPoint = ray.GetPoint(distance);
        foreach (var button in vrButtons)
        {
            if (!button.RectTransform)
            {
                continue;
            }

            var localPoint = button.RectTransform.InverseTransformPoint(hitPoint);
            if (button.RectTransform.rect.Contains(localPoint))
            {
                return button;
            }
        }

        return null;
    }

    private void ResetVrButtonHighlights(VrPanelButton activeButton, float progress)
    {
        foreach (var button in vrButtons)
        {
            if (button.Background)
            {
                button.Background.color = button == activeButton
                    ? Color.Lerp(new Color(0.09f, 0.36f, 0.78f, 0.95f), new Color(0.05f, 0.9f, 1f, 0.95f), progress)
                    : new Color(0.08f, 0.12f, 0.2f, 0.95f);
            }

            if (button.Label)
            {
                button.Label.text = button == activeButton && progress > 0f
                    ? $"{button.LabelText} {Mathf.CeilToInt((1f - progress) * gazeSelectSeconds + 0.01f)}"
                    : button.LabelText;
            }
        }
    }

    private static VrPanelButton CreateVrButton(RectTransform parent, string text, Vector2 anchoredPosition, Action click)
    {
        var obj = new GameObject(text, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        obj.transform.SetParent(parent, false);

        var rectTransform = obj.GetComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.sizeDelta = new Vector2(310f, 48f);
        rectTransform.anchoredPosition = anchoredPosition;

        var image = obj.GetComponent<Image>();
        image.color = new Color(0.08f, 0.12f, 0.2f, 0.95f);

        var label = CreateVrText(rectTransform, "Label", text, Vector2.zero, 19, FontStyle.Bold);
        label.alignment = TextAnchor.MiddleCenter;

        return new VrPanelButton
        {
            RectTransform = rectTransform,
            Background = image,
            Label = label,
            LabelText = text,
            Click = click
        };
    }

    private static Text CreateVrText(RectTransform parent, string name, string text, Vector2 anchoredPosition, int fontSize, FontStyle fontStyle)
    {
        var obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        obj.transform.SetParent(parent, false);

        var rectTransform = obj.GetComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.sizeDelta = new Vector2(390f, 34f);
        rectTransform.anchoredPosition = anchoredPosition;

        var label = obj.GetComponent<Text>();
        label.font = GetBuiltInFont();
        label.text = text;
        label.alignment = TextAnchor.MiddleCenter;
        label.fontSize = fontSize;
        label.fontStyle = fontStyle;
        label.color = new Color(0.93f, 0.98f, 1f, 1f);
        label.raycastTarget = false;
        return label;
    }

    private static Font GetBuiltInFont()
    {
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (!font)
        {
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        return font;
    }

    private void OnGUI()
    {
        if (!showRuntimeGui)
        {
            return;
        }

        const int width = 360;
        GUILayout.BeginArea(new Rect(20, 20, width, 220), GUI.skin.box);
        GUILayout.Label("Multiplayer QR Session");
        connectAddress = GUILayout.TextField(connectAddress);
        if (GUILayout.Button("Start Host"))
        {
            StartHost();
        }
        if (GUILayout.Button("Start Client"))
        {
            StartClient();
        }
        if (GUILayout.Button("Shutdown"))
        {
            Shutdown();
        }

        var status = networkManager && networkManager.IsListening
            ? $"Online - {(networkManager.IsHost ? "Host" : networkManager.IsServer ? "Server" : "Client")} / ClientId {networkManager.LocalClientId}"
            : "Offline";
        GUILayout.Label(status);
        GUILayout.EndArea();
    }
}
