using System.Runtime.InteropServices;

namespace Nami
{
    /// <summary>
    /// Zero-allocation big-endian binary writer for UDP message encoding.
    /// Assumes IEEE 754 float representation (valid on all modern platforms).
    /// </summary>
    internal static class EndianBitConverter
    {
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatUIntUnion
        {
            [FieldOffset(0)] public float FloatValue;
            [FieldOffset(0)] public uint UIntValue;
        }

        public static void WriteInt16BE(byte[] buffer, int offset, short value)
        {
            buffer[offset] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 1] = (byte)(value & 0xFF);
        }

        public static void WriteUInt16BE(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 1] = (byte)(value & 0xFF);
        }

        public static void WriteInt32BE(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }

        public static void WriteUInt32BE(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }

        public static void WriteInt64BE(byte[] buffer, int offset, long value)
        {
            buffer[offset] = (byte)((value >> 56) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 48) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 40) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 32) & 0xFF);
            buffer[offset + 4] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 5] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 6] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 7] = (byte)(value & 0xFF);
        }

        public static void WriteFloatBE(byte[] buffer, int offset, float value)
        {
            var union = new FloatUIntUnion { FloatValue = value };
            WriteUInt32BE(buffer, offset, union.UIntValue);
        }

        public static short ReadInt16BE(byte[] buffer, int offset)
        {
            return (short)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        public static ushort ReadUInt16BE(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        public static uint ReadUInt32BE(byte[] buffer, int offset)
        {
            return (uint)(
                (buffer[offset] << 24) |
                (buffer[offset + 1] << 16) |
                (buffer[offset + 2] << 8) |
                buffer[offset + 3]);
        }
    }
}


