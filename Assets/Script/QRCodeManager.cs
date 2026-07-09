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
    }

    [SerializeField]
    private MRUK _mrukInstance;

    [SerializeField]
    private GameObject _qrCodeSpawnPrefab;

    [SerializeField]
    private QRCodeModel[] _qrCodeModels;

    private static QRCodeManager s_instance;
    private readonly Dictionary<MRUKTrackable, GameObject> _spawnedObjects = new Dictionary<MRUKTrackable, GameObject>();

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
        var prefab = GetPrefabForKey(key);
        if (!prefab)
        {
            Debug.LogWarning($"<<< No prefab found for QRCode key: {key} >>>");
            return;
        }

        var instance = Instantiate(prefab, trackable.transform);
        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;
        _spawnedObjects[trackable] = instance;

        Debug.Log($"<<< Spawned model for QRCode key: {key} >>>");
    }

    public void OnTrackableRemoved(MRUKTrackable trackable)
    {
        if (trackable.TrackableType != OVRAnchor.TrackableType.QRCode)
        {
            return;
        }

        if (_spawnedObjects.TryGetValue(trackable, out var instance))
        {
            Destroy(instance);
            _spawnedObjects.Remove(trackable);
        }

        Debug.Log("<<< QRCode removed >>>");
    }

    private GameObject GetPrefabForKey(string key)
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
                    return model.Prefab;
                }
            }
        }

        return _qrCodeSpawnPrefab;
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

    private void OnDisable()
    {
        if (_mrukInstance)
        {
            _mrukInstance.SceneSettings.TrackableAdded.RemoveListener(OnTrackableAdded);
            _mrukInstance.SceneSettings.TrackableRemoved.RemoveListener(OnTrackableRemoved);
        }
        if (s_instance == this)
        {
            s_instance = null;
        }
    }
}
