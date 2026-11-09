using System;
using System.Collections.Generic;
using System.Linq;
using Connections;
using Utils.Framework;

namespace ServerRuntime
{
    public sealed class RelayLimits
    {
        public int MaxRecipients { get; set; } = 128;
        public int CommandsPerSecond { get; set; } = 10;
        public int BurstCommands { get; set; } = 20;
        public int FanoutBytesPerSecond { get; set; } = 1024 * 1024;
        public int BurstFanoutBytes { get; set; } = 2 * 1024 * 1024;
        public int GlobalFanoutBytesPerSecond { get; set; } = 8 * 1024 * 1024;
        public int GlobalBurstFanoutBytes { get; set; } = 16 * 1024 * 1024;
    }

    public interface ICommandRelayAuthorization
    {
        bool CanRelay(string principalId, CommandRoute route);
    }

    public sealed class CommandCapabilityRouter
    {
        private sealed class Entry
        {
            public string Epoch;
            public long Revision;
            public HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
            public Budget Budget;
            public DateTime NextSnapshot;
        }
        private sealed class Budget
        {
            private double tokens;
            private DateTime updated;
            public Budget(double capacity, DateTime now) { tokens = capacity; updated = now; }
            public bool Take(double amount, double rate, double capacity, DateTime now)
            {
                tokens = Math.Min(capacity, tokens + Math.Max(0, (now - updated).TotalSeconds) * rate);
                updated = now;
                if (amount > tokens) return false;
                tokens -= amount;
                return true;
            }
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly Dictionary<string, HashSet<string>> index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Budget> controlBudgets = new Dictionary<string, Budget>();
        private readonly Dictionary<string, Budget> commandBudgets = new Dictionary<string, Budget>();
        private readonly ServerSessionRegistry sessions;
        private readonly Func<string[]> connections;
        private readonly Func<string, bool> onlinePlayer;
        private readonly Func<DateTime> now;
        private readonly RelayLimits limits;
        private readonly Budget globalBudget;
        public ICommandRelayAuthorization Authorization { get; set; }

        public CommandCapabilityRouter(ServerSessionRegistry sessions, Func<string[]> connections, Func<string, bool> onlinePlayer,
            RelayLimits limits = null, Func<DateTime> now = null)
        {
            this.sessions = sessions;
            this.connections = connections;
            this.onlinePlayer = onlinePlayer;
            this.limits = limits ?? new RelayLimits();
            if (this.limits.MaxRecipients < 1 || this.limits.MaxRecipients > 128 || this.limits.CommandsPerSecond < 1 ||
                this.limits.BurstCommands < 1 || this.limits.FanoutBytesPerSecond < 1 || this.limits.BurstFanoutBytes < 1 ||
                this.limits.GlobalFanoutBytesPerSecond < 1 || this.limits.GlobalBurstFanoutBytes < 1)
                throw new ArgumentException("Invalid Relay resource limits.");
            this.now = now ?? (() => DateTime.UtcNow);
            globalBudget = new Budget(this.limits.GlobalBurstFanoutBytes, this.now());
        }
        public bool Supports(string connection) => sessions.TryGetReady(connection, out var session) &&
            session.Capabilities.Contains(CommandRelayProtocol.Capability, StringComparer.OrdinalIgnoreCase);

