using UnityEngine;
using UnityEngine.EventSystems;
using DG.Tweening;

public class DT_OpenPanel : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
    private RectTransform rect;
    private Vector2 closedPos;     // posición cerrada (pestaña visible)
    private Vector2 openPos;       // posición abierta (panel desplegado)

    [Header("Offsets")]
    public float hoverOffset = 20f;   // cuanto se mueve al hacer hover
    public float openOffset = 400f;   // cuanto se desplaza al abrir

    [Header("Duraciones")]
    public float hoverDuration = 0.2f;
    public float openDuration = 0.4f;

    private bool isOpen = false;

    void Awake()
    {
        rect = GetComponent<RectTransform>();
        closedPos = rect.anchoredPosition;
        openPos = closedPos + new Vector2(openOffset, 0f);
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (isOpen) return; // si está abierto, no hacer hover

        rect.DOKill();
        rect.DOAnchorPosX(closedPos.x + hoverOffset, hoverDuration).SetEase(Ease.OutQuad);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (isOpen) return;

        rect.DOKill();
        rect.DOAnchorPosX(closedPos.x, hoverDuration).SetEase(Ease.OutQuad);
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        rect.DOKill();

        if (!isOpen)
        {
            // abrir panel
            rect.DOAnchorPosX(openPos.x, openDuration).SetEase(Ease.OutBack);
            isOpen = true;
        }
        else
        {
            // cerrar panel
            rect.DOAnchorPosX(closedPos.x, openDuration).SetEase(Ease.OutBack);
            isOpen = false;
        }
    }
}
