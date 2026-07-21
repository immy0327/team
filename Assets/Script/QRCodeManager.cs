using Meta.XR.MRUtilityKit;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

public class QRCodeManager : MonoBehaviour
{
    [Serializable]
    public class QRCodeModel
    {
        public string Key;
        public GameObject Prefab;
        public int Health = 100;
        public Vector3 ModelRotationOffsetEuler;
    }

    public struct NetworkBattleSnapshot
    {
        public string SpawnId;
        public Vector3 Position;
        public Quaternion Rotation;
        public float CurrentHealth;
        public float MaxHealth;
        public bool HasLost;
        public bool IsAttacking;
    }

    private class SpawnedQRCodeModel
    {
        public GameObject Instance;
        public GameObject HealthBarRoot;
        public RectTransform HealthBarFill;
        public RectTransform HealthBarDamageFill;
        public Image HealthBarFillImage;
        public Image HealthBarDamageFillImage;
        public Text HealthBarText;
        public CanvasGroup HealthBarCanvasGroup;
        public float HealthBarMaxFillWidth;
        public float HealthBarFillHeight;
        public float HealthBarDisplayRatio = 1f;
        public float HealthBarDamageRatio = 1f;
        public string SpawnId;
        public ulong OwnerClientId;
        public string Key;
        public float MaxHealth;
        public float CurrentHealth;
        public float UntrackedSince = -1f;
        public bool HasLost;
        public Quaternion StandingRotationOffset = Quaternion.identity;
        public bool HasValidQRCodePose;
        public Vector3 LastQRCodePosition;
        public Quaternion LastQRCodeRotation = Quaternion.identity;
        public Vector3 PendingQRCodePosition;
        public Quaternion PendingQRCodeRotation = Quaternion.identity;
        public float PendingQRCodeSince = -1f;
        public PlayerCombat[] CombatControllers;
    }

    [SerializeField]
    private MRUK _mrukInstance;

    [SerializeField]
    private GameObject _qrCodeSpawnPrefab;

    [SerializeField]
    private int _defaultHealth = 100;

    [SerializeField]
    private QRCodeModel[] _qrCodeModels;

    [SerializeField]
    private bool _hideModelWhenQRCodeNotTracked = true;

    [SerializeField]
    private bool _keepModelsAfterFirstScan = true;

    [SerializeField]
    private bool _anchorModelOnlyOnFirstScan = true;

    [SerializeField]
    private float _hideDelaySeconds = 0.5f;

    [SerializeField]
    private bool _enableBattle = true;

    [SerializeField]
    private float _battleStartDelaySeconds = 0.5f;

    [SerializeField]
    private float _battleDamagePerSecond = 10f;

    [SerializeField]
    private bool _moveModelsTogetherBeforeBattle = true;

    [SerializeField]
    private float _approachSpeed = 0.35f;

    [SerializeField]
    private float _battleStartDistance = 0.35f;

    [SerializeField]
    private bool _faceEachOtherDuringBattle = true;

    [SerializeField]
    private float _turnSpeedDegreesPerSecond = 360f;

    [SerializeField]
    private float _modelYawOffsetDegrees;

    [SerializeField]
    private Vector3 _modelUprightOffsetEuler;

    [SerializeField]
    private float _modelVerticalOffset = 0.02f;

    [SerializeField]
    private bool _filterQRCodePoseJumps = true;

    [SerializeField]
    private float _maxQRCodeJumpDistance = 0.6f;

    [SerializeField]
    private float _jumpRecoverySeconds = 0.25f;

    [SerializeField]
    private float _poseSmoothingSpeed = 18f;

    [SerializeField]
    private float _healthBarHeight = 0.35f;

    [SerializeField]
    private float _healthBarWidth = 0.28f;

    [SerializeField]
    private float _healthBarMaxValue = 100f;

    [SerializeField]
    private float _healthBarUiScale = 0.002f;

    [SerializeField]
    private float _healthBarDamageLerpSpeed = 5f;

    [SerializeField]
    private float _healthBarGlowPulseSpeed = 4f;

    [SerializeField]
    private float _lowHealthPulseThreshold = 0.3f;

    private static QRCodeManager s_instance;
    private readonly Dictionary<MRUKTrackable, SpawnedQRCodeModel> _spawnedObjects = new Dictionary<MRUKTrackable, SpawnedQRCodeModel>();
    private readonly List<SpawnedQRCodeModel> _networkSpawnedObjects = new List<SpawnedQRCodeModel>();
    private readonly List<MRUKTrackable> _trackablesToRemove = new List<MRUKTrackable>();
    private float _battleReadyTime = -1f;
    private bool _battleInProgress;

    public static bool TrackingEnabled
    {
        get => s_instance && s_instance._mrukInstance && s_instance._mrukInstance.SceneSettings.TrackerConfiguration.QRCodeTrackingEnabled;
        set
        {
            if (!s_instance || !s_instance._mrukInstance)
            {
                return;
            }
            var config = s_instance._mrukInstance.SceneSettings.TrackerConfiguration;
            config.QRCodeTrackingEnabled = value;
            s_instance._mrukInstance.SceneSettings.TrackerConfiguration = config;
        }
    }