        public CommandCapabilityAck Apply(string connection, CommandCapabilitySnapshot snapshot)
        {
            var ack = new CommandCapabilityAck { Epoch = snapshot?.Epoch, Revision = snapshot?.Revision ?? 0 };
            if (!sessions.TryGetReady(connection, out var session) || !Supports(connection))
            { ack.Error = "UnsupportedRelay"; return ack; }
            ack.Epoch = session.Epoch;
            if (snapshot == null || snapshot.Epoch != session.Epoch || snapshot.Revision <= 0 || snapshot.Capabilities == null ||
                snapshot.Capabilities.Count > CommandRelayProtocol.MaxCapabilities || snapshot.Capabilities.Any(c => !CommandRelayProtocol.IsCapability(c)))
            { ack.Error = "InvalidCapability"; return ack; }
            var keys = new HashSet<string>(snapshot.Capabilities.Select(c => c.Key), StringComparer.Ordinal);
            if (keys.Count != snapshot.Capabilities.Count) { ack.Error = "InvalidCapability"; return ack; }
            lock (gate)
            {
                if (!sessions.TryGetReady(connection, out var current) || current.Epoch != session.Epoch)
                { ack.Error = "NotAuthenticated"; return ack; }
                if (!entries.TryGetValue(connection, out var entry) || entry.Epoch != session.Epoch)
                {
                    Remove(connection);
                    entry = new Entry { Epoch = session.Epoch, Budget = new Budget(limits.BurstFanoutBytes, now()) };
                    entries.Add(connection, entry);
                }
                if (snapshot.Revision < entry.Revision) { ack.Revision = entry.Revision; return ack; }
                if (snapshot.Revision == entry.Revision)
                { if (!entry.Keys.SetEquals(keys)) ack.Error = "InvalidCapability"; return ack; }
                if (now() < entry.NextSnapshot) { ack.Error = "RateLimited"; return ack; }
                RemoveIndex(connection, entry.Keys);
                entry.Keys = keys;
                entry.Revision = snapshot.Revision;
                entry.NextSnapshot = now().AddMilliseconds(250);
                foreach (string key in keys)
                {
                    if (!index.TryGetValue(key, out var recipients)) index[key] = recipients = new HashSet<string>();
                    recipients.Add(connection);
                }
                return ack;
            }
        }
        public void Remove(string connection)
        {
            lock (gate)
            {
                if (entries.TryGetValue(connection, out var entry)) RemoveIndex(connection, entry.Keys);
                entries.Remove(connection);
                commandBudgets.Remove(connection);
                controlBudgets.Remove(connection);
            }
        }
        private void RemoveIndex(string connection, IEnumerable<string> keys)
        {
            foreach (string key in keys)
                if (index.TryGetValue(key, out var recipients))
                { recipients.Remove(connection); if (recipients.Count == 0) index.Remove(key); }
        }
        public bool Eligible(string connection, FrameworkPacket command)
        {
            if (!Supports(connection) || !sessions.TryGetReady(connection, out var session)) return false;
            var route = command.CommandRoute;
            if (route == null || (route.TargetPlayerUuid != null && route.TargetPlayerUuid != session.PrincipalId)) return false;
            string key = new CommandCapability { PluginId = route.PluginId, CommandType = route.CommandType, Version = route.Version }.Key;
            lock (gate) return entries.TryGetValue(connection, out var entry) && entry.Epoch == session.Epoch && entry.Keys.Contains(key);
        }
        public string Validate(string connection, string principal, string epoch, FrameworkPacket command)
        {
            if (!sessions.TryGetReady(connection, out var session) || session.PrincipalId != principal || session.Epoch != epoch)
                return "NotAuthenticated";
            if (!Supports(connection)) return "UnsupportedRelay";
            string error = CommandRelayProtocol.Validate(command);
            if (error != null) return error;
            try { if (Authorization != null && !Authorization.CanRelay(principal, command.CommandRoute)) return "Forbidden"; }
            catch { return "Forbidden"; }
            return null;
        }
        public bool AdmitControl(string connection)
        {
            lock (gate)
            {
                if (!controlBudgets.TryGetValue(connection, out var budget))
                    controlBudgets[connection] = budget = new Budget(4, now());
                return budget.Take(1, 4, 4, now());
            }
        }
        public string Admit(string connection)
        {
            lock (gate)
            {
                if (!commandBudgets.TryGetValue(connection, out var budget))
                    commandBudgets[connection] = budget = new Budget(limits.BurstCommands, now());
                return budget.Take(1, limits.CommandsPerSecond, limits.BurstCommands, now()) ? null : "RateLimited";
            }
        }
        public RelayResult Route(string connection, string principal, string epoch, FrameworkPacket command,
            Func<FrameworkPacket, string[], PacketDispatchResult> deliver)
        {
            string requestId = command.MessageId;
            string error = Validate(connection, principal, epoch, command);
            if (error != null) return RelayResult.Failure(requestId, error);
            var route = command.CommandRoute;
            string[] candidates;
            string key = new CommandCapability { PluginId = route.PluginId, CommandType = route.CommandType, Version = route.Version }.Key;
            lock (gate) candidates = index.TryGetValue(key, out var matching) ? matching.ToArray() : Array.Empty<string>();
            string[] recipients = candidates.Where(id => (route.TargetPlayerUuid != null || id != connection) && Eligible(id, command))
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (recipients.Length == 0)
            {
                if (route.TargetPlayerUuid == null) error = "NoRecipients";
                else if (!onlinePlayer(route.TargetPlayerUuid)) error = "TargetOffline";
                else error = connections().Any(id => sessions.TryGetReady(id, out var s) &&
                    s.PrincipalId == route.TargetPlayerUuid && Supports(id)) ? "NoCapability" : "UnsupportedRelay";
                return RelayResult.Failure(requestId, error);
            }
            if (recipients.Length > limits.MaxRecipients) return RelayResult.Failure(requestId, "RateLimited");
            long bytes = (long)FrameworkSerialization.SerializePacket(command).Length * recipients.Length;
            lock (gate)
            {
                if (!entries.TryGetValue(connection, out var entry))
                    return RelayResult.Failure(requestId, "CapabilitiesNotSynced");
                if (!entry.Budget.Take(bytes, limits.FanoutBytesPerSecond, limits.BurstFanoutBytes, now()) ||
                    !globalBudget.Take(bytes, limits.GlobalFanoutBytesPerSecond, limits.GlobalBurstFanoutBytes, now()))
                    return RelayResult.Failure(requestId, "RateLimited");
            }
            PacketDispatchResult dispatched = deliver(command, recipients);
            var result = new RelayResult { RequestMessageId = requestId, Status = "Submitted" };
            foreach (string id in recipients)
                result.Deliveries.Add(new RelayDelivery { ConnectionId = id, Outcome = dispatched.For(id).ToString() });
            if (result.Deliveries.Any(d => d.Outcome != TransportSendOutcome.Submitted.ToString()))
            {
                result.Error = "DeliveryFailed";
                result.Status = result.Deliveries.Any(d => d.Outcome != TransportSendOutcome.NotSent.ToString()) ? "Partial" : "Rejected";
            }
            return result;
        }
    }
}
