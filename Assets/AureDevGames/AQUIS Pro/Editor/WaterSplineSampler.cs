#if UNITY_EDITOR
using UnityEngine;
using static AureDevGames.WaterSpline;

namespace AureDevGames
{
    public static class WaterSplineSampler
    {
        public static Vector3 SampleSegmentPosition(WaterSpline s, int segIdx, float localT)
        {
            if (s == null || s.points == null || s.points.Count == 0) return Vector3.zero;
            localT = Mathf.Clamp01(localT);
            int n = s.points.Count;
            int segCount = Mathf.Max(1, n - 1);
            segIdx = Mathf.Clamp(segIdx, 0, segCount - 1);
            if (!s.useBezier)
            {
                Vector3 p0 = s.points[segIdx];
                Vector3 p1 = s.points[segIdx + 1];
                return Vector3.Lerp(p0, p1, localT);
            }
            else
            {
                s.EnsureHandleCounts();
                Vector3 p0 = s.points[segIdx];
                Vector3 p1 = s.points[segIdx + 1];
                Vector3 c0 = s.outHandles[Mathf.Clamp(segIdx, 0, s.outHandles.Count - 1)];
                Vector3 c1 = s.inHandles[Mathf.Clamp(segIdx + 1, 0, s.inHandles.Count - 1)];
                return CubicBezier(p0, c0, c1, p1, localT);
            }
        }

        public static Vector3 SampleSegmentTangent(WaterSpline s, int segIdx, float localT)
        {
            if (s == null || s.points == null || s.points.Count < 2) return Vector3.forward;
            localT = Mathf.Clamp01(localT);
            int n = s.points.Count;
            int segCount = Mathf.Max(1, n - 1);
            segIdx = Mathf.Clamp(segIdx, 0, segCount - 1);
            if (!s.useBezier)
            {
                Vector3 p0 = s.points[segIdx];
                Vector3 p1 = s.points[segIdx + 1];
                Vector3 tan = p1 - p0;
                if (tan.sqrMagnitude < 1e-8f) return Vector3.forward;
                return tan.normalized;
            }
            else
            {
                s.EnsureHandleCounts();
                Vector3 p0 = s.points[segIdx];
                Vector3 p1 = s.points[segIdx + 1];
                Vector3 c0 = s.outHandles[Mathf.Clamp(segIdx, 0, s.outHandles.Count - 1)];
                Vector3 c1 = s.inHandles[Mathf.Clamp(segIdx + 1, 0, s.inHandles.Count - 1)];
                Vector3 tan = CubicBezierTangent(p0, c0, c1, p1, localT);
                if (tan.sqrMagnitude < 1e-8f)
                {
                    float eps = 1e-4f;
                    float t0 = Mathf.Max(0f, localT - eps);
                    float t1 = Mathf.Min(1f, localT + eps);
                    Vector3 pos0 = CubicBezier(p0, c0, c1, p1, t0);
                    Vector3 pos1 = CubicBezier(p0, c0, c1, p1, t1);
                    tan = pos1 - pos0;
                    if (tan.sqrMagnitude < 1e-8f)
                    {
                        tan = p1 - p0;
                        if (tan.sqrMagnitude < 1e-8f) return Vector3.forward;
                    }
                }
                return tan.normalized;
            }
        }

        public static float SampleSegmentWidth(WaterSpline s, int segIdx, float localT)
        {
            if (s == null || s.pointWidths == null || s.pointWidths.Count < 2) 
                return s != null ? s.width : 5f;

            localT = Mathf.Clamp01(localT);
            int n = s.pointWidths.Count;
            int segCount = Mathf.Max(1, n - 1);
            segIdx = Mathf.Clamp(segIdx, 0, segCount - 1);

            float w0 = s.pointWidths[segIdx];
            float w1 = s.pointWidths[segIdx + 1];
            return Mathf.Lerp(w0, w1, localT);
        }

        private static Vector3 CubicBezier(Vector3 p0, Vector3 c0, Vector3 c1, Vector3 p1, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * c0 + 3f * u * t * t * c1 + t * t * t * p1;
        }

        private static Vector3 CubicBezierTangent(Vector3 p0, Vector3 c0, Vector3 c1, Vector3 p1, float t)
        {
            float u = 1f - t;
            return 3f * u * u * (c0 - p0) + 6f * u * t * (c1 - c0) + 3f * t * t * (p1 - c1);
        }
    }
}
#endif
