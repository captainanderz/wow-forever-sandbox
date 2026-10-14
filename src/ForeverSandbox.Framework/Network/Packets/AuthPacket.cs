using System;
using System.IO;
using System.Text;
using Framework.Constants.Net;
using Framework.Misc;

namespace Framework.Network.Packets
{
    [System.Reflection.Obfuscation(Feature = "Apply to member * when method or constructor: virtualization", Exclude = false)]
    public class AuthPacket
    {
        public AuthPacketHeader Header { get; set; }
        public byte[] Data { get; set; }
        public int ProcessedBytes { get; set; }

        object stream;
        byte bytePart;
        byte preByte;
        int count;

        public AuthPacket()
        {
            stream = new BinaryWriter(new MemoryStream());
        }

        public AuthPacket(byte[] data, int size)
        {
            stream = new BinaryReader(new MemoryStream(data));

            Header = new AuthPacketHeader();
            Header.Message = Read<byte>(6);

            if (Read<bool>(1))
                Header.Channel = (AuthChannel)Read<byte>(4);

            Data = new byte[size];

            Buffer.BlockCopy(data, 0, Data, 0, size);
        }

        public AuthPacket(AuthServerMessage message, AuthChannel channel = AuthChannel.None)
        {
            stream = new BinaryWriter(new MemoryStream());

            Header = new AuthPacketHeader();
            Header.Message = (byte)message;
            Header.Channel = channel;

            var hasChannel = channel != AuthChannel.None;

            Write(Header.Message, 6);
            Write(hasChannel, 1);

            if (hasChannel)
                Write((byte)Header.Channel, 4);
        }

        public void Finish()
        {
            var writer = stream as BinaryWriter;

            Data = new byte[writer.BaseStream.Length];

            writer.BaseStream.Seek(0, SeekOrigin.Begin);

            for (int i = 0; i < Data.Length; i++)
                Data[i] = (byte)writer.BaseStream.ReadByte();

            writer.Dispose();
        }

        #region Reader
        public T Read<T>()
        {
            var reader = stream as BinaryReader;

            if (reader == null)
                throw new InvalidOperationException("");

            return reader.Read<T>();
        }

        public byte[] Read(int count)
        {
            var reader = stream as BinaryReader;

            if (reader == null)
                throw new InvalidOperationException("");

            ProcessedBytes += count;

            return reader.ReadBytes(count);
        }

        public string ReadString(int count)
        {
            return Encoding.UTF8.GetString(Read(count));
        }

        public T Read<T>(int bits)
        {
            ulong value = 0;
            var bitsToRead = 0;

            while (bits != 0)
            {
                if ((count % 8) == 0)
                {
                    bytePart = Read<byte>();

                    ProcessedBytes += 1;
                }

                var shiftedBits = count & 7;
                bitsToRead = 8 - shiftedBits;

                if (bitsToRead >= bits)
                    bitsToRead = bits;

                bits -= bitsToRead;

                value |= (uint)((bytePart >> shiftedBits) & ((byte)(1 << bitsToRead) - 1)) << bits;

                count += bitsToRead;
            }

            return (T)Convert.ChangeType(value, typeof(T));
        }

        public string ReadFourCC()
        {
            var data = BitConverter.GetBytes(Read<uint>(32));

            Array.Reverse(data);

            return Encoding.UTF8.GetString(data).Trim(new char[] { '\0' });
        }
        #endregion

        #region Writer
        public void Write<T>(T value)
        {
            var writer = stream as BinaryWriter;

            if (writer == null)
                throw new InvalidOperationException("");

            switch (Type.GetTypeCode(typeof(T)))
            {
                case TypeCode.SByte:
                    writer.Write(Convert.ToSByte(value));
                    break;
                case TypeCode.Byte:
                    writer.Write(Convert.ToByte(value));
                    break;
                case TypeCode.Int16:
                    writer.Write(Convert.ToInt16(value));
                    break;
                case TypeCode.UInt16:
                    writer.Write(Convert.ToUInt16(value));
                    break;
                case TypeCode.Int32:
                    writer.Write(Convert.ToInt32(value));
                    break;
                case TypeCode.UInt32:
                    writer.Write(Convert.ToUInt32(value));
                    break;
                case TypeCode.Int64:
                    writer.Write(Convert.ToInt64(value));
                    break;
                case TypeCode.UInt64:
                    writer.Write(Convert.ToUInt64(value));
                    break;
                case TypeCode.Single:
                    writer.Write(Convert.ToSingle(value));
                    break;
                default:
                    if (typeof(T) == typeof(byte[]))
                    {
                        Flush();

                        var data = value as byte[];
                        writer.Write(data);
                    }
                    break;
            }
        }

        public void Write<T>(T value, int bits)
        {
            var writer = stream as BinaryWriter;

            var bitsToWrite = 0;
            var shiftedBits = 0;

            var unpacked = (ulong)Convert.ChangeType(value, typeof(ulong));
            byte packedByte = 0;

            while (bits != 0)
            {
                shiftedBits = count & 7;

                if (shiftedBits != 0 && writer.BaseStream.Length > 0)
                    writer.BaseStream.Position -= 1;

                bitsToWrite = 8 - shiftedBits;

                if (bitsToWrite >= bits)
                    bitsToWrite = bits;

                packedByte = (byte)(preByte & ~(ulong)(((1 << bitsToWrite) - 1) << shiftedBits) | (((unpacked >> (bits - bitsToWrite)) & (ulong)((1 << bitsToWrite) - 1)) << shiftedBits));

                count += bitsToWrite;
                bits -= bitsToWrite;

                if (shiftedBits != 0)
                    preByte = 0;

                Write<byte>(packedByte);
            }

            preByte = packedByte;
        }

        public void Flush()
        {
            var remainingBits = 8 - (count & 7);

            if (remainingBits < 8)
                Write(0, remainingBits);

            preByte = 0;
        }

        public void WriteString(string data, int bits, bool isCString = true, int additionalCount = 0)
        {
            var bytes = Encoding.UTF8.GetBytes(data);

            Write(bytes.Length + additionalCount, bits);
            Write(bytes);

            if (isCString)
                Write(new byte[1]);

            Flush();
        }

        public void WriteFourCC(string data)
        {
            var bytes = Encoding.UTF8.GetBytes(data);

            Write(bytes);
        }
        #endregion
    }
}
