using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using V8.Net;

namespace Raven.Server.Documents.Patch
{
    public class PoolWithLevels<TValue>
        where TValue : IDisposable, new()
    {
        public struct PooledValue : IDisposable
        {
            public TValue Value;
            private PoolWithLevels<TValue> _pool;

            public PooledValue(TValue value, PoolWithLevels<TValue> pool)
            {
                Value = value;
                _pool = pool;
                _pool.IncrementLevel(value);
            }

            public void Dispose()
            {
                _pool.DecrementLevel(Value);
            }
        }
        
        private readonly ReaderWriterLock _Locker = new ReaderWriterLock();
        
        private int _maxCapacity;
        private int _targetLevel;
        private SortedList<int, HashSet<TValue>> _listByLevel = new();
        private Dictionary<TValue, int> _objectLevels = new();
        
        public PoolWithLevels(int targetLevel, int maxCapacity)
        {
            _targetLevel = targetLevel;
            _maxCapacity = maxCapacity;
        }

        public PooledValue GetValue()
        {
            using (_Locker.WriteLock())
            {
                TValue obj = default;
                using (var it = _listByLevel.GetEnumerator())
                {
                    while (it.MoveNext())
                    {
                        var (level, set) = it.Current;
                        if (set.Count >= 1)
                        {
                            obj = (level >= _targetLevel && _listByLevel.Count < _maxCapacity) ? new TValue() : set.First();
                        }
                    }

                    if (obj == null)
                    {
                        obj = new TValue();
                    }

                    return new PooledValue(obj, this);
                }
            }
        }

        public void IncrementLevel(TValue obj)
        {
            ModifyLevel(obj, 1);
        }
        
        public void DecrementLevel(TValue obj)
        {
            using (_Locker.WriteLock())
            {
                ModifyLevel(obj, -1);
            }
        }
        
        private void ModifyLevel(TValue obj, int delta)
        {
            if (!_objectLevels.ContainsKey(obj))
            {
                _objectLevels[obj] = 0;
            }

            var level = _objectLevels[obj];

            if (delta == 0)
                return;

            if (_listByLevel.TryGetValue(level, out HashSet<TValue> setPrev))
            {
                setPrev.Remove(obj);
                // we don't remove the empty set as it will be used later and it is one per level on the whole raven server
                //if (setPrev.Count == 0)
                //    Remove(level);
            }

            int levelNew = level + delta;
            _objectLevels[obj] = levelNew;
            if (levelNew == 0)
            {
                obj.Dispose();
            }
            else
            {
                if (!_listByLevel.TryGetValue(levelNew, out HashSet<TValue> setNew))
                {
                    setNew = new HashSet<TValue>();
                    _listByLevel[levelNew] = setNew;
                }

                setNew.Add(obj);
            }
        }
    }
}
