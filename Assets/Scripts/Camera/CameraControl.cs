using System.Collections;
using UnityEngine;

public class CameraRotator : MonoBehaviour
{
    public float rotationSpeed = 400f;
    private bool isRotating = false;

    public void RotateLeft()
    {
        if (!isRotating)
            StartCoroutine(RotateToAngle(90));
    }

    public void RotateRight()
    {
        if (!isRotating)
            StartCoroutine(RotateToAngle(-90));
    }

    private IEnumerator RotateToAngle(float angle)
    {
        isRotating = true;

        Quaternion startRot = transform.rotation;
        Quaternion endRot = Quaternion.Euler(0, transform.eulerAngles.y + angle, 0);

        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime * (rotationSpeed / 90f);
            transform.rotation = Quaternion.Slerp(startRot, endRot, t);
            yield return null;
        }

        transform.rotation = endRot;
        isRotating = false;
    }
}
