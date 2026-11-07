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

namespace MyBox
{
    // MyBox's inspector attributes (DeveloperConfig): only the Unity inspector reads them.

    [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Property, AllowMultiple = true)]
    public class ConditionalFieldAttribute : System.Attribute
    {
        public ConditionalFieldAttribute(string fieldToCheck = null, bool inverse = false, params object[] compareValues)
        {
        }

        public ConditionalFieldAttribute(string[] fieldToCheck, bool[] inverse = null, params object[] compare)
        {
        }

        public ConditionalFieldAttribute(bool useMethod, string method, bool inverse = false)
        {
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Property, AllowMultiple = true)]
    public class SeparatorAttribute : System.Attribute
    {
        public SeparatorAttribute(string title = null, bool withOffset = false)
        {
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Property)]
    public class OverrideLabelAttribute : System.Attribute
    {
        public OverrideLabelAttribute(string label)
        {
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Property)]
    public class FoldoutAttribute : System.Attribute
    {
        public FoldoutAttribute(string name, bool foldEverything = false, bool styled = false, bool readOnly = false)
        {
        }
    }

    /// <summary>MyBox's serializable array wrapper.</summary>
    [System.Serializable]
    public class CollectionWrapper<T>
    {
        public T[] Value = System.Array.Empty<T>();
    }
}
