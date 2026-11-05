using System.Buffers.Binary;
using System.Text;

namespace ServerTransport;

// phinix-stream/1 body: big-endian ushort UTF8 module byte count, module, opaque serialized payload.
// Only the transport envelope changes; packet serializers and application authorization remain shared.
public static class TlsModulePacket
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static byte[] Encode(string module, byte[] payload, int maximum)
    {
        ArgumentNullException.ThrowIfNull(module); ArgumentNullException.ThrowIfNull(payload);
        if (maximum < 3 || maximum > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximum));
        int nameSize = Utf8.GetByteCount(module);
        if (nameSize < 1 || nameSize > 128 || payload.Length > maximum - 2 - nameSize)
            throw new ArgumentOutOfRangeException(nameof(payload), "Module packet exceeds transport bounds.");
        byte[] result = new byte[2 + nameSize + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)nameSize);
        Utf8.GetBytes(module, result.AsSpan(2, nameSize)); payload.CopyTo(result, 2 + nameSize);
        return result;
    }
    public static (string Module, byte[] Payload) Decode(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length < 3) throw new InvalidDataException("Truncated module header.");
        int size = BinaryPrimitives.ReadUInt16BigEndian(body);
        if (size < 1 || size > 128 || size > body.Length - 2) throw new InvalidDataException("Invalid module header.");
        string module = Utf8.GetString(body, 2, size);
        return (module, body.AsSpan(2 + size).ToArray());
    }
}
