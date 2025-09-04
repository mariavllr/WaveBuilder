using UnityEngine;
using DG.Tweening;

public class DT_HighlightObject : MonoBehaviour
{
    [SerializeField] private GameObject element;
    [SerializeField] private float scaleUp = 1.2f;   // cuánto crece
    [SerializeField] private float duration = 0.5f;  // velocidad del pulso
    public bool startOnAwake = false;

    private Vector3 originalScale;
    private Tween pulseTween;

    private void Awake()
    {
        originalScale = element.transform.localScale;
        if(startOnAwake)
        {
            StartPulse();
        }
    }

    public void StartPulse()
    {
        // Si ya está corriendo, no lo recreamos
        if (pulseTween != null && pulseTween.IsActive()) return;

        pulseTween = element.transform
            .DOScale(originalScale * scaleUp, duration)
            .SetLoops(-1, LoopType.Yoyo)   // loop infinito adelante/atrás
            .SetEase(Ease.InOutSine);
    }

    public void StopPulse()
    {
        if (pulseTween != null)
        {
            pulseTween.Kill(); // detiene y destruye el tween
            pulseTween = null;
        }

        // Asegurar que el botón vuelva a su escala original
        element.transform.localScale = originalScale;
    }
}
