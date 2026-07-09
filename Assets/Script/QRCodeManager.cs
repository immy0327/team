using Meta.XR.MRUtilityKit;
using System;
using System.Linq;
using UnityEngine;

public class QRCodeManager : MonoBehaviour
{
    [SerializeField]
    private MRUK _mrukInstance;

    [SerializeField]
    private GameObject _qrCodeSpawnPrefab;

    private static QRCodeManager s_instance;

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
        var instance = Instantiate(_qrCodeSpawnPrefab, trackable.transform);

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
    }

    public void OnTrackableRemoved(MRUKTrackable trackable)
    {
        if (trackable.TrackableType != OVRAnchor.TrackableType.QRCode)
        {
            return;
        }

        Debug.Log("<<< QRCode removed >>>");
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