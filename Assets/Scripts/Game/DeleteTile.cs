using System;
using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems; // Necesario para eventos de UI

public class DeleteTile : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public static event Action OnDeleteTile;
    private WaveFunctionGame wfc;

    RectTransform rectTransform;
    private Vector3 originalScale;
    private Vector2 originalPosition;

    [SerializeField] private float bounceScale = 1.1f;
    [SerializeField] private float moveUpAmount = 10f;
    [SerializeField] private float duration = 0.2f;

    private void Start()
    {
        wfc = FindFirstObjectByType<WaveFunctionGame>();
        rectTransform = GetComponent<RectTransform>();
        originalScale = rectTransform.localScale;
        originalPosition = rectTransform.anchoredPosition;
    }
    public void OnPointerEnter(PointerEventData eventData)
    {

        //-------------ANIMACION CON DOTWEEN------------------------
        rectTransform = GetComponent<RectTransform>();


        // Do a little bounce and move up
        Sequence seq = DOTween.Sequence();
        seq.Append(rectTransform.DOScale(originalScale * bounceScale, duration).SetEase(Ease.OutBack));
        seq.Join(rectTransform.DOAnchorPosY(originalPosition.y + moveUpAmount, duration).SetEase(Ease.OutQuad));



        //-----------------ACCION------------------------------------
        if (Input.GetMouseButton(0) && wfc.actualTileDragged != null) //comprobar que tienes un objeto en la mano!
        {
            //Animacion
            rectTransform.DOKill(); // cancelar animaciones previas si hay
            rectTransform.DOShakeRotation(0.4f, new Vector3(0, 0, 20f), 10, 90f, false)
                         .SetEase(Ease.OutQuad).OnComplete(() => rectTransform.localRotation = Quaternion.Euler(Vector3.zero)); ;

            //Accion
            Debug.Log("Borrar tile");
            OnDeleteTile?.Invoke();
        }

    }

    public void OnPointerExit(PointerEventData eventData)
    {

        // Return to original position and scale
        Sequence seq = DOTween.Sequence();
        seq.Append(rectTransform.DOScale(originalScale, duration).SetEase(Ease.OutBack));
        seq.Join(rectTransform.DOAnchorPosY(originalPosition.y, duration).SetEase(Ease.OutQuad));
    }
}
