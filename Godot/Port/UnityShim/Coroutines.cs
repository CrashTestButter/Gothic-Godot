using System;
using System.Collections;
using System.Collections.Generic;

namespace UnityEngine
{
    // Unity's coroutines for the shared core (GameTimeService's clock, SaveGameService's end-of-frame save, ...):
    // stepped once per Godot frame by Time.Advance. Supported yields: null and WaitForEndOfFrame (next frame),
    // WaitForSeconds / WaitForSecondsRealtime, CustomYieldInstruction (WaitUntil, WaitWhile), a nested IEnumerator
    // and another Coroutine.

    public class YieldInstruction
    {
    }

    public sealed class WaitForEndOfFrame : YieldInstruction
    {
    }

    public sealed class WaitForFixedUpdate : YieldInstruction
    {
    }

    public sealed class WaitForSeconds : YieldInstruction
    {
        public readonly float Seconds;

        public WaitForSeconds(float seconds) => Seconds = seconds;
    }

    public abstract class CustomYieldInstruction : IEnumerator
    {
        public abstract bool keepWaiting { get; }
        public object Current => null;
        public bool MoveNext() => keepWaiting;
        public virtual void Reset()
        {
        }
    }

    public sealed class WaitForSecondsRealtime : CustomYieldInstruction
    {
        private readonly float _until;

        public WaitForSecondsRealtime(float seconds) => _until = Time.realtimeSinceStartup + seconds;
        public override bool keepWaiting => Time.realtimeSinceStartup < _until;
    }

    public sealed class WaitUntil : CustomYieldInstruction
    {
        private readonly Func<bool> _predicate;

        public WaitUntil(Func<bool> predicate) => _predicate = predicate;
        public override bool keepWaiting => !_predicate();
    }

    public sealed class WaitWhile : CustomYieldInstruction
    {
        private readonly Func<bool> _predicate;

        public WaitWhile(Func<bool> predicate) => _predicate = predicate;
        public override bool keepWaiting => _predicate();
    }

    public sealed class Coroutine : YieldInstruction
    {
        internal readonly Stack<IEnumerator> Stack = new();
        internal readonly object Owner;
        internal float WaitUntilTime;
        internal CustomYieldInstruction WaitingFor;
        internal Coroutine WaitingForRoutine;

        internal Coroutine(IEnumerator routine, object owner)
        {
            Stack.Push(routine);
            Owner = owner;
        }

        public bool IsDone => Stack.Count == 0;
    }

    public static class CoroutineRunner
    {
        private static readonly List<Coroutine> _running = new();
        private static readonly List<Coroutine> _stepping = new();

        public static int Count => _running.Count;

        public static Coroutine Start(IEnumerator routine, object owner)
        {
            if (routine == null)
                return null;
            var coroutine = new Coroutine(routine, owner);
            _running.Add(coroutine);
            // Like Unity: runs up to its first yield at once.
            Step(coroutine);
            return coroutine;
        }

        public static void Stop(Coroutine coroutine)
        {
            if (coroutine == null)
                return;
            coroutine.Stack.Clear();
            _running.Remove(coroutine);
        }

        public static void StopAll(object owner) => _running.RemoveAll(c => ReferenceEquals(c.Owner, owner));

        /// <summary>Once per frame, after Time moved on.</summary>
        public static void StepAll()
        {
            _stepping.Clear();
            _stepping.AddRange(_running);
            foreach (var coroutine in _stepping)
                Step(coroutine);
            _running.RemoveAll(c => c.IsDone);
        }

        private static void Step(Coroutine coroutine)
        {
            if (coroutine.IsDone || Time.time < coroutine.WaitUntilTime)
                return;
            if (coroutine.WaitingFor != null)
            {
                if (coroutine.WaitingFor.keepWaiting)
                    return;
                coroutine.WaitingFor = null;
            }

            if (coroutine.WaitingForRoutine != null)
            {
                if (!coroutine.WaitingForRoutine.IsDone)
                    return;
                coroutine.WaitingForRoutine = null;
            }

            while (coroutine.Stack.Count > 0)
            {
                var top = coroutine.Stack.Peek();
                if (!top.MoveNext())
                {
                    coroutine.Stack.Pop();
                    continue;
                }

                switch (top.Current)
                {
                    case WaitForSeconds wait:
                        coroutine.WaitUntilTime = Time.time + wait.Seconds;
                        return;
                    case CustomYieldInstruction custom:
                        coroutine.WaitingFor = custom;
                        return;
                    case Coroutine other:
                        coroutine.WaitingForRoutine = other;
                        return;
                    case IEnumerator nested:
                        coroutine.Stack.Push(nested);
                        continue;
                    default:
                        // null, WaitForEndOfFrame, anything else: the next frame.
                        return;
                }
            }
        }
    }
}
