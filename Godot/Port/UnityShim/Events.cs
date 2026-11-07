using System;
using System.Collections.Generic;

namespace UnityEngine.Events
{
    // Unity's events for the shared core's GlobalEventDispatcher (the event bus every service uses): listeners in
    // the order they were added; a listener removed during Invoke is not called anymore.

    public delegate void UnityAction();
    public delegate void UnityAction<in T0>(T0 arg0);
    public delegate void UnityAction<in T0, in T1>(T0 arg0, T1 arg1);
    public delegate void UnityAction<in T0, in T1, in T2>(T0 arg0, T1 arg1, T2 arg2);
    public delegate void UnityAction<in T0, in T1, in T2, in T3>(T0 arg0, T1 arg1, T2 arg2, T3 arg3);

    public abstract class UnityEventBase
    {
        public virtual int GetPersistentEventCount() => 0;
    }

    public class UnityEventListeners<TAction> : UnityEventBase where TAction : Delegate
    {
        private readonly List<TAction> _listeners = new();
        private TAction[] _calling = Array.Empty<TAction>();
        private bool _isChanged = true;

        public void AddListener(TAction call)
        {
            _listeners.Add(call);
            _isChanged = true;
        }

        public void RemoveListener(TAction call)
        {
            _listeners.Remove(call);
            _isChanged = true;
        }

        public void RemoveAllListeners()
        {
            _listeners.Clear();
            _isChanged = true;
        }

        protected TAction[] Listeners
        {
            get
            {
                if (_isChanged)
                {
                    _calling = _listeners.ToArray();
                    _isChanged = false;
                }

                return _calling;
            }
        }

        protected bool IsStillListening(TAction call) => !_isChanged || _listeners.Contains(call);
    }

    public class UnityEvent : UnityEventListeners<UnityAction>
    {
        public void Invoke()
        {
            foreach (var call in Listeners)
                if (IsStillListening(call))
                    call();
        }
    }

    public class UnityEvent<T0> : UnityEventListeners<UnityAction<T0>>
    {
        public void Invoke(T0 arg0)
        {
            foreach (var call in Listeners)
                if (IsStillListening(call))
                    call(arg0);
        }
    }

    public class UnityEvent<T0, T1> : UnityEventListeners<UnityAction<T0, T1>>
    {
        public void Invoke(T0 arg0, T1 arg1)
        {
            foreach (var call in Listeners)
                if (IsStillListening(call))
                    call(arg0, arg1);
        }
    }

    public class UnityEvent<T0, T1, T2> : UnityEventListeners<UnityAction<T0, T1, T2>>
    {
        public void Invoke(T0 arg0, T1 arg1, T2 arg2)
        {
            foreach (var call in Listeners)
                if (IsStillListening(call))
                    call(arg0, arg1, arg2);
        }
    }

    public class UnityEvent<T0, T1, T2, T3> : UnityEventListeners<UnityAction<T0, T1, T2, T3>>
    {
        public void Invoke(T0 arg0, T1 arg1, T2 arg2, T3 arg3)
        {
            foreach (var call in Listeners)
                if (IsStillListening(call))
                    call(arg0, arg1, arg2, arg3);
        }
    }
}

namespace UnityEngine.EventSystems
{
    /// <summary>Only the namespace is used (GlobalEventDispatcher's usings).</summary>
    public class BaseEventData
    {
    }
}
