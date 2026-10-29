using System;
using System.Collections.Generic;

namespace UrsaMinorThrottle
{
    // Compare physical axis positions, ignoring button events and already-selected axes.
    internal sealed class AxisDetector
    {
        private readonly Dictionary<string, float> baseline;
        private readonly string excluded;
        private const float MinimumMovement = 0.25f;

        internal AxisDetector(IDictionary<string, float> initial, string excludedKey)
        {
            baseline = new Dictionary<string, float>(initial);
            excluded = excludedKey;
        }

        internal bool TrySelect(IDictionary<string, float> current, out string selected, out bool ambiguous)
        {
            selected = null;
            ambiguous = false;
            float best = 0f, second = 0f;
            foreach (var item in current)
            {
                if (item.Key == excluded || !baseline.TryGetValue(item.Key, out var start) ||
                    float.IsNaN(item.Value) || float.IsInfinity(item.Value) ||
                    float.IsNaN(start) || float.IsInfinity(start)) continue;
                float delta = Math.Abs(item.Value - start);
                if (delta > best) { second = best; best = delta; selected = item.Key; }
                else if (delta > second) second = delta;
            }
            if (best < MinimumMovement) { selected = null; return false; }
            if (second >= MinimumMovement && second >= best * 0.75f)
            {
                selected = null;
                ambiguous = true;
                return false;
            }
            return true;
        }
    }
}
