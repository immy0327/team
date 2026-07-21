using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.VFX;
using System.Collections;

[RequireComponent(typeof(Animator))]
public class PlayerCombat : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private float attackCooldown = 1.0f;
    [SerializeField] private string idleStateName = "Monster_Fighting_Ide";
    [SerializeField] private float idleTransitionDuration = 0.1f;
    [SerializeField] private bool allowKeyboardAttack = true;

    [Header("Visual Effects")]
    [SerializeField] private ParticleSystem[] attackParticles;
    [SerializeField] private VisualEffect[] attackVfxs;

    private Animator animator;
    private bool isAttacking;
    private bool autoAttack;
    private Coroutine attackCooldownRoutine;
    private readonly int attackHash = Animator.StringToHash("Attack");
    private int idleStateHash;

    private void Awake()
    {
        animator = GetComponent<Animator>();
        idleStateHash = Animator.StringToHash(idleStateName);
        StopAttackEffects();
    }

    private void OnEnable()
    {
        if (!autoAttack)
        {
            ReturnToIdle();
            StopAttackEffects();
        }
    }

    private void OnDisable()
    {
        autoAttack = false;
        isAttacking = false;

        if (attackCooldownRoutine != null)
        {
            StopCoroutine(attackCooldownRoutine);
            attackCooldownRoutine = null;
        }

        StopAttackEffects();
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (allowKeyboardAttack && keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
        {
            TryStartAttack();
        }

        if (autoAttack)
        {
            TryStartAttack();
        }
    }

    public void SetBattleAttacking(bool attacking)
    {
        if (autoAttack == attacking)
        {
            return;
        }

        autoAttack = attacking;
        if (autoAttack)
        {
            TryStartAttack();
            return;
        }

        if (attackCooldownRoutine != null)
        {
            StopCoroutine(attackCooldownRoutine);
            attackCooldownRoutine = null;
        }

        isAttacking = false;
        animator.ResetTrigger(attackHash);
        ReturnToIdle();
        StopAttackEffects();
    }

    public void ReturnToIdle()
    {
        if (!animator || !animator.runtimeAnimatorController)
        {
            return;
        }

        if (!animator.HasState(0, idleStateHash))
        {
            return;
        }

        animator.CrossFade(idleStateHash, idleTransitionDuration, 0, 0f);
    }

    public void PlayAttackEffects()
    {
        if (attackParticles != null)
        {
            foreach (ParticleSystem ps in attackParticles)
            {
                if (ps != null)
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Play();
                }
            }
        }

        if (attackVfxs != null)
        {
            foreach (VisualEffect vfx in attackVfxs)
            {
                if (vfx != null)
                {
                    vfx.Stop();
                    vfx.Play();
                }
            }
        }
    }

    private void TryStartAttack()
    {
        if (isAttacking)
        {
            return;
        }

        animator.SetTrigger(attackHash);
        attackCooldownRoutine = StartCoroutine(ResetAttack());
    }

    private void StopAttackEffects()
    {
        if (attackParticles != null)
        {
            foreach (ParticleSystem ps in attackParticles)
            {
                if (ps != null)
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }
        }

        if (attackVfxs != null)
        {
            foreach (VisualEffect vfx in attackVfxs)
            {
                if (vfx != null)
                {
                    vfx.Stop();
                }
            }
        }
    }

    private IEnumerator ResetAttack()
    {
        isAttacking = true;
        yield return new WaitForSeconds(attackCooldown);
        isAttacking = false;
        attackCooldownRoutine = null;
    }
}
