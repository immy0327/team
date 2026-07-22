using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

public sealed class StartupInstructionImage : MonoBehaviour
{
    private const string TextureResourceName = "multiplayer-controls-intro";
    private const float DisplaySeconds = 12f;

    private Canvas canvas;
    private CanvasGroup canvasGroup;
    private bool rightPrimaryWasPressed;
    private bool rightSecondaryWasPressed;
    private bool leftSecondaryWasPressed;
    private float shownAt;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateOnLoad()
    {
        var obj = new GameObject(nameof(StartupInstructionImage));
        DontDestroyOnLoad(obj);
        obj.AddComponent<StartupInstructionImage>();
    }

    private IEnumerator Start()
    {
        var texture = Resources.Load<Texture2D>(TextureResourceName);
        if (!texture)
        {
            Debug.LogWarning($"<<< Startup instruction texture not found: Resources/{TextureResourceName}. >>>");
            Destroy(gameObject);
            yield break;
        }

        Camera targetCamera = null;
        for (var i = 0; i < 90 && !targetCamera; i++)
        {
            targetCamera = Camera.main ? Camera.main : FindAnyObjectByType<Camera>();
            yield return null;
        }

        if (!targetCamera)
        {
            Debug.LogWarning("<<< Startup instruction image could not find a camera. >>>");
            Destroy(gameObject);
            yield break;
        }

        CreateOverlay(targetCamera, texture);
        shownAt = Time.unscaledTime;
    }

    private void Update()
    {
        if (!canvas)
        {
            return;
        }

        if (Time.unscaledTime - shownAt >= DisplaySeconds || AnyControlShortcutPressed())
        {
            Hide();
        }
    }

    private void CreateOverlay(Camera targetCamera, Texture2D texture)
    {
        var canvasObject = new GameObject("Startup Multiplayer Instructions", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        canvasObject.transform.SetParent(targetCamera.transform, false);
        canvasObject.transform.localPosition = new Vector3(0f, 0f, 1.35f);
        canvasObject.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        canvasObject.transform.localScale = Vector3.one * 0.00125f;

        canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 500;
        canvas.worldCamera = targetCamera;

        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 2f;

        canvasGroup = canvasObject.GetComponent<CanvasGroup>();
        canvasGroup.alpha = 0.96f;
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;

        var canvasRect = canvasObject.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(1600f, 800f);

        var imageObject = new GameObject("Instruction Image", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        imageObject.transform.SetParent(canvasObject.transform, false);

        var imageRect = imageObject.GetComponent<RectTransform>();
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.offsetMin = Vector2.zero;
        imageRect.offsetMax = Vector2.zero;

        var rawImage = imageObject.GetComponent<RawImage>();
        rawImage.texture = texture;
        rawImage.raycastTarget = false;

        Debug.Log("<<< Startup multiplayer instruction image shown. >>>");
    }

    private bool AnyControlShortcutPressed()
    {
        var rightHand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        var leftHand = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);

        var rightPrimaryPressed = IsButtonPressed(rightHand, CommonUsages.primaryButton);
        var rightSecondaryPressed = IsButtonPressed(rightHand, CommonUsages.secondaryButton);
        var leftSecondaryPressed = IsButtonPressed(leftHand, CommonUsages.secondaryButton);

        return WasPressedThisFrame(rightPrimaryPressed, ref rightPrimaryWasPressed)
            || WasPressedThisFrame(rightSecondaryPressed, ref rightSecondaryWasPressed)
            || WasPressedThisFrame(leftSecondaryPressed, ref leftSecondaryWasPressed);
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

    private void Hide()
    {
        if (canvas)
        {
            Destroy(canvas.gameObject);
        }

        Destroy(gameObject);
    }
}