    private void OnEnable()
    {
        s_instance = this;

        if (!_mrukInstance)
        {
            Debug.Log($"{nameof(QRCodeManager)} requires an MRUK object in the scene!");
            return;
        }

        _mrukInstance.SceneSettings.TrackableAdded.AddListener(OnTrackableAdded);
        _mrukInstance.SceneSettings.TrackableRemoved.AddListener(OnTrackableRemoved);
    }

    private void Update()
    {
        _trackablesToRemove.Clear();
        var anchorModelsToQRCodes = !_enableBattle || GetActiveFightableModelCount() < 2;

        foreach (var item in _spawnedObjects)
        {
            var trackable = item.Key;
            var model = item.Value;

            if (model.Instance == null)
            {
                _trackablesToRemove.Add(trackable);
                continue;
            }

            if (!trackable)
            {
                if (_keepModelsAfterFirstScan)
                {
                    UpdateHealthBarPosition(model);
                    UpdateHealthBar(model);
                    FaceHealthBarToCamera(model);
                    continue;
                }

                _trackablesToRemove.Add(trackable);
                continue;
            }

            UpdateHealthBarPosition(model);
            UpdateHealthBar(model);
            FaceHealthBarToCamera(model);

            if (model.HasLost)
            {
                SetModelAttacking(model, false);
                continue;
            }

            if (trackable.IsTracked)
            {
                model.UntrackedSince = -1f;
                if (anchorModelsToQRCodes && ShouldUpdateModelFromQRCode(model))
                {
                    PlaceModelOnQRCode(model, trackable);
                    UpdateHealthBarPosition(model);
                }
                if (_hideModelWhenQRCodeNotTracked && !model.Instance.activeSelf)
                {
                    model.Instance.SetActive(true);
                    SetHealthBarActive(model, true);
                    SetModelAttacking(model, false);
                    Debug.Log("<<< QRCode tracked again. Showing model. >>>");
                }
                continue;
            }

            if (_keepModelsAfterFirstScan)
            {
                if (!model.Instance.activeSelf)
                {
                    model.Instance.SetActive(true);
                    SetHealthBarActive(model, true);
                }

                continue;
            }

            if (!_hideModelWhenQRCodeNotTracked)
            {
                continue;
            }

            if (model.UntrackedSince < 0f)
            {
                model.UntrackedSince = Time.time;
            }

            if (Time.time - model.UntrackedSince >= _hideDelaySeconds && model.Instance.activeSelf)
            {
                SetModelAttacking(model, false);
                model.Instance.SetActive(false);
                SetHealthBarActive(model, false);
                Debug.Log("<<< QRCode not tracked. Hiding model. >>>");
            }
        }

        foreach (var trackable in _trackablesToRemove)
        {
            if (_spawnedObjects.TryGetValue(trackable, out var model))
            {
                SetModelAttacking(model, false);
            }
            _spawnedObjects.Remove(trackable);
        }

        foreach (var model in _networkSpawnedObjects)
        {
            if (model.Instance == null)
            {
                continue;
            }

            UpdateHealthBarPosition(model);
            UpdateHealthBar(model);
            FaceHealthBarToCamera(model);

            if (model.HasLost)
            {
                SetModelAttacking(model, false);
            }
        }

        if (ShouldRunBattleSimulation())
        {
            TryResolveBattle();
        }
    }

    private void OnTrackableAdded(MRUKTrackable trackable)
    {
        if (trackable.TrackableType != OVRAnchor.TrackableType.QRCode)
        {
            return;
        }

        if (_spawnedObjects.ContainsKey(trackable))
        {
            return;
        }

        Debug.Log("<<< QRCode detected! >>>>" + nameof(OnTrackableAdded));

        Debug.Log($"<<< QRCode ID: {trackable.name}, Position: {trackable.transform.position}, Rotation: {trackable.transform.rotation.eulerAngles} >>>");

        Debug.Log("<<< 2D Bounding Box points: " + trackable.PlaneRect + ">>>");

        Debug.Log("<<< 2D Polygon points: " + trackable.PlaneBoundary2D + ">>>");

        if (trackable.MarkerPayloadString is { } str)
        {
            Debug.Log("<<< Payload is a string: " + str + ">>>");
        }
        else if (trackable.MarkerPayloadBytes is { } bytes)
        {
            Debug.Log($"Binary(data=[{string.Join(" ", bytes.Take(16).Select(b => $"{b:x02}"))}{(bytes.Length > 16 ? " ..." : "")}], length={bytes.Length})");
        }
        else
        {
            Debug.Log("<<<< No payload >>>>");
        }

        var key = GetModelKey(trackable.MarkerPayloadString);
        var multiplayer = MultiplayerQRCodeSession.Instance;
        if (_keepModelsAfterFirstScan &&
            ((!multiplayer.IsNetworkActive && HasSpawnedModelForKey(key)) ||
             (multiplayer.IsNetworkActive && HasSpawnedModelForSpawnId($"{multiplayer.LocalClientId}:{key}"))))
        {
            Debug.Log($"<<< QRCode key already spawned and will stay visible: {key} >>>");
            return;
        }

        var config = GetModelForKey(key);
        var prefab = config?.Prefab ? config.Prefab : _qrCodeSpawnPrefab;
        if (!prefab)
        {
            Debug.LogWarning($"<<< No prefab found for QRCode key: {key} >>>");
            return;
        }

        var health = GetHealth(config);
        var spawnPosition = trackable.transform.position + Vector3.up * _modelVerticalOffset;
        var spawnRotation = GetUprightRotation(trackable.transform.rotation);

        if (multiplayer.IsNetworkActive)
        {
            multiplayer.RequestSpawnModel(key, spawnPosition, spawnRotation, health);
            return;
        }

        SpawnLocalModel(key, config, prefab, spawnPosition, spawnRotation, health, trackable);
    }

