using System;
using System.Collections.Generic;

namespace UrsaMinorThrottle
{
    // Ignore buttons already held when capture starts; accept one new press only.
    internal sealed class ButtonDetector
    {
        private HashSet<string> previous;
        internal ButtonDetector(IEnumerable<string> held) { previous = new HashSet<string>(held ?? Array.Empty<string>()); }
        internal string Observe(IEnumerable<string> held, out bool ambiguous)
        {
            var current = new HashSet<string>(held ?? Array.Empty<string>());
            string selected = null;
            int count = 0;
            foreach (var key in current)
                if (!previous.Contains(key)) { selected = key; count++; }
            previous = current;
            ambiguous = count > 1;
            return count == 1 ? selected : null;
        }
    }
}
