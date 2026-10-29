namespace UrsaMinorThrottle
{
    internal static class EngineRouting
    {
        // Reversing thrust must never drive a conventional, non-reversing engine forward.
        internal static float Power(float x, float left, float right, bool reverse, bool supportsNativeReverse)
        {
            if (reverse && !supportsNativeReverse) return 0f;
            return x < -0.1f ? left : x > 0.1f ? right : (left + right) * 0.5f;
        }
    }
}
