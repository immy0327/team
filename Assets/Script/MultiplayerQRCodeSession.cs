using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

public class MultiplayerQRCodeSession : MonoBehaviour
{
    private const string SpawnRequestMessage = "QRCodeSpawnRequest";
    private const string SpawnBroadcastMessage = "QRCodeSpawnBroadcast";
    private const string BattleStateBroadcastMessage = "QRCodeBattleStateBroadcast";
    private const ushort DefaultPort = 7777;

    private static MultiplayerQRCodeSession s_instance;

    [SerializeField] private string connectAddress = "127.0.0.1";
    [SerializeField] private ushort port = DefaultPort;
    [SerializeField] private bool showRuntimeGui = false;
    [SerializeField] private bool showVrControlPanel = false;
    [SerializeField] private bool enableControllerShortcuts = true;
    [SerializeField] private float controllerShortcutCooldown = 0.5f;
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
    private Camera vrPanelCamera;
    private VrPanelButton gazedButton;
    private float gazedButtonSince = -1f;
    private GameObject fallbackPanelRoot;
    private TextMesh fallbackStatusText;
    private VrTextButton gazedTextButton;
    private float gazedTextButtonSince = -1f;
    private bool rightPrimaryWasPressed;
    private bool rightSecondaryWasPressed;
    private bool leftSecondaryWasPressed;
    private float nextControllerShortcutTime;
    private readonly List<VrPanelButton> vrButtons = new List<VrPanelButton>();
    private readonly List<VrTextButton> fallbackButtons = new List<VrTextButton>();
    private readonly List<SpawnRecord> serverSpawnRecords = new List<SpawnRecord>();

    private class VrPanelButton
    {
        public RectTransform RectTransform;
        public Image Background;
        public Text Label;
        public string LabelText;
        public Action Click;
    }

    private class VrTextButton
    {
        public GameObject Root;
        public Renderer Background;
        public TextMesh Label;
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
        Debug.Log("<<< MultiplayerQRCodeSession runtime bootstrap v20260722-01 >>>");
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
        Debug.Log("<<< MultiplayerQRCodeSession awake v20260722-01 >>>");
    }

    private void Update()
    {
        HandleControllerShortcuts();
        RegisterMessageHandlersIfReady();
        BroadcastBattleStateIfNeeded();
        UpdateVrControlPanel();
    }

    private void HandleControllerShortcuts()
    {
        if (!enableControllerShortcuts)
        {
            return;
        }

        var rightHand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        var leftHand = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);

        var rightPrimaryPressed = IsButtonPressed(rightHand, CommonUsages.primaryButton);
        var rightSecondaryPressed = IsButtonPressed(rightHand, CommonUsages.secondaryButton);
        var leftSecondaryPressed = IsButtonPressed(leftHand, CommonUsages.secondaryButton);

        if (WasPressedThisFrame(rightPrimaryPressed, ref rightPrimaryWasPressed))
        {
            RunControllerShortcut(StartHost, "A / Start Host");
        }

        if (WasPressedThisFrame(rightSecondaryPressed, ref rightSecondaryWasPressed))
        {
            RunControllerShortcut(StartClient, "B / Start Client");
        }

