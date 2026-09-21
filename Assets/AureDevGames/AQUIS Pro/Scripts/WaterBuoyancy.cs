using System.Collections.Generic;
using UnityEngine;

namespace AureDevGames
{
    [RequireComponent(typeof(Rigidbody))]
    public class WaterBuoyancy : MonoBehaviour
    {
        [Header("Water Reference")]
        [Tooltip("The WaterWaves component driving the surface. If unassigned, the script will find the closest one.")]
        public WaterWaves waterComponent;

        [Header("Buoyancy Settings")]
        [Tooltip("Transforms where buoyancy force is applied. If empty, the object's center is used. For a boat, use 4 points at the bottom corners.")]
        public Transform[] floaters;
        
        [Tooltip("How much the object floats (upward force multiplier). Higher values make it pop up faster out of the water.")]
        public float buoyancyMultiplier = 15f;
        
        [Tooltip("Offsets the calculated water height. Use this to fine-tune the resting water line of the object.")]
        public float depthOffset = 0f;

        [Header("Water Resistance")]
        [Tooltip("Drag applied when the object is submerged in water to simulate resistance.")]
        public float waterDrag = 3f;
        [Tooltip("Angular drag applied when submerged to stabilize rotation.")]
        public float waterAngularDrag = 1f;

        [Header("Stability")]
        [Tooltip("Lowers the physical center of mass. This prevents the object from flipping upside down completely (like a pendulum). -1 or -2 usually works perfectly for boats.")]
        public float centerOfMassOffset = -1.5f;

        private Rigidbody rb;
        private float defaultDrag;
        private float defaultAngularDrag;
        private bool isUnderwater;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            defaultDrag = rb.linearDamping;
            defaultAngularDrag = rb.angularDamping;

            rb.centerOfMass = new Vector3(0, centerOfMassOffset, 0);

            if (waterComponent == null)
                FindClosestWater();

            if (floaters == null || floaters.Length == 0)
            {
                GameObject centerFloater = new GameObject("Center_Floater");
                centerFloater.transform.SetParent(transform);
                centerFloater.transform.localPosition = Vector3.zero;
                floaters = new Transform[] { centerFloater.transform };
            }
        }

        private void FindClosestWater()
        {
            WaterWaves[] allWaters = FindObjectsByType<WaterWaves>(FindObjectsSortMode.None);
            float closestDist = float.MaxValue;

            foreach (var w in allWaters)
            {
                float dist = Vector3.Distance(transform.position, w.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    waterComponent = w;
                }
            }
        }

        private void FixedUpdate()
        {
            if (waterComponent == null) return;

            int submergedCount = 0;

            float forcePerFloater = buoyancyMultiplier / floaters.Length;

            for (int i = 0; i < floaters.Length; i++)
            {
                Transform floater = floaters[i];

                float localWaterHeight = waterComponent.GetWaveHeight(floater.position) + depthOffset;
                float surfaceY = waterComponent.transform.position.y + localWaterHeight;

                float depth = floater.position.y - surfaceY;

                if (depth < 0f)
                {

                    float displacementMultiplier = Mathf.Clamp01(Mathf.Abs(depth) / 2f) * forcePerFloater;

                    Vector3 buoyancyForce = Vector3.up * (Mathf.Abs(Physics.gravity.y) * displacementMultiplier);

                    rb.AddForceAtPosition(buoyancyForce, floater.position, ForceMode.Acceleration);
                    submergedCount++;
                }
            }

            if (submergedCount > 0)
            {
                float submersionRatio = (float)submergedCount / floaters.Length;
                rb.linearDamping = Mathf.Lerp(defaultDrag, waterDrag, submersionRatio);
                rb.angularDamping = Mathf.Lerp(defaultAngularDrag, waterAngularDrag, submersionRatio);
                isUnderwater = true;
            }
            else if (isUnderwater)
            {

                rb.linearDamping = defaultDrag;
                rb.angularDamping = defaultAngularDrag;
                isUnderwater = false;
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (floaters == null) return;
            
            Gizmos.color = Color.yellow;
            foreach (var f in floaters)
            {
                if (f != null)
                    Gizmos.DrawSphere(f.position, 0.1f);
            }
        }
    }
}
