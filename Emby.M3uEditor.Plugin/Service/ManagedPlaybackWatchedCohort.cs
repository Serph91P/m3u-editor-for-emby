using System;
using System.Collections.Generic;
using System.Linq;

namespace Emby.M3uEditor.Plugin.Service
{
    /// <summary>
    /// Tracks watched state only for the explicitly selected Emby profiles.
    /// Each item's required cohort is snapshotted at first observation so a
    /// later configuration change cannot retroactively release cached media.
    /// </summary>
    public sealed class ManagedPlaybackWatchedCohort
    {
        private readonly Dictionary<string, CohortState> _items =
            new Dictionary<string, CohortState>(StringComparer.Ordinal);
        private HashSet<string> _users;

        public ManagedPlaybackWatchedCohort(IEnumerable<string> users)
        {
            _users = Normalize(users);
        }

        public void UpdateUsers(IEnumerable<string> users)
        {
            _users = Normalize(users);
        }

        public bool Record(string itemId, string userId, bool played)
        {
            if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(userId))
            {
                return false;
            }

            CohortState state;
            if (!_items.TryGetValue(itemId, out state))
            {
                state = new CohortState(_users);
                _items.Add(itemId, state);
            }

            if (!state.Required.Contains(userId))
            {
                return state.IsComplete;
            }

            if (played)
            {
                state.Watched.Add(userId);
            }
            else
            {
                state.Watched.Remove(userId);
            }

            return state.IsComplete;
        }

        public bool IsComplete(string itemId)
        {
            CohortState state;
            return !string.IsNullOrWhiteSpace(itemId) && _items.TryGetValue(itemId, out state) && state.IsComplete;
        }

        private static HashSet<string> Normalize(IEnumerable<string> users)
        {
            return new HashSet<string>(
                (users ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.Ordinal);
        }

        private sealed class CohortState
        {
            public CohortState(IEnumerable<string> users)
            {
                Required = new HashSet<string>(users, StringComparer.Ordinal);
                Watched = new HashSet<string>(StringComparer.Ordinal);
            }

            public HashSet<string> Required { get; }
            public HashSet<string> Watched { get; }
            public bool IsComplete => Required.Count > 0 && Required.All(Watched.Contains);
        }
    }
}
