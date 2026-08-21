using System.Collections.Generic;

namespace WrenBot.PathFinding
{
    public static class Extensions
    {
        public static void Push<T>(this List<T> source, T item)
        {
            source.Add(item);
        }
    }
}
