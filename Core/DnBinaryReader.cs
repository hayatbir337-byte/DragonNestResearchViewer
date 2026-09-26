using System.Numerics;
using System.Text;

namespace DragonNestResearchViewer.Core;

public sealed class DnBinaryReader : IDisposable
{
    readonly BinaryReader _br;
    readonly Encoding _encoding = Encoding.GetEncoding(949);

    public DnBinaryReader(Stream stream) => _br = new BinaryReader(stream);

    public long Position { get => _br.BaseStream.Position; set => _br.BaseStream.Position = value; }
    public long Length => _br.BaseStream.Length;
    public long Remaining => Length - Position;

    public byte ReadByte() => _br.ReadByte();
    public byte[] ReadBytes(int count) => _br.ReadBytes(count);
    public short ReadInt16() => _br.ReadInt16();
    public ushort ReadUInt16() => _br.ReadUInt16();
    public int ReadInt32() => _br.ReadInt32();
    public float ReadSingle() => _br.ReadSingle();
    public Vector2 ReadVector2() => new(ReadSingle(), ReadSingle());
    public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());
    public Vector4 ReadVector4() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());

    public Matrix4x4 ReadMatrix4x4() => new(
        ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(),
        ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(),
        ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(),
        ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());

    public string ReadFixedString(int bytes)
    {
        var data = ReadBytes(bytes);
        int len = Array.IndexOf(data, (byte)0);
        if (len < 0) len = data.Length;
        return _encoding.GetString(data, 0, len);
    }

    public string ReadCString()
    {
        using var ms = new MemoryStream();
        while (Remaining > 0)
        {
            byte b = ReadByte();
            if (b == 0) break;
            ms.WriteByte(b);
        }
        return _encoding.GetString(ms.ToArray());
    }

    public string ReadLengthString()
    {
        int len = ReadInt32();
        if (len < 0 || len > Remaining || len > 16_777_216)
            throw new InvalidDataException($"Invalid string length {len} at 0x{Position - 4:X}");
        return ReadFixedString(len);
    }

    public void Skip(long bytes) => Position += bytes;
    public void Dispose() => _br.Dispose();
}