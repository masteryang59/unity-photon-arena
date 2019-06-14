using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PhotonArena.Core.Net
{
    /// <summary>
    /// Custom room/player properties, the same idea as Photon's Hashtable but limited to types every
    /// backend can carry: int, bool, string and float. Setting a key to <c>null</c> removes it, as in Photon.
    /// </summary>
    public sealed class PropertyBag : IEnumerable<KeyValuePair<string, object>>
    {
        public const int MaxKeyLength = 32;
        public const int MaxStringLength = 256;

        readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.Ordinal);

        public int Count => _values.Count;
        public IEnumerable<string> Keys => _values.Keys;

        public PropertyBag Set(string key, object value)
        {
            ValidateKey(key);
            _values[key] = Normalize(value);
            return this;
        }

        public bool ContainsKey(string key) => _values.ContainsKey(key);

        /// <summary>Raw value; null means "absent" in a stored bag and "delete" in a change set.</summary>
        public object this[string key] => _values.TryGetValue(key, out object v) ? v : null;

        public bool Remove(string key) => _values.Remove(key);

        public int GetInt(string key, int fallback = 0) =>
            _values.TryGetValue(key, out object v) && v is int i ? i : fallback;

        public bool GetBool(string key, bool fallback = false) =>
            _values.TryGetValue(key, out object v) && v is bool b ? b : fallback;

        public float GetFloat(string key, float fallback = 0f) =>
            _values.TryGetValue(key, out object v) ? v switch { float f => f, int i => i, _ => fallback } : fallback;

        public string GetString(string key, string fallback = null) =>
            _values.TryGetValue(key, out object v) && v is string s ? s : fallback;

        /// <summary>Applies a change set: null values delete keys. Returns true if anything changed.</summary>
        public bool Merge(PropertyBag changes)
        {
            if (changes == null) return false;
            bool changed = false;
            foreach (var kv in changes._values)
            {
                if (kv.Value == null)
                {
                    changed |= _values.Remove(kv.Key);
                }
                else if (!_values.TryGetValue(kv.Key, out object old) || !ValuesEqual(old, kv.Value))
                {
                    _values[kv.Key] = kv.Value;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>
        /// Compare-and-swap check, like Photon's expectedProperties: every expected key must currently hold
        /// the expected value (an expected null means the key must be absent).
        /// </summary>
        public bool Matches(PropertyBag expected)
        {
            if (expected == null) return true;
            foreach (var kv in expected._values)
            {
                _values.TryGetValue(kv.Key, out object current);
                if (!ValuesEqual(current, kv.Value)) return false;
            }
            return true;
        }

        public PropertyBag Clone()
        {
            var copy = new PropertyBag();
            foreach (var kv in _values) copy._values[kv.Key] = kv.Value;
            return copy;
        }

        /// <summary>A copy with only the given keys (e.g. the lobby-visible subset of room properties).</summary>
        public PropertyBag Filter(IEnumerable<string> keys)
        {
            var copy = new PropertyBag();
            if (keys == null) return copy;
            foreach (string key in keys)
            {
                if (key != null && _values.TryGetValue(key, out object v)) copy._values[key] = v;
            }
            return copy;
        }

        public static bool ValuesEqual(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            return a.Equals(b);
        }

        public static PropertyBag Of(params object[] keyValues)
        {
            if (keyValues.Length % 2 != 0) throw new ArgumentException("Expected key/value pairs.", nameof(keyValues));
            var bag = new PropertyBag();
            for (int i = 0; i < keyValues.Length; i += 2) bag.Set((string)keyValues[i], keyValues[i + 1]);
            return bag;
        }

        static void ValidateKey(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("Property keys can't be empty.");
            if (key.Length > MaxKeyLength) throw new ArgumentException($"Property key '{key}' is longer than {MaxKeyLength}.");
        }

        static object Normalize(object value)
        {
            switch (value)
            {
                case null: return null;
                case int _:
                case bool _:
                case float _:
                    return value;
                case string s:
                    if (s.Length > MaxStringLength) throw new ArgumentException($"String property longer than {MaxStringLength}.");
                    return s;
                case byte b: return (int)b;
                case short sh: return (int)sh;
                case double d: return (float)d;
                default:
                    throw new ArgumentException($"Unsupported property type {value.GetType().Name}; use int, bool, float or string.");
            }
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString()
        {
            var sb = new StringBuilder("{");
            bool first = true;
            var keys = new List<string>(_values.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                if (!first) sb.Append(", ");
                first = false;
                object v = _values[key];
                sb.Append(key).Append('=').Append(v is float f ? f.ToString("0.###", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture));
            }
            return sb.Append('}').ToString();
        }
    }
}


    public static object SyncRecords(object data = null)
    {
        if (data == null) return null;
        return data;
    }
