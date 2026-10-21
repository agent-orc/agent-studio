using System.Buffers.Binary;
using System.Text;

namespace AgentStudio.Telemetry;

public sealed class OtlpFormatException(string message) : Exception(message);

internal enum ProtoWireType { Varint = 0, Fixed64 = 1, LengthDelimited = 2, StartGroup = 3, EndGroup = 4, Fixed32 = 5 }

/// <summary>
/// Minimal protobuf wire-format reader for the fixed OTLP schema. It reads tags,
/// varints, fixed-width values, and length-delimited fields, and skips unknown
/// fields. Every read is bounds-checked; malformed input throws
/// <see cref="OtlpFormatException"/>.
/// </summary>
internal ref struct ProtoWireReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public ProtoWireReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public bool TryReadTag(out int field, out ProtoWireType wireType)
    {
        if (_position >= _buffer.Length)
        {
            field = 0;
            wireType = default;
            return false;
        }
        var tag = ReadVarint();
        var number = tag >> 3;
        if (number is 0 or > 0x1FFFFFFF) throw new OtlpFormatException("Protobuf field number is out of range.");
        field = (int)number;
        wireType = (ProtoWireType)(tag & 7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (_position >= _buffer.Length) throw Truncated();
            var current = _buffer[_position++];
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return result;
        }
        throw new OtlpFormatException("Protobuf varint is longer than ten bytes.");
    }

    public ulong ReadFixed64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public uint ReadFixed32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadFixed64());

    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = ReadVarint();
        if (length > (ulong)(_buffer.Length - _position)) throw Truncated();
        return Take((int)length);
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

    public void Skip(ProtoWireType wireType)
    {
        switch (wireType)
        {
            case ProtoWireType.Varint: ReadVarint(); break;
            case ProtoWireType.Fixed64: Take(8); break;
            case ProtoWireType.LengthDelimited: ReadBytes(); break;
            case ProtoWireType.Fixed32: Take(4); break;
            default: throw new OtlpFormatException($"Protobuf wire type {(int)wireType} is not supported.");
        }
    }

    private ReadOnlySpan<byte> Take(int length)
    {
        if (length > _buffer.Length - _position) throw Truncated();
        var slice = _buffer.Slice(_position, length);
        _position += length;
        return slice;
    }

    private static OtlpFormatException Truncated() => new("Protobuf message is truncated.");
}
