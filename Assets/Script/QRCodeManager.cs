using Meta.XR.MRUtilityKit;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

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

    private class SpawnedQRCodeModel
    {
        public GameObject Instance;
        public GameObject HealthBarRoot;
        public Transform HealthBarFill;
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

    private static QRCodeManager s_instance;
    private readonly Dictionary<MRUKTrackable, SpawnedQRCodeModel> _spawnedObjects = new Dictionary<MRUKTrackable, SpawnedQRCodeModel>();
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

            if (!trackable || model.Instance == null)
            {
                _trackablesToRemove.Add(trackable);
                continue;
            }

            UpdateHealthBarPosition(model);
            FaceHealthBarToCamera(model);

            if (model.HasLost)
            {
                SetModelAttacking(model, false);
                continue;
            }

            if (trackable.IsTracked)
            {
                model.UntrackedSince = -1f;
                if (anchorModelsToQRCodes)
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

        TryResolveBattle();
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
        var config = GetModelForKey(key);
        var prefab = config?.Prefab ? config.Prefab : _qrCodeSpawnPrefab;
        if (!prefab)
        {
            Debug.LogWarning($"<<< No prefab found for QRCode key: {key} >>>");
            return;
        }

        var instance = new GameObject($"QRCodeModel({key})");
        var visual = Instantiate(prefab, instance.transform, false);
        visual.name = prefab.name;
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.Euler(GetModelRotationOffset(config));

        var health = GetHealth(config);
        var spawnedModel = new SpawnedQRCodeModel
        {
            Instance = instance,
            HealthBarRoot = CreateHealthBar(health, out var healthBarFill),
            HealthBarFill = healthBarFill,
            Key = key,
            MaxHealth = health,
            CurrentHealth = health,
            CombatControllers = visual.GetComponentsInChildren<PlayerCombat>(true)
        };
        _spawnedObjects[trackable] = spawnedModel;
        PlaceModelOnQRCode(spawnedModel, trackable);
        UpdateHealthBarPosition(spawnedModel);

        _battleReadyTime = -1f;
        _battleInProgress = false;
        SetAllModelsAttacking(false);

        Debug.Log($"<<< Spawned model for QRCode key: {key}, health: {health} >>>");
    }

    public void OnTrackableRemoved(MRUKTrackable trackable)
    {
        if (trackable.TrackableType != OVRAnchor.TrackableType.QRCode)
        {
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

    private GameObject CreateHealthBar(int health, out Transform fillTransform)
    {
        var root = new GameObject($"HealthBar({health})");

        var background = GameObject.CreatePrimitive(PrimitiveType.Cube);
        background.name = "HealthBarBackground";
        background.transform.SetParent(root.transform, false);
        background.transform.localScale = new Vector3(_healthBarWidth, 0.035f, 0.01f);
        SetPrimitiveColor(background, Color.gray);

        var fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
        fill.name = "HealthBarFill";
        fill.transform.SetParent(root.transform, false);

        var ratio = Mathf.Clamp01(health / Mathf.Max(1f, _healthBarMaxValue));
        fill.transform.localScale = new Vector3(_healthBarWidth * ratio, 0.022f, 0.014f);
        fill.transform.localPosition = new Vector3(-(_healthBarWidth * (1f - ratio)) * 0.5f, 0f, -0.012f);
        SetPrimitiveColor(fill, Color.green);

        fillTransform = fill.transform;
        return root;
    }

    private static void SetPrimitiveColor(GameObject obj, Color color)
    {
        if (obj.TryGetComponent<Collider>(out var collider))
        {
            Destroy(collider);
        }

        if (obj.TryGetComponent<Renderer>(out var renderer))
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (!shader)
            {
                shader = Shader.Find("Standard");
            }
            if (!shader)
            {
                return;
            }
            renderer.material = new Material(shader);
            renderer.material.color = color;
        }
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
        foreach (var model in _spawnedObjects.Values)
        {
            SetModelAttacking(model, attacking);
        }
    }

    private int GetActiveFightableModelCount()
    {
        return _spawnedObjects.Count(item =>
            item.Key && item.Key.IsTracked &&
            item.Value.Instance && item.Value.Instance.activeSelf &&
            !item.Value.HasLost);
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

        var activeModels = _spawnedObjects
            .Where(item => item.Key && item.Key.IsTracked && item.Value.Instance && item.Value.Instance.activeSelf && !item.Value.HasLost)
            .Select(item => item.Value)
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

        model.HealthBarRoot.transform.position = model.Instance.transform.position + Vector3.up * _healthBarHeight;
    }

    private void UpdateHealthBar(SpawnedQRCodeModel model)
    {
        if (!model.HealthBarFill)
        {
            return;
        }

        var ratio = Mathf.Clamp01(model.CurrentHealth / Mathf.Max(1f, _healthBarMaxValue));
        model.HealthBarFill.localScale = new Vector3(_healthBarWidth * ratio, 0.022f, 0.014f);
        model.HealthBarFill.localPosition = new Vector3(-(_healthBarWidth * (1f - ratio)) * 0.5f, 0f, -0.012f);
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
        _spawnedObjects.Clear();
        if (s_instance == this)
        {
            s_instance = null;
        }
    }
}
