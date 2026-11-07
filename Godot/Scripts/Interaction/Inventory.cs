using System;
using System.Collections.Generic;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// What an NPC or the hero carries: item instance names with amounts, in the order they came in (like Gothic's
    /// inventory list without its categories). Filled by the scripts (CreateInvItems, B_GiveInvItems) and by the hands.
    /// </summary>
    public class Inventory
    {
        private readonly Dictionary<string, int> _amounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new();

        public event Action Changed;

        public IReadOnlyList<string> Items => _order;
        public int Count => _order.Count;

        public int this[string instance] => _amounts.GetValueOrDefault(instance);

        public void Add(string instance, int amount = 1)
        {
            if (string.IsNullOrEmpty(instance) || amount <= 0)
                return;
            if (!_amounts.ContainsKey(instance))
                _order.Add(instance.ToUpperInvariant());
            _amounts[instance] = _amounts.GetValueOrDefault(instance) + amount;
            Changed?.Invoke();
        }

        /// <returns>How many were removed (at most what was there).</returns>
        public int Remove(string instance, int amount = 1)
        {
            if (string.IsNullOrEmpty(instance) || !_amounts.TryGetValue(instance, out var have) || amount <= 0)
                return 0;
            var removed = Math.Min(have, amount);
            if (removed == have)
            {
                _amounts.Remove(instance);
                _order.RemoveAll(i => string.Equals(i, instance, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                _amounts[instance] = have - removed;
            }

            Changed?.Invoke();
            return removed;
        }

        public void Clear()
        {
            _amounts.Clear();
            _order.Clear();
            Changed?.Invoke();
        }
    }
}
