using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Utils;
using Utils.Framework;

namespace ServerRuntime
{
    public enum SessionBindFailure { None, Unauthorized, UnsupportedVersion, InvalidCapabilities, Conflict }

    // Trusted server context, not a wire DTO/log record. SessionId is never formatted in diagnostics.
    public sealed class ServerSessionReadyEventArgs : EventArgs
    {
        public string ConnectionId { get; }
        public string PrincipalId { get; }
        public string SessionId { get; }
        public string Epoch { get; }
        public IReadOnlyList<string> Capabilities { get; }
        internal ServerSessionReadyEventArgs(string connection, string principal, string session, IEnumerable<string> capabilities, string epoch = null)
        {
            ConnectionId = connection; PrincipalId = principal; SessionId = session;
            Epoch = epoch ?? Guid.NewGuid().ToString("N");
            Capabilities = new ReadOnlyCollection<string>(capabilities.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    public interface IServerSessionReadiness
    {
        event EventHandler<ServerSessionReadyEventArgs> SessionReady;
        bool TryGetReady(string connectionId, out ServerSessionReadyEventArgs session);
    }

    public sealed class ServerSessionRegistry : IServerSessionReadiness
    {
        private enum Phase { Negotiating, Ready, Rejected }
        private sealed class Entry
        {
            public ServerSessionReadyEventArgs Session;
            public HashSet<string> Offered;
            public Phase Phase;
            public bool EverReady;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly Func<string, string, string, bool> live;
        private readonly Action<string, LogLevel> log;
        public event EventHandler<ServerSessionReadyEventArgs> SessionReady;
        public ServerSessionRegistry(Func<string, string, string, bool> isLive, Action<string, LogLevel> log = null)
        { live = isLive ?? throw new ArgumentNullException(nameof(isLive)); this.log = log; }
        private bool IsLive(ServerSessionReadyEventArgs session) => IsLive(session.ConnectionId, session.PrincipalId, session.SessionId);
        private bool IsLive(string connection, string principal, string session)
        { try { return live(connection, principal, session); } catch { return false; } }

        public bool TryBind(string connection, string principal, string session, int version,
            IEnumerable<string> offeredCapabilities, IEnumerable<string> serverCapabilities,
            out ServerSessionReadyEventArgs bound, out SessionBindFailure failure)
        {
            bound = null; failure = SessionBindFailure.Unauthorized;
            if (new[] { connection, principal, session }.Any(value => string.IsNullOrEmpty(value) || value.Length > 256) || !IsLive(connection, principal, session)) return false;
            if (version != FrameworkProtocol.Version) { failure = SessionBindFailure.UnsupportedVersion; return false; }
            string[] offered = (offeredCapabilities ?? Array.Empty<string>()).Take(513).ToArray();
            if (offered.Length > 512 || offered.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 256))
            { failure = SessionBindFailure.InvalidCapabilities; return false; }
            var offeredSet = new HashSet<string>(offered, StringComparer.OrdinalIgnoreCase);
            lock (gate)
            {
                if (entries.TryGetValue(connection, out Entry entry))
                {
                    if (entry.Phase == Phase.Rejected || entry.Session.PrincipalId != principal || entry.Session.SessionId != session || !entry.Offered.SetEquals(offeredSet))
                    { failure = SessionBindFailure.Conflict; return false; }
                    bound = entry.Session;
                }
                else
                {
                    var negotiated = offeredSet.Intersect(serverCapabilities ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                    bound = new ServerSessionReadyEventArgs(connection, principal, session, negotiated);
                    entries.Add(connection, new Entry { Session = bound, Offered = offeredSet });
                }
            }
            failure = SessionBindFailure.None;
            return true;
        }

        // Ready is a server decision after local capability-response submission, not client ACK.
        public bool MarkReady(ServerSessionReadyEventArgs session)
        {
            if (session == null) return false;
            if (!IsLive(session))
            {
                lock (gate)
                    if (entries.TryGetValue(session.ConnectionId, out Entry stale) && ReferenceEquals(stale.Session, session)) stale.Phase = Phase.Rejected;
                return false;
            }
            lock (gate)
            {
                if (!entries.TryGetValue(session.ConnectionId, out Entry entry) || !ReferenceEquals(entry.Session, session) || entry.Phase == Phase.Rejected) return false;
                if (entry.Phase == Phase.Ready) return true;
                entry.Phase = Phase.Ready; entry.EverReady = true;
            }
            foreach (EventHandler<ServerSessionReadyEventArgs> subscriber in SessionReady?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                if (!TryGetReady(session.ConnectionId, out var current) || !ReferenceEquals(current, session)) break;
                try { subscriber(this, session); }
                catch (Exception error) { try { log?.Invoke("SessionReady subscriber failed: " + error.GetType().Name, LogLevel.ERROR); } catch { } }
            }
            return true;
        }

        public bool TryGetReady(string connectionId, out ServerSessionReadyEventArgs session)
        {
            session = null;
            Entry entry;
            lock (gate)
                if (connectionId == null || !entries.TryGetValue(connectionId, out entry) || entry.Phase != Phase.Ready) return false;
            bool valid = IsLive(entry.Session);
            lock (gate)
            {
                if (!entries.TryGetValue(connectionId, out Entry current) || !ReferenceEquals(current, entry)) return false;
                if (!valid) { entry.Phase = Phase.Rejected; return false; }
                if (entry.Phase == Phase.Rejected) return false;
                session = entry.Session;
                return true;
            }
        }
        public bool MatchesReady(string connection, string principal, string session) =>
            TryGetReady(connection, out var ready) && ready.PrincipalId == principal && ready.SessionId == session;
        public bool ReplaceSession(string connection, string oldSession, string newSession)
        {
            lock (gate)
            {
                if (!entries.TryGetValue(connection, out var entry)) return true;
                if (entry.Session.SessionId != oldSession || !IsLive(connection, entry.Session.PrincipalId, newSession)) return false;
                entries[connection] = new Entry {
                    Session = new ServerSessionReadyEventArgs(connection, entry.Session.PrincipalId, newSession, entry.Session.Capabilities, entry.Session.Epoch),
                    Offered = new HashSet<string>(entry.Offered, StringComparer.OrdinalIgnoreCase),
                    Phase = entry.EverReady ? Phase.Ready : entry.Phase, EverReady = entry.EverReady
                };
                return true;
            }
        }
        public void Remove(string connection) { lock (gate) entries.Remove(connection); }
    }
}
