using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using LiteNetLib;

namespace Connections
{
    // The EXACT pinned backend releases _peers before its receive-side Remove,
    // racing UpdateLogic.RemoveAt. Reuse that existing gate without modifying the DLL.
    internal sealed class PinnedUdpPeerSynchronization
    {
        private const string BackendSha256 = "320526331c04be0218341e68412b51df5752aa5381842c7c9d26e414c217eca0";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly Action<byte[], int, int, NetEndPoint> receive;
        private PinnedUdpReceiveBudget budget;
        private Action<Exception> onFailure;
        private bool failed;
        internal object PeerGate { get; }
        private PinnedUdpPeerSynchronization(object peerGate, Action<byte[], int, int, NetEndPoint> receive)
        { PeerGate = peerGate; this.receive = receive; }
        private void Receive(byte[] bytes, int count, int error, NetEndPoint endpoint)
        {
            lock (PeerGate)
            {
                if (failed) return;
                try
                {
                    if (budget != null && error == 0 && !budget.CanReceive(bytes.AsSpan(0, count), endpoint)) return;
                    receive(bytes, count, error, endpoint);
                }
                catch (Exception exception) when (onFailure != null && exception is not OutOfMemoryException)
                {
                    failed = true;
                    onFailure(exception);
                }
            }
        }

        internal static PinnedUdpPeerSynchronization Install(NetManager manager) => InstallCore(manager, null);

        internal static PinnedUdpPeerSynchronization InstallServer(NetManager manager, Action<Exception> onFailure)
            => InstallCore(manager, onFailure ?? throw new ArgumentNullException(nameof(onFailure)));

        private static PinnedUdpPeerSynchronization InstallCore(NetManager manager, Action<Exception> onFailure)
        {
            var type = typeof(NetManager);
            if (manager.IsRunning || manager.UnsyncedEvents) throw new InvalidOperationException("UDP synchronization requires startup with queued callbacks.");
            string location = type.Assembly.Location;
            if (string.IsNullOrEmpty(location) || !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location))).Equals(BackendSha256, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("UDP backend differs from the reviewed synchronization pin.");
            var peers = type.GetField("_peers", Private)?.GetValue(manager) ?? throw new NotSupportedException("UDP peer gate unavailable.");
            var socket = type.GetField("_socket", Private)?.GetValue(manager) ?? throw new NotSupportedException("UDP socket callback unavailable.");
            var field = socket.GetType().GetField("_onMessageReceived", Private) ?? throw new NotSupportedException("UDP callback layout changed.");
            var prior = field.GetValue(socket) as Delegate ?? throw new NotSupportedException("UDP callback missing.");
            if (prior.GetInvocationList().Length != 1 || !ReferenceEquals(prior.Target, manager)) throw new NotSupportedException("UDP callback ownership changed.");
            var original = (Action<byte[], int, int, NetEndPoint>)prior.Method.CreateDelegate(typeof(Action<byte[], int, int, NetEndPoint>), prior.Target);
            var guard = new PinnedUdpPeerSynchronization(peers, original);
            if (onFailure != null)
            {
                guard.budget = new PinnedUdpReceiveBudget(manager);
                guard.onFailure = onFailure;
            }
            var callback = Delegate.CreateDelegate(field.FieldType, guard, typeof(PinnedUdpPeerSynchronization).GetMethod(nameof(Receive), Private));
            // Instance initonly field, replaced before socket/logic threads are started.
            field.SetValue(socket, callback);
            if (!ReferenceEquals(field.GetValue(socket), callback)) throw new NotSupportedException("UDP callback could not be synchronized.");
            return guard;
        }
    }
}