        if (WasPressedThisFrame(leftSecondaryPressed, ref leftSecondaryWasPressed))
        {
            RunControllerShortcut(Shutdown, "Y / Shutdown");
        }
    }

    private static bool IsButtonPressed(InputDevice device, InputFeatureUsage<bool> button)
    {
        return device.isValid && device.TryGetFeatureValue(button, out var pressed) && pressed;
    }

    private static bool WasPressedThisFrame(bool pressed, ref bool wasPressed)
    {
        var triggered = pressed && !wasPressed;
        wasPressed = pressed;
        return triggered;
    }

    private void RunControllerShortcut(Action action, string label)
    {
        if (Time.unscaledTime < nextControllerShortcutTime)
        {
            return;
        }

        nextControllerShortcutTime = Time.unscaledTime + Mathf.Max(0.1f, controllerShortcutCooldown);
        Debug.Log($"<<< Controller shortcut: {label} >>>");
        action?.Invoke();
    }

    public void StartHost()
    {
        EnsureNetworkManager();
        if (networkManager.IsListening)
        {
            Debug.Log("<<< Multiplayer already running; StartHost ignored. >>>");
            return;
        }

        ConfigureTransport("0.0.0.0");
        networkManager.StartHost();
        RegisterMessageHandlersIfReady();
        Debug.Log("<<< Multiplayer host started. >>>");
    }

    public void StartClient()
    {
        EnsureNetworkManager();
        if (networkManager.IsListening)
        {
            Debug.Log("<<< Multiplayer already running; StartClient ignored. >>>");
            return;
        }

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

        var camera = GetVrPanelCamera();
        if (!camera)
        {
            if (Time.frameCount % 120 == 0)
            {
                Debug.LogWarning("<<< Multiplayer QR panel waiting for an active camera. >>>");
            }
            return;
        }

        EnsureVrControlPanel(camera.transform);
        EnsureFallbackControlPanel(camera.transform);
        UpdateVrPanelStatus();
        UpdateGazeSelection(camera);
        UpdateFallbackGazeSelection(camera);
    }

    private Camera GetVrPanelCamera()
    {
        if (vrPanelCamera && vrPanelCamera.isActiveAndEnabled)
        {
            return vrPanelCamera;
        }

        if (Camera.main && Camera.main.isActiveAndEnabled)
        {
            vrPanelCamera = Camera.main;
            return vrPanelCamera;
        }

        var cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
        vrPanelCamera = cameras
            .Where(camera => camera && camera.isActiveAndEnabled)
            .OrderByDescending(camera => camera.depth)
            .FirstOrDefault();
        return vrPanelCamera;
    }

    private void EnsureVrControlPanel(Transform cameraTransform)
    {
        if (vrCanvas)
        {
            if (!vrCanvas.gameObject.activeSelf)
            {
                vrCanvas.gameObject.SetActive(true);
            }

            PlaceVrPanel(cameraTransform);
            return;
        }

        var root = new GameObject("Multiplayer QR VR Panel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        vrCanvas = root.GetComponent<Canvas>();
        vrCanvas.renderMode = RenderMode.WorldSpace;
        vrCanvas.sortingOrder = 200;
        vrCanvas.worldCamera = cameraTransform.GetComponent<Camera>();

        var scaler = root.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 12f;

        vrPanel = root.GetComponent<RectTransform>();
        vrPanel.sizeDelta = new Vector2(440f, 330f);

        var panelImage = root.AddComponent<Image>();
        panelImage.color = new Color(0.015f, 0.02f, 0.035f, 0.88f);

        CreateVrText(vrPanel, "Title", "Multiplayer QR Session", new Vector2(0f, 126f), 26, FontStyle.Bold);
        vrStatusText = CreateVrText(vrPanel, "Status", string.Empty, new Vector2(0f, 88f), 17, FontStyle.Normal);
        CreateVrText(vrPanel, "Hint", "Look at a button for 1 second", new Vector2(0f, -130f), 15, FontStyle.Normal);

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

        if (vrCanvas.transform.parent != cameraTransform)
        {
            vrCanvas.transform.SetParent(cameraTransform, false);
        }

        vrCanvas.transform.localPosition = new Vector3(0f, vrPanelHeightOffset, Mathf.Max(0.45f, vrPanelDistance));
        vrCanvas.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        vrCanvas.transform.localScale = Vector3.one * 0.0032f;
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

    private void EnsureFallbackControlPanel(Transform cameraTransform)
    {
        if (fallbackPanelRoot)
        {
            if (fallbackPanelRoot.transform.parent != cameraTransform)
            {
                fallbackPanelRoot.transform.SetParent(cameraTransform, false);
            }

            fallbackPanelRoot.transform.localPosition = new Vector3(0f, vrPanelHeightOffset + 0.02f, Mathf.Max(0.45f, vrPanelDistance * 0.82f));
            fallbackPanelRoot.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            fallbackPanelRoot.transform.localScale = Vector3.one;
            return;
        }

        fallbackPanelRoot = new GameObject("Multiplayer QR Fallback 3D Panel");
        fallbackPanelRoot.transform.SetParent(cameraTransform, false);
        fallbackPanelRoot.transform.localPosition = new Vector3(0f, vrPanelHeightOffset + 0.02f, Mathf.Max(0.45f, vrPanelDistance * 0.82f));
        fallbackPanelRoot.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

        CreateFallbackQuad(fallbackPanelRoot.transform, "Backplate", new Vector3(0f, 0f, 0.03f), new Vector2(0.82f, 0.62f), new Color(0.01f, 0.018f, 0.035f, 0.92f));
        CreateFallbackText(fallbackPanelRoot.transform, "Title", "Multiplayer QR Session", new Vector3(0f, 0.23f, 0f), 0.045f, FontStyle.Bold, Color.cyan);
        fallbackStatusText = CreateFallbackText(fallbackPanelRoot.transform, "Status", string.Empty, new Vector3(0f, 0.15f, 0f), 0.026f, FontStyle.Normal, Color.white);
        CreateFallbackText(fallbackPanelRoot.transform, "Hint", "Look at a button for 1 second", new Vector3(0f, -0.255f, 0f), 0.023f, FontStyle.Normal, new Color(0.85f, 0.95f, 1f, 1f));

        fallbackButtons.Clear();
        fallbackButtons.Add(CreateFallbackButton(fallbackPanelRoot.transform, "Start Host", new Vector3(0f, 0.06f, 0f), StartHost));
        fallbackButtons.Add(CreateFallbackButton(fallbackPanelRoot.transform, "Start Client", new Vector3(0f, -0.065f, 0f), StartClient));
        fallbackButtons.Add(CreateFallbackButton(fallbackPanelRoot.transform, "Shutdown", new Vector3(0f, -0.19f, 0f), Shutdown));

        Debug.Log("<<< Multiplayer QR fallback 3D panel created. >>>");
    }

    private void UpdateFallbackStatus()
    {
        if (!fallbackStatusText)
        {
            return;
        }

        if (networkManager && networkManager.IsListening)
        {
            var mode = networkManager.IsHost ? "Host" : networkManager.IsServer ? "Server" : "Client";
            fallbackStatusText.text = $"Online: {mode} / ClientId {networkManager.LocalClientId}";
            return;
        }

        fallbackStatusText.text = $"Offline / Client target: {connectAddress}:{port}";
    }

    private void UpdateFallbackGazeSelection(Camera camera)
    {
        UpdateFallbackStatus();

        var hitButton = GetGazedFallbackButton(camera);
        if (hitButton == null)
        {
            gazedTextButton = null;
            gazedTextButtonSince = -1f;
            ResetFallbackButtonHighlights(null, 0f);
            return;
        }

        if (gazedTextButton != hitButton)
        {
            gazedTextButton = hitButton;
            gazedTextButtonSince = Time.time;
        }

        var holdDuration = Mathf.Max(0.1f, gazeSelectSeconds);
        var progress = Mathf.Clamp01((Time.time - gazedTextButtonSince) / holdDuration);
        ResetFallbackButtonHighlights(hitButton, progress);

        if (progress < 1f)
        {
            return;
        }

        gazedTextButtonSince = Time.time + 0.45f;
        hitButton.Click?.Invoke();
    }

    private VrTextButton GetGazedFallbackButton(Camera camera)
    {
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        foreach (var button in fallbackButtons)
        {
            if (button?.Root == null)
            {
                continue;
            }

            var collider = button.Root.GetComponent<BoxCollider>();
            if (collider && collider.Raycast(ray, out _, Mathf.Max(0.6f, vrPanelDistance) + 0.35f))
            {
                return button;
            }
        }

        return null;
    }

    private void ResetFallbackButtonHighlights(VrTextButton activeButton, float progress)
    {
        foreach (var button in fallbackButtons)
        {
            if (button?.Background)
            {
                var color = button == activeButton
                    ? Color.Lerp(new Color(0.07f, 0.28f, 0.72f, 1f), new Color(0f, 0.95f, 1f, 1f), progress)
                    : new Color(0.05f, 0.1f, 0.22f, 1f);
                button.Background.material.color = color;
            }

            if (button?.Label)
            {
                button.Label.text = button == activeButton && progress > 0f
                    ? $"{button.LabelText} {Mathf.CeilToInt((1f - progress) * gazeSelectSeconds + 0.01f)}"
                    : button.LabelText;
            }
        }
    }

    private static VrTextButton CreateFallbackButton(Transform parent, string text, Vector3 localPosition, Action click)
    {
        var root = new GameObject(text);
        root.transform.SetParent(parent, false);
        root.transform.localPosition = localPosition;
        root.transform.localRotation = Quaternion.identity;

        var background = CreateFallbackQuad(root.transform, "ButtonBack", Vector3.zero, new Vector2(0.48f, 0.075f), new Color(0.05f, 0.1f, 0.22f, 1f));
        var label = CreateFallbackText(root.transform, "Label", text, new Vector3(0f, -0.012f, -0.006f), 0.036f, FontStyle.Bold, Color.white);

        var collider = root.AddComponent<BoxCollider>();
        collider.center = Vector3.zero;
        collider.size = new Vector3(0.52f, 0.09f, 0.08f);

        return new VrTextButton
        {
            Root = root,
            Background = background,
            Label = label,
            LabelText = text,
            Click = click
        };
    }

    private static Renderer CreateFallbackQuad(Transform parent, string name, Vector3 localPosition, Vector2 size, Color color)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = name;
        quad.transform.SetParent(parent, false);
        quad.transform.localPosition = localPosition;
        quad.transform.localRotation = Quaternion.identity;
        quad.transform.localScale = new Vector3(size.x, size.y, 1f);

        var collider = quad.GetComponent<Collider>();
        if (collider)
        {
            Destroy(collider);
        }

        var renderer = quad.GetComponent<Renderer>();
        renderer.material = CreateUnlitMaterial(color);
        return renderer;
    }

    private static TextMesh CreateFallbackText(Transform parent, string name, string text, Vector3 localPosition, float characterSize, FontStyle fontStyle, Color color)
    {
        var obj = new GameObject(name, typeof(TextMesh), typeof(MeshRenderer));
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = localPosition;
        obj.transform.localRotation = Quaternion.identity;

        var label = obj.GetComponent<TextMesh>();
        label.font = GetBuiltInFont();
        label.text = text;
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.characterSize = characterSize;
        label.fontSize = 64;
        label.fontStyle = fontStyle;
        label.color = color;

        var renderer = obj.GetComponent<MeshRenderer>();
        renderer.material = label.font ? label.font.material : CreateUnlitMaterial(color);
        renderer.material.color = color;

        return label;
    }

    private static Material CreateUnlitMaterial(Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (!shader)
        {
            shader = Shader.Find("Unlit/Color");
        }
        if (!shader)
        {
            shader = Shader.Find("Sprites/Default");
        }
        if (!shader)
        {
            shader = Shader.Find("Standard");
        }

        var material = new Material(shader);
        material.color = color;
        return material;
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
