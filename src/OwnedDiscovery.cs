using System;
using System.Collections.Generic;
namespace UrsaMinorThrottle
{
    // Merge registrations and hierarchy fallback without duplicates or foreign engines.
    internal static class OwnedDiscovery
    {
        internal static List<T> Merge<T>(IEnumerable<T> registered, IEnumerable<T> children, Func<T, bool> belongs) where T : class
        {
            var result = new List<T>();
            var seen = new HashSet<T>();
            foreach (var source in new[] { registered, children })
            {
                if (source == null) continue;
                foreach (var item in source)
                    if (item != null && belongs(item) && seen.Add(item)) result.Add(item);
            }
            return result;
        }
    }
}
