using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.VFX; // 必須引用此命名空間
using System.Collections;

[RequireComponent(typeof(Animator))]
public class PlayerCombat : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private float attackCooldown = 1.0f;

    [Header("Visual Effects")]
    [Tooltip("傳統粒子特效")]
    [SerializeField] private ParticleSystem[] attackParticles;

    [Tooltip("VFX Graph 特效")]
    [SerializeField] private VisualEffect[] attackVfxs;

    private Animator animator;
    private bool isAttacking;
    private readonly int attackHash = Animator.StringToHash("Attack");

    void Start()
    {
        animator = GetComponent<Animator>();
    }

    void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.spaceKey.wasPressedThisFrame && !isAttacking)
        {
            animator.SetTrigger(attackHash);
            StartCoroutine(ResetAttack());
        }
    }

    // --- 給 Animation Event 呼叫的函數 ---
    public void PlayAttackEffects()
    {
        // 處理 ParticleSystem
        if (attackParticles != null)
        {
            foreach (ParticleSystem ps in attackParticles)
            {
                if (ps != null)
                {
                    ps.Stop();
                    ps.Play();
                }
            }
        }

        // 處理 VFX Graph
        if (attackVfxs != null)
        {
            foreach (VisualEffect vfx in attackVfxs)
            {
                if (vfx != null)
                {
                    // 若特效有設定 Spawn 規則，直接呼叫 Play() 即可
                    vfx.Play();

                    // 如果你的 VFX 是透過 Event 觸發 (例如 "OnPlay")，請改用下面這行：
                    // vfx.SendEvent("OnPlay");
                }
            }
        }
    }

    private IEnumerator ResetAttack()
    {
        isAttacking = true;
        yield return new WaitForSeconds(attackCooldown);
        isAttacking = false;
    }
}