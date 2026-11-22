using System;
using System.IO;
using System.IO.Compression;

namespace Gtamp.Watcher
{
    /// <summary>
    /// Writes a PNG from raw BGRA pixels.
    /// <para>
    /// Hand-written because the alternative, <c>System.Drawing.Common</c>, is
    /// Windows-only from .NET 6 onwards, and the whole solution is built and
    /// tested on Linux in CI. A dependency that stops the build compiling on the
    /// machine that checks it is a bad trade for a format this small: signature,
    /// three chunks, one CRC and a zlib wrapper around Deflate.
    /// </para>
    /// </summary>
    public static class Png
    {
        public static byte[] Encode(byte[] bgra, int width, int height)
        {
            if (bgra == null)
            {
                throw new ArgumentNullException(nameof(bgra));
            }

            if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            {
                throw new ArgumentException("pixel buffer is smaller than the stated image", nameof(bgra));
            }

            using var file = new MemoryStream();
            file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)width);
            WriteBigEndian(header, 4, (uint)height);
            header[8] = 8;   // bits per channel
            header[9] = 2;   // colour type 2: truecolour, no alpha
            header[10] = 0;  // deflate
            header[11] = 0;  // adaptive filtering
            header[12] = 0;  // no interlace
            WriteChunk(file, "IHDR", header);

            // One filter byte per scanline, then RGB. Filter 0 (None) keeps this
            // readable; the deflate pass does the compressing.
            var raw = new byte[height * ((width * 3) + 1)];
            int at = 0;
            for (int y = 0; y < height; y++)
            {
                raw[at++] = 0;
                int row = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int p = row + (x * 4);
                    raw[at++] = bgra[p + 2];
                    raw[at++] = bgra[p + 1];
                    raw[at++] = bgra[p];
                }
            }

            WriteChunk(file, "IDAT", Zlib(raw));
            WriteChunk(file, "IEND", Array.Empty<byte>());
            return file.ToArray();
        }

        /// <summary>Deflate wrapped in the zlib header and Adler-32 checksum PNG requires.</summary>
        private static byte[] Zlib(byte[] data)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x9C);

            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(data, 0, data.Length);
            }

            uint adler = Adler32(data);
            output.WriteByte((byte)(adler >> 24));
            output.WriteByte((byte)(adler >> 16));
            output.WriteByte((byte)(adler >> 8));
            output.WriteByte((byte)adler);
            return output.ToArray();
        }

        private static void WriteChunk(Stream into, string type, byte[] data)
        {
            var length = new byte[4];
            WriteBigEndian(length, 0, (uint)data.Length);
            into.Write(length, 0, 4);

            var body = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++)
            {
                body[i] = (byte)type[i];
            }

            Buffer.BlockCopy(data, 0, body, 4, data.Length);
            into.Write(body, 0, body.Length);

            var crc = new byte[4];
            WriteBigEndian(crc, 0, Crc32(body));
            into.Write(crc, 0, 4);
        }

        private static void WriteBigEndian(byte[] into, int offset, uint value)
        {
            into[offset] = (byte)(value >> 24);
            into[offset + 1] = (byte)(value >> 16);
            into[offset + 2] = (byte)(value >> 8);
            into[offset + 3] = (byte)value;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        private static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            }

            return c ^ 0xFFFFFFFFu;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte value in data)
            {
                a = (a + value) % 65521u;
                b = (b + a) % 65521u;
            }

            return (b << 16) | a;
        }
    }
}
