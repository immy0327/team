using UnityEngine;
using UnityEngine.UI;
using Meta.XR;

public class VRSceneController : MonoBehaviour
{
    [Header("Input")]
    public OVRInput.Button togglePanelButton = OVRInput.Button.Three; // 左手 X
    public OVRInput.Button spawnButton = OVRInput.Button.One;         // 右手按鈕
    public OVRInput.Axis2D moveAxis = OVRInput.Axis2D.PrimaryThumbstick;

    [Header("Raycast")]
    public Transform rayStartPoint;
    public float rayLength = 5f;
    public EnvironmentRaycastManager envRayManager;
    public LineRenderer line;

    [Header("Panel & Options")]
    public GameObject panel;
    public Image[] options;
    public GameObject[] prefabs;
    public Color normalColor = Color.white;
    public Color highlightColor = Color.yellow;

    [Header("Settings")]
    public float panelDistance = 1.5f;
    public float panelScale = 0.008f;
    public float inputCooldown = 0.2f;

    private int currentIndex = 0;
    private float lastInputTime = 0f;

    private Vector3 lastHitPoint;

    void Start()
    {
        if (line != null)
            line.positionCount = 2;

        if (panel != null)
            panel.SetActive(false);

        UpdateSelection();
    }

    void Update()
    {
        HandleRaycast();
        HandlePanelToggle();
        HandlePanelInteraction();
    }

    // ------------------------------
    // Raycast 顯示線 & 記錄最後碰撞點
    // ------------------------------
    void HandleRaycast()
    {
        if (rayStartPoint == null || line == null || envRayManager == null) return;

        Ray ray = new Ray(rayStartPoint.position, rayStartPoint.forward);
        bool hasHit = envRayManager.Raycast(ray, out var hit, rayLength);

        line.SetPosition(0, rayStartPoint.position);
        line.SetPosition(1, hasHit ? hit.point : rayStartPoint.position + rayStartPoint.forward * rayLength);

        lastHitPoint = hasHit ? hit.point : rayStartPoint.position + rayStartPoint.forward * rayLength;
    }

    // ------------------------------
    // 左手 X 開關面板
    // ------------------------------
    void HandlePanelToggle()
    {
        if (OVRInput.GetDown(togglePanelButton) && panel != null)
        {
            bool isActive = !panel.activeSelf;
            panel.SetActive(isActive);

            if (isActive)
            {
                Vector3 forward = Camera.main.transform.forward;
                Vector3 targetPos = Camera.main.transform.position + forward * panelDistance;
                panel.transform.position = targetPos;
                panel.transform.rotation = Quaternion.LookRotation(targetPos - Camera.main.transform.position);
                panel.transform.localScale = Vector3.one * panelScale;
            }
        }
    }

    // ------------------------------
    // 面板選項控制 + 右手生成 Prefab
    // ------------------------------
    void HandlePanelInteraction()
    {
        if (panel == null || options.Length == 0 || prefabs.Length == 0) return;

        // 搖桿選擇（只有面板開啟時才生效）
        if (panel.activeSelf)
        {
            Vector2 axis = OVRInput.Get(moveAxis);
            if (Time.time - lastInputTime > inputCooldown)
            {
                if (axis.y > 0.5f)
                {
                    currentIndex = (currentIndex - 1 + options.Length) % options.Length;
                    lastInputTime = Time.time;
                    UpdateSelection();
                }
                else if (axis.y < -0.5f)
                {
                    currentIndex = (currentIndex + 1) % options.Length;
                    lastInputTime = Time.time;
                    UpdateSelection();
                }
            }
        }

        // 右手按鈕生成選中物件（面板開關無關）
        if (OVRInput.GetDown(spawnButton))
        {
            if (currentIndex >= 0 && currentIndex < prefabs.Length)
            {
                Vector3 spawnPos = lastHitPoint; // 生成在 Raycast 碰撞點
                Quaternion spawnRot = Quaternion.LookRotation(rayStartPoint.forward);
                Instantiate(prefabs[currentIndex], spawnPos, spawnRot);
            }
        }
    }

    void UpdateSelection()
    {
        for (int i = 0; i < options.Length; i++)
        {
            options[i].color = (i == currentIndex) ? highlightColor : normalColor;
        }
    }
}