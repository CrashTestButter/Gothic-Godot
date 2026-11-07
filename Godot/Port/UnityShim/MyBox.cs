using System.Collections.Generic;
using System.Linq;

namespace MyBox
{
    /// <summary>The MyBox collection and string helpers the shared core uses.</summary>
    public static class MyCollections
    {
        private static readonly System.Random _random = new();

        public static bool IsNullOrEmpty(this string value) => string.IsNullOrEmpty(value);
        public static bool NotNullOrEmpty(this string value) => !string.IsNullOrEmpty(value);
        public static bool IsNullOrEmpty<T>(this ICollection<T> collection) => collection == null || collection.Count == 0;
        public static bool NotNullOrEmpty<T>(this ICollection<T> collection) => !collection.IsNullOrEmpty();
        public static bool IsNullOrEmpty<T>(this T[] array) => array == null || array.Length == 0;
        public static bool IsEmpty<T>(this IEnumerable<T> collection) => !collection.Any();
        public static T GetRandom<T>(this IList<T> collection) => collection[_random.Next(collection.Count)];
        public static T GetRandom<T>(this T[] array) => array[_random.Next(array.Length)];
    }
}