    private SpawnedQRCodeModel SpawnLocalModel(
        string key,
        QRCodeModel config,
        GameObject prefab,
        Vector3 position,
        Quaternion rotation,
        int health,
        MRUKTrackable trackable)
    {
        var instance = new GameObject($"QRCodeModel({key})");
        var visual = Instantiate(prefab, instance.transform, false);
        visual.name = prefab.name;
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.Euler(GetModelRotationOffset(config));

        var spawnedModel = new SpawnedQRCodeModel
        {
            SpawnId = trackable ? trackable.GetInstanceID().ToString() : key,
            Instance = instance,
            HealthBarRoot = CreateHealthBar(
                health,
                out var healthBarFill,
                out var healthBarDamageFill,
                out var healthBarFillImage,
                out var healthBarDamageFillImage,
                out var healthBarText,
                out var healthBarCanvasGroup),
            HealthBarFill = healthBarFill,
            HealthBarDamageFill = healthBarDamageFill,
            HealthBarFillImage = healthBarFillImage,
            HealthBarDamageFillImage = healthBarDamageFillImage,
            HealthBarText = healthBarText,
            HealthBarCanvasGroup = healthBarCanvasGroup,
            HealthBarMaxFillWidth = healthBarFill.sizeDelta.x,
            HealthBarFillHeight = healthBarFill.sizeDelta.y,
            Key = key,
            MaxHealth = health,
            CurrentHealth = health,
            CombatControllers = visual.GetComponentsInChildren<PlayerCombat>(true)
        };

        instance.transform.SetPositionAndRotation(position, rotation);
        spawnedModel.LastQRCodePosition = position;
        spawnedModel.LastQRCodeRotation = rotation;
        spawnedModel.HasValidQRCodePose = true;
        spawnedModel.StandingRotationOffset = GetStandingRotationOffset(instance.transform.rotation);

        if (trackable)
        {
            _spawnedObjects[trackable] = spawnedModel;
        }

        UpdateHealthBarPosition(spawnedModel);
        UpdateHealthBar(spawnedModel);

        _battleReadyTime = -1f;
        _battleInProgress = false;
        SetAllModelsAttacking(false);

        Debug.Log($"<<< Spawned model for QRCode key: {key}, health: {health} >>>");
        return spawnedModel;
    }

    public void OnTrackableRemoved(MRUKTrackable trackable)
    {
        if (trackable.TrackableType != OVRAnchor.TrackableType.QRCode)
        {
            return;
        }

        if (_keepModelsAfterFirstScan)
        {
            Debug.Log("<<< QRCode removed, spawned model stays visible. >>>");
            return;
        }

        DestroySpawnedModel(trackable);

        Debug.Log("<<< QRCode removed >>>");
    }

