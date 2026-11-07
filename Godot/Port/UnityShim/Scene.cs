using System;
using System.Collections.Generic;

namespace UnityEngine
{
    // Inert stand-ins for Unity's scene types. They let the shared core's models compile (NpcContainer.Go,
    // properties classes deriving from MonoBehaviour); nothing on the Godot side drives them. Core files that really
    // work with scene objects are not compiled into the Godot build.

    public class Object
    {
        public string name { get; set; }

        public static bool operator ==(Object a, Object b) => ReferenceEquals(a, b);
        public static bool operator !=(Object a, Object b) => !ReferenceEquals(a, b);
        public static implicit operator bool(Object o) => !ReferenceEquals(o, null);
        public override bool Equals(object obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => base.GetHashCode();
    }

    public class Component : Object
    {
        public GameObject gameObject { get; set; }
        public Transform transform => gameObject?.transform;
    }

    public class Behaviour : Component
    {
        public bool enabled { get; set; } = true;
    }

    public class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(System.Collections.IEnumerator routine) => CoroutineRunner.Start(routine, this);
        public void StopCoroutine(Coroutine routine) => CoroutineRunner.Stop(routine);
        public void StopAllCoroutines() => CoroutineRunner.StopAll(this);
    }

    public class ScriptableObject : Object
    {
    }

    public class GameObject : Object
    {
        public GameObject()
        {
            transform = new Transform { gameObject = this };
        }

        public GameObject(string name) : this()
        {
            this.name = name;
        }

        public Transform transform { get; }
        public bool activeSelf { get; private set; } = true;
        public bool activeInHierarchy => activeSelf;
        public void SetActive(bool value) => activeSelf = value;
    }

    public class Transform : Component, IEnumerable<Transform>
    {
        private readonly List<Transform> _children = new();
        private Transform _parent;

        public Vector3 position { get; set; }
        public Quaternion rotation { get; set; } = Quaternion.identity;
        public Vector3 localPosition { get; set; }
        public Quaternion localRotation { get; set; } = Quaternion.identity;
        public Vector3 localScale { get; set; } = Vector3.one;
        public Vector3 forward => rotation * Vector3.forward;
        public Vector3 right => rotation * Vector3.right;
        public Vector3 up => rotation * Vector3.up;
        public int childCount => _children.Count;

        public Transform parent
        {
            get => _parent;
            set => SetParent(value);
        }

        public void SetParent(Transform newParent)
        {
            _parent?._children.Remove(this);
            _parent = newParent;
            _parent?._children.Add(this);
        }

        public Transform GetChild(int index) => _children[index];
        public IEnumerator<Transform> GetEnumerator() => _children.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Inspector attributes the core uses.
    [AttributeUsage(AttributeTargets.Field)] public class SerializeField : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public class HideInInspector : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public class SerializeReference : Attribute { }
    [AttributeUsage(AttributeTargets.All)] public class TooltipAttribute : Attribute { public TooltipAttribute(string tooltip) { } }
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)] public class HeaderAttribute : Attribute { public HeaderAttribute(string header) { } }
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)] public class SpaceAttribute : Attribute { public SpaceAttribute() { } public SpaceAttribute(float height) { } }
    [AttributeUsage(AttributeTargets.Field)] public class RangeAttribute : Attribute { public RangeAttribute(float min, float max) { } }
    [AttributeUsage(AttributeTargets.Field)] public class MinAttribute : Attribute { public MinAttribute(float min) { } }
    [AttributeUsage(AttributeTargets.Field)] public class TextAreaAttribute : Attribute { public TextAreaAttribute() { } public TextAreaAttribute(int min, int max) { } }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)] public class RequireComponent : Attribute { public RequireComponent(Type type) { } }
    [AttributeUsage(AttributeTargets.Class)] public class DisallowMultipleComponent : Attribute { }
    [AttributeUsage(AttributeTargets.Class)] public class CreateAssetMenuAttribute : Attribute { public string fileName { get; set; } public string menuName { get; set; } public int order { get; set; } }
    [AttributeUsage(AttributeTargets.Method)] public class ContextMenu : Attribute { public ContextMenu(string name) { } }
    [AttributeUsage(AttributeTargets.Method)] public class HideInCallstackAttribute : Attribute { }
}

namespace UberLogger
{
    /// <summary>UberLogger's channel logging, routed to UnityEngine.Debug's sinks.</summary>
    public static class UberDebug
    {
        public static void LogChannel(string channel, string message, params object[] par) =>
            UnityEngine.Debug.LogSink($"[{channel}] {message}");
        public static void LogWarningChannel(string channel, string message, params object[] par) =>
            UnityEngine.Debug.WarningSink($"[{channel}] {message}");
        public static void LogErrorChannel(string channel, string message, params object[] par) =>
            UnityEngine.Debug.ErrorSink($"[{channel}] {message}");
    }

    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class StackTraceIgnore : System.Attribute
    {
    }
}
