using System;
using System.Globalization;

namespace Framework.Network.Packets
{
    [System.Reflection.Obfuscation(Feature = "Apply to member * when method or constructor: virtualization", Exclude = false)]
    public class BitUnpack
    {
        public PacketReader reader;
        int Position;
        byte Value;

        public BitUnpack(PacketReader reader)
        {
            this.reader = reader;
            Position = 8;
            Value = 0;
        }

        public bool GetBit()
        {
            if (Position == 8)
            {
                Value = reader.Read<byte>();
                Position = 0;
            }

            int returnValue = Value;
            Value = (byte)(2 * returnValue);
            ++Position;

            return Convert.ToBoolean(returnValue >> 7);
        }

        public T GetBits<T>(byte bitCount)
        {
            int returnValue = 0;

            checked
            {
                for (var i = bitCount - 1; i >= 0; --i)
                    returnValue = GetBit() ? (1 << i) | returnValue : returnValue;
            }

            return (T)Convert.ChangeType(returnValue, typeof(T), CultureInfo.InvariantCulture);
        }

        public T GetNameLength<T>(byte bitCount)
        {
            int returnValue = 0;

            // Unknown, always before namelength bits...
            GetBit();

            checked
            {
                for (var i = bitCount - 1; i >= 0; --i)
                    returnValue = GetBit() ? (1 << i) | returnValue : returnValue;
            }

            return (T)Convert.ChangeType(returnValue, typeof(T), CultureInfo.InvariantCulture);
        }

        public ulong GetPackedValue(byte[] mask, byte[] bytes)
        {
            bool[] valueMask = new bool[mask.Length];
            byte[] valueBytes = new byte[bytes.Length];

            for (int i = 0; i < valueMask.Length; i++)
                valueMask[i] = GetBit();
     
            for (byte i = 0; i < bytes.Length; i++)
                if (valueMask[mask[i]])
                    valueBytes[bytes[i]] = (byte)(reader.Read<byte>() ^ 1);

            return BitConverter.ToUInt64(valueBytes, 0);
        }
    }
}
