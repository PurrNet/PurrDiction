#if UNITY_PHYSICS_2D
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PurrNet.Prediction
{
    // A PlatformEffector2D decides whether a body passes through a one-way surface once, when the contact
    // begins, and keeps that decision in the contact. A rollback restores poses without those contacts, so
    // Unity rebuilds them at the restored pose and judges them again: a body restored more than halfway
    // through a platform is then pushed out on top. This keeps, per tick, the effector pairs a body was
    // passing through and holds them apart with IgnoreCollision after a restore until they separate.
    internal sealed class OneWayContactHistory2D
    {
        private struct Pair
        {
            public Collider2D Mine;
            public Collider2D Other;

            public bool Matches(Collider2D a, Collider2D b) => Mine == a && Other == b;
        }

        private struct Slot
        {
            public ulong Tick;
            public bool Valid;
            public int Count;
            public Pair[] Pairs;
        }

        private readonly Rigidbody2D _body;
        private readonly Slot[] _slots;
        private readonly List<Pair> _passing = new List<Pair>();
        private ContactPoint2D[] _contacts = new ContactPoint2D[16];

        public OneWayContactHistory2D(Rigidbody2D body, int capacity)
        {
            _body = body;
            _slots = new Slot[Mathf.Max(1, capacity)];
        }

        // Call with the state that enters the tick, after the previous physics step.
        public void Record(ulong tick)
        {
            ref var slot = ref _slots[tick % (ulong)_slots.Length];

            // After a restore the contacts still describe the pose before it until the next step.
            if (_restoredSinceStep && slot.Valid && slot.Tick == tick)
                return;

            slot.Tick = tick;
            slot.Valid = true;
            slot.Count = 0;

            // A pair held apart has no contact, but it is still passing through.
            for (var i = 0; i < _passing.Count; i++)
                Add(ref slot, _passing[i].Mine, _passing[i].Other);

            if (_restoredSinceStep)
                return;

            int count = ReadContacts();
            for (var i = 0; i < count; i++)
            {
                if (TryGetDisabledEffectorPair(_contacts[i], out var mine, out var other))
                    Add(ref slot, mine, other);
            }
        }

        // Call after the body's pose for the tick was restored, before the next physics step.
        public void Restore(ulong tick)
        {
            ref var slot = ref _slots[tick % (ulong)_slots.Length];
            bool known = slot.Valid && slot.Tick == tick;
            _restoredSinceStep = true;

            for (var i = _passing.Count - 1; i >= 0; i--)
            {
                var pair = _passing[i];
                if (known && Contains(ref slot, pair.Mine, pair.Other))
                    continue;
                Release(pair);
                _passing.RemoveAt(i);
            }

            if (!known)
                return;

            // A contact the body was passing through before the restore survives it while the shapes stay
            // close, and would keep the body passing through where the tick says it collides. Unity only
            // drops a contact immediately when one of its colliders is disabled, so rebuild that collider.
            int count = ReadContacts();
            for (var i = 0; i < count; i++)
            {
                if (!TryGetDisabledEffectorPair(_contacts[i], out var mine, out var other) ||
                    Contains(ref slot, mine, other) || !mine.enabled)
                    continue;
                mine.enabled = false;
                mine.enabled = true;
            }

            for (var i = 0; i < slot.Count; i++)
            {
                var pair = slot.Pairs[i];
                if (!pair.Mine || !pair.Other)
                    continue;
                Physics2D.IgnoreCollision(pair.Mine, pair.Other, true);
                if (!IsPassing(pair.Mine, pair.Other))
                    _passing.Add(pair);
            }
        }

        private bool _restoredSinceStep;

        private int ReadContacts()
        {
            if (!_body)
                return 0;

            int count = _body.GetContacts(_contacts);
            while (count == _contacts.Length)
            {
                _contacts = new ContactPoint2D[_contacts.Length * 2];
                count = _body.GetContacts(_contacts);
            }
            return count;
        }

        private bool TryGetDisabledEffectorPair(in ContactPoint2D contact, out Collider2D mine, out Collider2D other)
        {
            mine = null;
            other = null;
            if (contact.enabled)
                return false;

            var a = contact.collider;
            var b = contact.otherCollider;
            if (!a || !b || a.isTrigger || b.isTrigger || (!IsOneWay(a) && !IsOneWay(b)))
                return false;

            bool aIsMine = a.attachedRigidbody == _body;
            mine = aIsMine ? a : b;
            other = aIsMine ? b : a;
            return true;
        }

        // Only a PlatformEffector2D keeps a one-way decision in its contacts.
        private static bool IsOneWay(Collider2D collider) =>
            collider.usedByEffector && collider.TryGetComponent<PlatformEffector2D>(out var effector) &&
            effector.enabled && effector.useOneWay;

        // Call after every physics step: a pair that no longer touches hands the next contact back to Unity.
        public void AfterStep()
        {
            _restoredSinceStep = false;
            for (var i = _passing.Count - 1; i >= 0; i--)
            {
                var pair = _passing[i];
                if (pair.Mine && pair.Other && pair.Mine.isActiveAndEnabled && pair.Other.isActiveAndEnabled &&
                    pair.Mine.Distance(pair.Other).isOverlapped)
                    continue;
                Release(pair);
                _passing.RemoveAt(i);
            }
        }

        public void Clear()
        {
            for (var i = 0; i < _passing.Count; i++)
                Release(_passing[i]);
            _passing.Clear();
            for (var i = 0; i < _slots.Length; i++)
            {
                _slots[i].Valid = false;
                _slots[i].Count = 0;
            }
        }

        private bool IsPassing(Collider2D mine, Collider2D other)
        {
            for (var i = 0; i < _passing.Count; i++)
            {
                if (_passing[i].Matches(mine, other))
                    return true;
            }
            return false;
        }

        private static void Release(Pair pair)
        {
            if (pair.Mine && pair.Other)
                Physics2D.IgnoreCollision(pair.Mine, pair.Other, false);
        }

        private static bool Contains(ref Slot slot, Collider2D mine, Collider2D other)
        {
            for (var i = 0; i < slot.Count; i++)
            {
                if (slot.Pairs[i].Matches(mine, other))
                    return true;
            }
            return false;
        }

        private static void Add(ref Slot slot, Collider2D mine, Collider2D other)
        {
            if (Contains(ref slot, mine, other))
                return;
            if (slot.Pairs == null)
                slot.Pairs = new Pair[4];
            else if (slot.Count == slot.Pairs.Length)
                Array.Resize(ref slot.Pairs, slot.Count * 2);
            slot.Pairs[slot.Count++] = new Pair { Mine = mine, Other = other };
        }
    }
}
#endif
