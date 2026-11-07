using System;
using System.Collections.Generic;
using System.Reflection;

namespace Reflex.Attributes
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method)]
    public class InjectAttribute : Attribute
    {
    }
}

namespace Reflex.Core
{
    /// <summary>
    /// The part of Reflex the shared core uses: a container of singletons, resolved by type, and [Inject] on fields
    /// and properties. The Godot side registers the services at startup.
    /// </summary>
    public class Container
    {
        private readonly Dictionary<Type, object> _singletons = new();
        private readonly Container _parent;

        public Container(Container parent = null)
        {
            _parent = parent;
        }

        public void Register<T>(T instance) => _singletons[typeof(T)] = instance;
        public void Register(Type type, object instance) => _singletons[type] = instance;

        public T Resolve<T>() => (T)Resolve(typeof(T));

        public object Resolve(Type type)
        {
            if (_singletons.TryGetValue(type, out var instance))
                return instance;
            if (_parent != null)
                return _parent.Resolve(type);
            throw new InvalidOperationException($"Reflex shim: {type.FullName} is not registered.");
        }

        public bool TryResolve(Type type, out object instance)
        {
            if (_singletons.TryGetValue(type, out instance))
                return true;
            return _parent != null && _parent.TryResolve(type, out instance);
        }

        public bool HasBinding(Type type) => TryResolve(type, out _);
    }
}

namespace Reflex.Injectors
{
    public static class AttributeInjector
    {
        private const BindingFlags _members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Inject(object target, Reflex.Core.Container container)
        {
            for (var type = target.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(_members | BindingFlags.DeclaredOnly))
                {
                    if (field.IsDefined(typeof(Reflex.Attributes.InjectAttribute)) &&
                        container.TryResolve(field.FieldType, out var value))
                        field.SetValue(target, value);
                }

                foreach (var property in type.GetProperties(_members | BindingFlags.DeclaredOnly))
                {
                    if (property.IsDefined(typeof(Reflex.Attributes.InjectAttribute)) && property.CanWrite &&
                        container.TryResolve(property.PropertyType, out var value))
                        property.SetValue(target, value);
                }
            }
        }
    }
}
