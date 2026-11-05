using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Connections;

// Layout and offsets apply only after PinnedUdpPeerSynchronization verifies the DLL hash.
internal sealed class PinnedUdpReceiveBudget
{
    internal const int MaxFragments = 4096;
    private const int MaxQueuedEvents = 512;
    private const long QueueBytes = 32L * 1024 * 1024;
    private const long PeerFragmentBytes = 48L * 1024 * 1024;
    private const long GlobalFragmentBytes = 96L * 1024 * 1024;
    private const int Merged = 0x13;
    private const int FragmentBit = 0x80;

    private readonly NetManager manager;
    private readonly ICollection queue;
    private readonly FieldInfo reader, fragments, slots, ordered, buffered, rawData, packetSize;
    private readonly int mtu;
    private readonly List<NetPeer> peers = new();
    private readonly Dictionary<ushort, int> incomingGroups = new();
    private readonly Dictionary<ushort, int> peerGroups = new();

    internal PinnedUdpReceiveBudget(NetManager manager)
    {
        this.manager = manager;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        queue = (ICollection)RequireField(typeof(NetManager), "_netEventsQueue", fields).GetValue(manager);
        reader = RequireField(queue.GetType().GetGenericArguments()[0], "DataReader", fields);
        fragments = RequireField(typeof(NetPeer), "_holdedFragments", fields);
        slots = RequireField(fragments.FieldType.GetGenericArguments()[1], "Fragments", fields);
        ordered = RequireField(typeof(NetPeer), "_reliableOrderedChannel", fields);
        buffered = RequireField(ordered.FieldType, "_receivedPackets", fields);
        var packetType = buffered.FieldType.GetElementType();
        rawData = RequireField(packetType, "RawData", fields);
        packetSize = RequireField(packetType, "Size", fields);
        var constants = typeof(NetManager).Assembly.GetType("LiteNetLib.NetConstants", throwOnError: true);
        mtu = (int)RequireField(constants, "MaxPacketSize", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        if (mtu < 512 || mtu > 65535) throw new NotSupportedException("Unexpected UDP datagram size.");
    }

    private static FieldInfo RequireField(Type type, string name, BindingFlags flags) =>
        type.GetField(name, flags) ?? throw new NotSupportedException("UDP receive budget layout changed.");

    // The backend queues reliable packets and acknowledges them. Drop before it sees the
    // datagram so a dropped application payload is never acknowledged by the transport.
    internal bool CanReceive(ReadOnlySpan<byte> datagram, NetEndPoint endpoint)
    {
        incomingGroups.Clear();
        if (datagram.Length > mtu || !TryInspect(datagram, incomingGroups)) return false;
        lock (queue)
        {
            if (queue.Count >= MaxQueuedEvents) return false;
            long bytes = 0;
            foreach (object evt in queue)
            {
                if (reader.GetValue(evt) is NetDataReader payload) bytes += payload.AvailableBytes;
                if (bytes >= QueueBytes) return false;
            }
        }

        if (incomingGroups.Count == 0) return true;
        long globalBytes = 0;
        bool knownPeer = false;
        manager.GetPeersNonAlloc(peers);
        try
        {
            foreach (NetPeer peer in peers)
            {
                peerGroups.Clear();
                foreach (DictionaryEntry group in (IDictionary)fragments.GetValue(peer))
                    Reserve(peerGroups, (ushort)group.Key, ((Array)slots.GetValue(group.Value)).Length);
                // Ordered packets can become fragments when a gap closes. Count their
                // reservations while still buffered, once per fragment ID.
                foreach (object packet in (Array)buffered.GetValue(ordered.GetValue(peer)))
                {
                    if (packet == null) continue;
                    var data = (byte[])rawData.GetValue(packet);
                    int length = (int)packetSize.GetValue(packet);
                    if (!InspectLeaf(data.AsSpan(0, length), peerGroups)) return false;
                }

                if (Equals(peer.EndPoint, endpoint))
                {
                    knownPeer = true;
                    foreach (var group in incomingGroups) Reserve(peerGroups, group.Key, group.Value);
                }

                long peerBytes = 0;
                foreach (int count in peerGroups.Values) peerBytes += count * (mtu + 32L);
                globalBytes += peerBytes;
                if (peerBytes > PeerFragmentBytes || globalBytes > GlobalFragmentBytes) return false;
            }
        }
        finally { peers.Clear(); }
        return knownPeer;
    }

    internal static bool TryInspect(ReadOnlySpan<byte> datagram, Dictionary<ushort, int> reservations)
    {
        if (datagram.IsEmpty) return false;
        if ((datagram[0] & 0x7f) != Merged) return InspectLeaf(datagram, reservations);
        if (datagram[0] != Merged || datagram.Length < 4) return false;
        var remaining = datagram[1..];
        while (!remaining.IsEmpty)
        {
            if (remaining.Length < 2) return false;
            int length = BinaryPrimitives.ReadUInt16LittleEndian(remaining);
            remaining = remaining[2..];
            if (length == 0 || length > remaining.Length || !InspectLeaf(remaining[..length], reservations))
                return false;
            remaining = remaining[length..];
        }
        return true;
    }

    private static bool InspectLeaf(ReadOnlySpan<byte> packet, Dictionary<ushort, int> reservations)
    {
        int kind = packet[0] & 0x7f;
        if (kind >= Merged) return false; // The pinned sender only emits one level of merging.
        if ((packet[0] & FragmentBit) == 0) return true;
        if (kind is not (1 or 3) || packet.Length <= 9) return false;
        int part = BinaryPrimitives.ReadUInt16LittleEndian(packet[5..]);
        int total = BinaryPrimitives.ReadUInt16LittleEndian(packet[7..]);
        if (total < 2 || total > MaxFragments || part >= total) return false;
        Reserve(reservations, BinaryPrimitives.ReadUInt16LittleEndian(packet[3..]), total);
        return true;
    }

    private static void Reserve(Dictionary<ushort, int> groups, ushort id, int count)
    {
        if (!groups.TryGetValue(id, out int reserved) || count > reserved) groups[id] = count;
    }

}
