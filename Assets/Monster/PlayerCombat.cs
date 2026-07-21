using UnityEngine;
using System.Collections;

public class AttackEffectManager : MonoBehaviour
{
    [Header("Particle Settings")]
    [Tooltip("將你的粒子特效放入此陣列")]
    [SerializeField] private ParticleSystem[] attackParticles;

    /// <summary>
    /// 給 Animation Event 呼叫的函數
    /// </summary>
    public void PlayAttackEffects()
    {
        if (attackParticles == null || attackParticles.Length == 0)
        {
            Debug.LogWarning("未指定攻擊特效組件！");
            return;
        }

        foreach (ParticleSystem ps in attackParticles)
        {
            if (ps != null)
            {
                // 停止之前的播放並重新播放，確保連擊時特效順暢
                ps.Stop();
                ps.Play();
            }
        }
    }
}