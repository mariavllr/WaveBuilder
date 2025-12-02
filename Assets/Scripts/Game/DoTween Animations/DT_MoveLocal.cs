using UnityEngine;
using DG.Tweening;

public class DT_MoveLocal : MonoBehaviour
{
    [SerializeField] private GameObject element;
    [SerializeField] private Vector3 targetPosition;
    [SerializeField] private float duration = 0.5f;
    public bool startOnAwake = false;

    private Vector3 originalPosition;
    private Tween moveTween;

    private void Awake()
    {
        originalPosition = element.transform.localPosition;
        if(startOnAwake)
        {
            StartMove();
        }
    }

    public void StartMove()
    {
        // Si ya está corriendo, no lo recreamos
        if (moveTween != null && moveTween.IsActive()) return;

        moveTween = element.transform
            .DOLocalMove(originalPosition + targetPosition, duration)
            .SetLoops(-1, LoopType.Yoyo)   // loop infinito adelante/atrás
            .SetEase(Ease.InOutSine);
    }

    public void StopMove()
    {
        if (moveTween != null)
        {
            moveTween.Kill(); // detiene y destruye el tween
            moveTween = null;
        }

        // Asegurar que el botón vuelva a su escala original
        element.transform.localScale = originalPosition;
    }
}