    private QRCodeModel GetModelForKey(string key)
    {
        if (!string.IsNullOrWhiteSpace(key) && _qrCodeModels != null)
        {
            foreach (var model in _qrCodeModels)
            {
                if (model == null || !model.Prefab)
                {
                    continue;
                }

                if (string.Equals(model.Key?.Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    return model;
                }
            }
        }

        return null;
    }

    private bool HasSpawnedModelForKey(string key)
    {
        return GetAllSpawnedModels().Any(model =>
            model.Instance &&
            !model.HasLost &&
            string.Equals(model.Key?.Trim(), key?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private bool HasSpawnedModelForSpawnId(string spawnId)
    {
        return GetAllSpawnedModels().Any(model =>
            model.Instance &&
            !model.HasLost &&
            string.Equals(model.SpawnId, spawnId, StringComparison.OrdinalIgnoreCase));
    }

    public void SpawnNetworkedModel(string spawnId, string key, Vector3 position, Quaternion rotation, int health, ulong ownerClientId)
    {
        if (HasSpawnedModelForSpawnId(spawnId))
        {
            return;
        }

        var config = GetModelForKey(key);
        var prefab = config?.Prefab ? config.Prefab : _qrCodeSpawnPrefab;
        if (!prefab)
        {
            Debug.LogWarning($"<<< No prefab found for network QRCode key: {key} >>>");
            return;
        }

        var spawnedModel = SpawnLocalModel(key, config, prefab, position, rotation, health, null);
        spawnedModel.SpawnId = spawnId;
        spawnedModel.OwnerClientId = ownerClientId;
        _networkSpawnedObjects.Add(spawnedModel);
        Debug.Log($"<<< Network spawned QRCode model: {spawnId}, key: {key} >>>");
    }

    public List<NetworkBattleSnapshot> GetNetworkBattleSnapshots()
    {
        var snapshots = new List<NetworkBattleSnapshot>();
        foreach (var model in GetAllSpawnedModels())
        {
            if (!model.Instance)
            {
                continue;
            }

            snapshots.Add(new NetworkBattleSnapshot
            {
                SpawnId = model.SpawnId,
                Position = model.Instance.transform.position,
                Rotation = model.Instance.transform.rotation,
                CurrentHealth = model.CurrentHealth,
                MaxHealth = model.MaxHealth,
                HasLost = model.HasLost,
                IsAttacking = _battleInProgress && !model.HasLost && model.CurrentHealth > 0f
            });
        }

        return snapshots;
    }

    public void ApplyNetworkBattleSnapshot(
        string spawnId,
        Vector3 position,
        Quaternion rotation,
        float currentHealth,
        float maxHealth,
        bool hasLost,
        bool isAttacking)
    {
        var model = FindSpawnedModelBySpawnId(spawnId);
        if (model == null || !model.Instance)
        {
            return;
        }

        model.Instance.transform.SetPositionAndRotation(position, rotation);
        model.CurrentHealth = Mathf.Clamp(currentHealth, 0f, Mathf.Max(1f, maxHealth));
        model.MaxHealth = Mathf.Max(1f, maxHealth);
        model.HasLost = hasLost;

        if (hasLost)
        {
            SetModelAttacking(model, false);
            model.Instance.SetActive(false);
            SetHealthBarActive(model, false);
            UpdateHealthBar(model);
            return;
        }

        if (!model.Instance.activeSelf)
        {
            model.Instance.SetActive(true);
        }

        SetHealthBarActive(model, true);
        SetModelAttacking(model, isAttacking);
        UpdateHealthBarPosition(model);
        UpdateHealthBar(model);
    }

    private IEnumerable<SpawnedQRCodeModel> GetAllSpawnedModels()
    {
        foreach (var model in _spawnedObjects.Values)
        {
            yield return model;
        }

        foreach (var model in _networkSpawnedObjects)
        {
            yield return model;
        }
    }

    private SpawnedQRCodeModel FindSpawnedModelBySpawnId(string spawnId)
    {
        return GetAllSpawnedModels().FirstOrDefault(model =>
            model.Instance &&
            string.Equals(model.SpawnId, spawnId, StringComparison.OrdinalIgnoreCase));
    }

    private bool ShouldRunBattleSimulation()
    {
        var multiplayer = MultiplayerQRCodeSession.Instance;
        return !multiplayer.IsNetworkActive || multiplayer.IsServer;
    }

    private int GetHealth(QRCodeModel config)
    {
        if (config == null || config.Health <= 0)
        {
            return Mathf.Max(1, _defaultHealth);
        }

        return config.Health;
    }

    private Vector3 GetModelRotationOffset(QRCodeModel config)
    {
        return config != null ? config.ModelRotationOffsetEuler : _modelUprightOffsetEuler;
    }

    private static string GetModelKey(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        payload = payload.Trim();

        if (Uri.TryCreate(payload, UriKind.Absolute, out var uri))
        {
            return uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
        }

        return payload;
    }

    private GameObject CreateHealthBar(
        int health,
        out RectTransform fillTransform,
        out RectTransform damageFillTransform,
        out Image fillImage,
        out Image damageFillImage,
        out Text healthText,
        out CanvasGroup canvasGroup)
    {
        var root = new GameObject($"HealthBar({health})", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup));
        root.transform.localScale = Vector3.one * Mathf.Max(0.0001f, _healthBarUiScale);

        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 50;

        canvasGroup = root.GetComponent<CanvasGroup>();
        canvasGroup.alpha = 1f;

        var barWidth = Mathf.Max(80f, _healthBarWidth * 520f);
        var frameWidth = barWidth + 28f;
        var rootWidth = frameWidth + 14f;

        var rootRect = root.GetComponent<RectTransform>();
        rootRect.sizeDelta = new Vector2(rootWidth, 54f);

        var glow = CreateHealthBarImage(rootRect, "OuterGlow", new Color(0.05f, 0.9f, 1f, 0.22f), new Vector2(rootWidth + 8f, 58f), Vector2.zero);
        glow.raycastTarget = false;

        var frame = CreateHealthBarImage(rootRect, "Frame", new Color(0.01f, 0.015f, 0.025f, 0.88f), new Vector2(frameWidth, 46f), Vector2.zero);
        var frameOutline = frame.gameObject.AddComponent<Outline>();
        frameOutline.effectColor = new Color(0.25f, 0.95f, 1f, 0.9f);
        frameOutline.effectDistance = new Vector2(1.2f, -1.2f);

        var barBack = CreateHealthBarImage(rootRect, "BarBack", new Color(0.03f, 0.035f, 0.05f, 0.95f), new Vector2(barWidth + 6f, 18f), new Vector2(0f, -6f));
        var backOutline = barBack.gameObject.AddComponent<Outline>();
        backOutline.effectColor = new Color(0f, 0f, 0f, 0.7f);
        backOutline.effectDistance = new Vector2(1f, -1f);

        damageFillImage = CreateHealthBarImage(barBack.rectTransform, "DamageFill", new Color(1f, 0.28f, 0.08f, 0.9f), new Vector2(barWidth, 12f), Vector2.zero);
        damageFillImage.type = Image.Type.Filled;
        damageFillImage.fillMethod = Image.FillMethod.Horizontal;
        damageFillImage.fillOrigin = 0;
        damageFillImage.fillAmount = 1f;
        damageFillTransform = damageFillImage.rectTransform;

        fillImage = CreateHealthBarImage(barBack.rectTransform, "HealthFill", new Color(0.1f, 1f, 0.45f, 1f), new Vector2(barWidth, 12f), Vector2.zero);
        fillImage.type = Image.Type.Filled;
        fillImage.fillMethod = Image.FillMethod.Horizontal;
        fillImage.fillOrigin = 0;
        fillImage.fillAmount = 1f;
        fillTransform = fillImage.rectTransform;

        var shine = CreateHealthBarImage(barBack.rectTransform, "TopShine", new Color(1f, 1f, 1f, 0.22f), new Vector2(barWidth, 4f), new Vector2(0f, 4f));
        shine.raycastTarget = false;

        for (var i = 1; i < 8; i++)
        {
            var x = Mathf.Lerp(-barWidth * 0.5f, barWidth * 0.5f, i / 8f);
            var marker = CreateHealthBarImage(barBack.rectTransform, $"Segment{i}", new Color(0f, 0f, 0f, 0.28f), new Vector2(1.2f, 14f), new Vector2(x, 0f));
            marker.raycastTarget = false;
        }

        healthText = CreateHealthBarText(rootRect, "HealthText", $"HP {health}/{health}", new Vector2(0f, 13f));

        return root;
    }

    private static Image CreateHealthBarImage(RectTransform parent, string name, Color color, Vector2 size, Vector2 anchoredPosition)
    {
        var obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        obj.transform.SetParent(parent, false);

        var rectTransform = obj.GetComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.sizeDelta = size;
        rectTransform.anchoredPosition = anchoredPosition;

        var image = obj.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static Text CreateHealthBarText(RectTransform parent, string name, string text, Vector2 anchoredPosition)
    {
        var obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(Outline));
        obj.transform.SetParent(parent, false);

        var rectTransform = obj.GetComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.sizeDelta = new Vector2(150f, 18f);
        rectTransform.anchoredPosition = anchoredPosition;

        var label = obj.GetComponent<Text>();
        label.font = GetBuiltInFont();
        label.text = text;
        label.alignment = TextAnchor.MiddleCenter;
        label.fontSize = 14;
        label.fontStyle = FontStyle.Bold;
        label.color = new Color(0.92f, 1f, 1f, 1f);
        label.raycastTarget = false;

        var outline = obj.GetComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
        outline.effectDistance = new Vector2(1f, -1f);

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

    private void FaceHealthBarToCamera(SpawnedQRCodeModel model)
    {
        if (!model.HealthBarRoot || !Camera.main)
        {
            return;
        }

        var direction = model.HealthBarRoot.transform.position - Camera.main.transform.position;
        if (direction.sqrMagnitude > 0.0001f)
        {
            model.HealthBarRoot.transform.rotation = Quaternion.LookRotation(direction);
        }
    }

    private static void SetHealthBarActive(SpawnedQRCodeModel model, bool active)
    {
        if (model.HealthBarRoot)
        {
            model.HealthBarRoot.SetActive(active);
        }
    }

    private bool ShouldUpdateModelFromQRCode(SpawnedQRCodeModel model)
    {
        return !_keepModelsAfterFirstScan ||
               !_anchorModelOnlyOnFirstScan ||
               !model.HasValidQRCodePose;
    }

    private static void SetModelAttacking(SpawnedQRCodeModel model, bool attacking)
    {
        if (model == null || model.CombatControllers == null)
        {
            return;
        }

        foreach (var combat in model.CombatControllers)
        {
            if (combat != null)
            {
                combat.SetBattleAttacking(attacking);
            }
        }
    }

    private static void SetModelsAttacking(IEnumerable<SpawnedQRCodeModel> models, bool attacking)
    {
        foreach (var model in models)
        {
            SetModelAttacking(model, attacking);
        }
    }

    private void SetAllModelsAttacking(bool attacking)
    {
        foreach (var model in GetAllSpawnedModels())
        {
            SetModelAttacking(model, attacking);
        }
    }

    private int GetActiveFightableModelCount()
    {
        return GetAllSpawnedModels().Count(model =>
            IsFightableModelActive(GetTrackableForModel(model), model));
    }

    private MRUKTrackable GetTrackableForModel(SpawnedQRCodeModel model)
    {
        foreach (var item in _spawnedObjects)
        {
            if (ReferenceEquals(item.Value, model))
            {
                return item.Key;
            }
        }

        return null;
    }

    private bool IsFightableModelActive(MRUKTrackable trackable, SpawnedQRCodeModel model)
    {
        return model.Instance &&
               model.Instance.activeSelf &&
               !model.HasLost &&
               (_keepModelsAfterFirstScan || (trackable && trackable.IsTracked));
    }

    private void PlaceModelOnQRCode(SpawnedQRCodeModel model, MRUKTrackable trackable)
    {
        if (!model.Instance || !trackable)
        {
            return;
        }

        var modelTransform = model.Instance.transform;
        var targetPosition = trackable.transform.position + Vector3.up * _modelVerticalOffset;
        var targetRotation = GetUprightRotation(trackable.transform.rotation);
        var hadValidPose = model.HasValidQRCodePose;

        if (!TryAcceptQRCodePose(model, targetPosition, targetRotation))
        {
            return;
        }

        if (!hadValidPose || _poseSmoothingSpeed <= 0f)
        {
            modelTransform.position = model.LastQRCodePosition;
            modelTransform.rotation = model.LastQRCodeRotation;
            model.StandingRotationOffset = GetStandingRotationOffset(modelTransform.rotation);
            return;
        }

        var smoothing = 1f - Mathf.Exp(-Mathf.Max(0f, _poseSmoothingSpeed) * Time.deltaTime);
        modelTransform.position = Vector3.Lerp(modelTransform.position, model.LastQRCodePosition, smoothing);
        modelTransform.rotation = Quaternion.Slerp(modelTransform.rotation, model.LastQRCodeRotation, smoothing);
        model.StandingRotationOffset = GetStandingRotationOffset(modelTransform.rotation);
    }

    private bool TryAcceptQRCodePose(SpawnedQRCodeModel model, Vector3 targetPosition, Quaternion targetRotation)
    {
        if (!_filterQRCodePoseJumps || !model.HasValidQRCodePose)
        {
            AcceptQRCodePose(model, targetPosition, targetRotation);
            return true;
        }

        var maxJumpDistance = Mathf.Max(0f, _maxQRCodeJumpDistance);
        var jumpDistance = Vector3.Distance(model.LastQRCodePosition, targetPosition);
        if (maxJumpDistance <= 0f || jumpDistance <= maxJumpDistance)
        {
            AcceptQRCodePose(model, targetPosition, targetRotation);
            return true;
        }

        if (model.PendingQRCodeSince < 0f ||
            Vector3.Distance(model.PendingQRCodePosition, targetPosition) > maxJumpDistance * 0.25f)
        {
            model.PendingQRCodePosition = targetPosition;
            model.PendingQRCodeRotation = targetRotation;
            model.PendingQRCodeSince = Time.time;
            Debug.Log($"<<< Ignoring QRCode pose jump for {model.Key}: {jumpDistance:0.00}m >>>");
            return false;
        }

        if (Time.time - model.PendingQRCodeSince < Mathf.Max(0f, _jumpRecoverySeconds))
        {
            return false;
        }

        AcceptQRCodePose(model, model.PendingQRCodePosition, model.PendingQRCodeRotation);
        Debug.Log($"<<< Accepted stable QRCode pose jump for {model.Key}. >>>");
        return true;
    }

    private static void AcceptQRCodePose(SpawnedQRCodeModel model, Vector3 position, Quaternion rotation)
    {
        model.LastQRCodePosition = position;
        model.LastQRCodeRotation = rotation;
        model.HasValidQRCodePose = true;
        model.PendingQRCodeSince = -1f;
    }

    private Quaternion GetUprightRotation(Quaternion qrRotation)
    {
        var forward = Vector3.ProjectOnPlane(qrRotation * Vector3.forward, Vector3.up);
        if (forward.sqrMagnitude <= 0.0001f)
        {
            forward = Vector3.ProjectOnPlane(qrRotation * Vector3.up, Vector3.up);
        }

        if (forward.sqrMagnitude <= 0.0001f)
        {
            return Quaternion.Euler(0f, _modelYawOffsetDegrees, 0f);
        }

        return Quaternion.LookRotation(forward.normalized, Vector3.up) *
               Quaternion.Euler(0f, _modelYawOffsetDegrees, 0f);
    }

    private void TryResolveBattle()
    {
        if (!_enableBattle)
        {
            SetAllModelsAttacking(false);
            _battleInProgress = false;
            return;
        }

        var activeModels = GetAllSpawnedModels()
            .Where(model => IsFightableModelActive(GetTrackableForModel(model), model))
            .ToList();

        if (activeModels.Count < 2)
        {
            SetAllModelsAttacking(false);
            _battleReadyTime = -1f;
            _battleInProgress = false;
            return;
        }

        if (_faceEachOtherDuringBattle)
        {
            FaceModelsTowardCenter(activeModels);
        }
        else
        {
            KeepModelsStanding(activeModels);
        }

        if (!_battleInProgress && _moveModelsTogetherBeforeBattle && !AreModelsCloseEnough(activeModels))
        {
            SetModelsAttacking(activeModels, false);
            _battleReadyTime = -1f;
            MoveModelsTowardCenter(activeModels);
            return;
        }

        if (!_battleInProgress && _battleReadyTime < 0f)
        {
            SetModelsAttacking(activeModels, false);
            _battleReadyTime = Time.time + _battleStartDelaySeconds;
            Debug.Log("<<< Battle ready. Fighting soon. >>>");
            return;
        }

        if (!_battleInProgress && Time.time < _battleReadyTime)
        {
            SetModelsAttacking(activeModels, false);
            return;
        }

        if (!_battleInProgress)
        {
            _battleInProgress = true;
            SetModelsAttacking(activeModels, true);
            Debug.Log("<<< Battle started. Health bars are decreasing. >>>");
        }
        else
        {
            SetModelsAttacking(activeModels, true);
        }

        var damage = Mathf.Max(0f, _battleDamagePerSecond) * Time.deltaTime;
        foreach (var model in activeModels)
        {
            model.CurrentHealth = Mathf.Max(0f, model.CurrentHealth - damage);
            UpdateHealthBar(model);

            if (model.CurrentHealth > 0f)
            {
                continue;
            }

            model.HasLost = true;
            SetModelAttacking(model, false);
            if (model.Instance)
            {
                model.Instance.SetActive(false);
            }
            SetHealthBarActive(model, false);
            Debug.Log($"<<< {model.Key} lost. Model hidden. >>>");
        }

        var survivors = activeModels.Where(model => !model.HasLost && model.CurrentHealth > 0f).ToList();
        if (survivors.Count == 1)
        {
            SetModelsAttacking(activeModels, false);
            Debug.Log($"<<< {survivors[0].Key} wins with health {survivors[0].CurrentHealth:0}! >>>");
            _battleReadyTime = -1f;
            _battleInProgress = false;
        }
        else if (survivors.Count == 0)
        {
            SetModelsAttacking(activeModels, false);
            Debug.Log("<<< Battle ended in a draw. >>>");
            _battleReadyTime = -1f;
            _battleInProgress = false;
        }
    }

    private bool AreModelsCloseEnough(List<SpawnedQRCodeModel> models)
    {
        var center = GetBattleCenter(models);
        return models.All(model => Vector3.Distance(model.Instance.transform.position, center) <= _battleStartDistance);
    }

    private void MoveModelsTowardCenter(List<SpawnedQRCodeModel> models)
    {
        var center = GetBattleCenter(models);
        var step = Mathf.Max(0f, _approachSpeed) * Time.deltaTime;

        foreach (var model in models)
        {
            var transformToMove = model.Instance.transform;
            var position = transformToMove.position;
            var target = center;
            target.y = position.y;
            transformToMove.position = Vector3.MoveTowards(position, target, step);
        }
    }

    private void FaceModelsTowardCenter(List<SpawnedQRCodeModel> models)
    {
        var center = GetBattleCenter(models);
        var maxDegreesDelta = Mathf.Max(0f, _turnSpeedDegreesPerSecond) * Time.deltaTime;

        foreach (var model in models)
        {
            var transformToRotate = model.Instance.transform;
            var direction = center - transformToRotate.position;
            direction.y = 0f;

            if (direction.sqrMagnitude <= 0.0001f)
            {
                KeepModelStanding(model);
                continue;
            }

            var currentYaw = GetHorizontalYaw(transformToRotate.forward);
            var targetYaw = Quaternion.LookRotation(direction.normalized, Vector3.up).eulerAngles.y + _modelYawOffsetDegrees;
            var yaw = Mathf.MoveTowardsAngle(currentYaw, targetYaw, maxDegreesDelta);
            transformToRotate.rotation = GetStandingRotation(yaw, model.StandingRotationOffset);
        }
    }

    private static void KeepModelsStanding(List<SpawnedQRCodeModel> models)
    {
        foreach (var model in models)
        {
            KeepModelStanding(model);
        }
    }

    private static void KeepModelStanding(SpawnedQRCodeModel model)
    {
        var transformToRotate = model.Instance.transform;
        var yaw = GetHorizontalYaw(transformToRotate.forward);
        transformToRotate.rotation = GetStandingRotation(yaw, model.StandingRotationOffset);
    }

    private static Quaternion GetStandingRotation(float yaw, Quaternion standingRotationOffset)
    {
        return Quaternion.Euler(0f, yaw, 0f) * standingRotationOffset;
    }

    private static Quaternion GetStandingRotationOffset(Quaternion standingRotation)
    {
        var yaw = GetHorizontalYaw(standingRotation * Vector3.forward);
        return Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f)) * standingRotation;
    }

    private static float GetHorizontalYaw(Vector3 forward)
    {
        forward = Vector3.ProjectOnPlane(forward, Vector3.up);
        if (forward.sqrMagnitude <= 0.0001f)
        {
            return 0f;
        }

        return Quaternion.LookRotation(forward.normalized, Vector3.up).eulerAngles.y;
    }

    private static Vector3 GetBattleCenter(List<SpawnedQRCodeModel> models)
    {
        var center = Vector3.zero;
        foreach (var model in models)
        {
            center += model.Instance.transform.position;
        }

        return center / models.Count;
    }

    private void UpdateHealthBarPosition(SpawnedQRCodeModel model)
    {
        if (!model.HealthBarRoot || !model.Instance)
        {
            return;
        }

        model.HealthBarRoot.transform.position = GetHealthBarWorldPosition(model);
    }

    private Vector3 GetHealthBarWorldPosition(SpawnedQRCodeModel model)
    {
        if (!TryGetModelBounds(model.Instance, out var bounds))
        {
            return model.Instance.transform.position + Vector3.up * _healthBarHeight;
        }

        return new Vector3(bounds.center.x, bounds.max.y + _healthBarHeight, bounds.center.z);
    }

    private static bool TryGetModelBounds(GameObject modelRoot, out Bounds bounds)
    {
        bounds = default;
        if (!modelRoot)
        {
            return false;
        }

        var renderers = modelRoot.GetComponentsInChildren<Renderer>(false);
        var hasBounds = false;
        foreach (var renderer in renderers)
        {
            if (!renderer || !renderer.enabled)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
                continue;
            }

            bounds.Encapsulate(renderer.bounds);
        }

        return hasBounds;
    }

    private void UpdateHealthBar(SpawnedQRCodeModel model)
    {
        if (!model.HealthBarFillImage)
        {
            return;
        }

        var maxHealth = Mathf.Max(1f, model.MaxHealth > 0f ? model.MaxHealth : _healthBarMaxValue);
        var ratio = Mathf.Clamp01(model.CurrentHealth / maxHealth);
        model.HealthBarDisplayRatio = ratio;
        model.HealthBarDamageRatio = Mathf.MoveTowards(
            model.HealthBarDamageRatio,
            ratio,
            Mathf.Max(0.1f, _healthBarDamageLerpSpeed) * Time.deltaTime);

        SetHealthBarFillWidth(model.HealthBarFill, model.HealthBarMaxFillWidth, model.HealthBarFillHeight, model.HealthBarDisplayRatio);
        model.HealthBarFillImage.color = GetHealthFillColor(ratio);

        if (model.HealthBarDamageFillImage)
        {
            SetHealthBarFillWidth(
                model.HealthBarDamageFill,
                model.HealthBarMaxFillWidth,
                model.HealthBarFillHeight,
                Mathf.Max(model.HealthBarDamageRatio, ratio));
            model.HealthBarDamageFillImage.color = new Color(1f, 0.24f, 0.06f, ratio < 1f ? 0.9f : 0.25f);
        }

        if (model.HealthBarText)
        {
            model.HealthBarText.text = $"HP {Mathf.CeilToInt(model.CurrentHealth)}/{Mathf.CeilToInt(maxHealth)}";
        }

        if (model.HealthBarCanvasGroup)
        {
            var isLowHealth = ratio <= Mathf.Clamp01(_lowHealthPulseThreshold);
            var pulse = isLowHealth ? Mathf.Abs(Mathf.Sin(Time.time * Mathf.Max(0.1f, _healthBarGlowPulseSpeed))) : 0f;
            model.HealthBarCanvasGroup.alpha = isLowHealth ? Mathf.Lerp(0.72f, 1f, pulse) : 1f;
        }
    }

    private static void SetHealthBarFillWidth(RectTransform fill, float maxWidth, float height, float ratio)
    {
        if (!fill)
        {
            return;
        }

        ratio = Mathf.Clamp01(ratio);
        maxWidth = Mathf.Max(1f, maxWidth);
        var width = maxWidth * ratio;
        fill.sizeDelta = new Vector2(width, height);
        fill.anchoredPosition = new Vector2(-maxWidth * (1f - ratio) * 0.5f, fill.anchoredPosition.y);
    }

    private static Color GetHealthFillColor(float ratio)
    {
        var danger = new Color(1f, 0.08f, 0.05f, 1f);
        var warning = new Color(1f, 0.78f, 0.08f, 1f);
        var healthy = new Color(0.1f, 1f, 0.45f, 1f);

        return ratio < 0.5f
            ? Color.Lerp(danger, warning, ratio * 2f)
            : Color.Lerp(warning, healthy, (ratio - 0.5f) * 2f);
    }

    private void DestroySpawnedModel(MRUKTrackable trackable)
    {
        if (!_spawnedObjects.TryGetValue(trackable, out var model))
        {
            return;
        }

        SetModelAttacking(model, false);

        if (model.HealthBarRoot)
        {
            Destroy(model.HealthBarRoot);
        }

        if (model.Instance)
        {
            Destroy(model.Instance);
        }

        _spawnedObjects.Remove(trackable);
    }

    private void OnDisable()
    {
        if (_mrukInstance)
        {
            _mrukInstance.SceneSettings.TrackableAdded.RemoveListener(OnTrackableAdded);
            _mrukInstance.SceneSettings.TrackableRemoved.RemoveListener(OnTrackableRemoved);
        }
        foreach (var model in _spawnedObjects.Values)
        {
            SetModelAttacking(model, false);

            if (model.HealthBarRoot)
            {
                Destroy(model.HealthBarRoot);
            }
            if (model.Instance)
            {
                Destroy(model.Instance);
            }
        }
        foreach (var model in _networkSpawnedObjects)
        {
            SetModelAttacking(model, false);

            if (model.HealthBarRoot)
            {
                Destroy(model.HealthBarRoot);
            }
            if (model.Instance)
            {
                Destroy(model.Instance);
            }
        }
        _spawnedObjects.Clear();
        _networkSpawnedObjects.Clear();
        if (s_instance == this)
        {
            s_instance = null;
        }
    }
}
