using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(CharacterController))]
public class PlayerController2 : MonoBehaviour
{
    [Header("Gravity Settings")]
    [SerializeField] private float gravity = -9.81f;

    private Animator animator;
    private CharacterController controller;
    private float verticalVelocity;

    private readonly int attackHash = Animator.StringToHash("Attack");

    void Start()
    {
        animator = GetComponent<Animator>();
        controller = GetComponent<CharacterController>();
    }

    void Update()
    {
        // 1. 重力處理：如果角色不在地面上，持續增加向下速度
        if (controller.isGrounded && verticalVelocity < 0)
        {
            verticalVelocity = -2f; // 給予一個微小的負值以確保緊貼地面
        }
        else
        {
            verticalVelocity += gravity * Time.deltaTime;
        }

        // 2. 應用重力移動
        controller.Move(new Vector3(0, verticalVelocity, 0) * Time.deltaTime);

        // 3. 攻擊輸入偵測
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
        {
            animator.SetTrigger(attackHash);
        }
    }
}