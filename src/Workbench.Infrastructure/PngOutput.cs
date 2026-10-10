using System.Buffers.Binary;

namespace Workbench.Infrastructure;

public static class PngOutput
{
    public static void Validate(byte[] bytes, int width, int height)
    {
        ReadOnlySpan<byte> magic = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 45 || !bytes.AsSpan(0, 8).SequenceEqual(magic) || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) != width || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) != height)
            throw new InvalidDataException("The runtime returned an invalid PNG or unexpected image dimensions.");
        var offset = 8; var imageData = false; var end = false;
        while (offset + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > int.MaxValue || (long)offset + length + 12 > bytes.Length) throw new InvalidDataException("The PNG is truncated.");
            var chunk = bytes.AsSpan(offset + 4, (int)length + 4); uint crc = uint.MaxValue;
            foreach (var b in chunk) { crc ^= b; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + (int)length, 4))) throw new InvalidDataException("The PNG checksum is invalid.");
            imageData |= chunk[..4].SequenceEqual("IDAT"u8); end = chunk[..4].SequenceEqual("IEND"u8);
            offset += (int)length + 12; if (end) break;
        }
        if (!imageData || !end || offset != bytes.Length) throw new InvalidDataException("The PNG has no image data or valid end chunk.");
    }
}
