using System;
using System.Security.Cryptography;

namespace Framework.Cryptography.WoW
{
    public sealed class WoWCrypt : IDisposable
    {
        public bool IsInitialized { get; set; }

        static readonly byte[] ServerEncryptionKey = { 0x08, 0xF1, 0x95, 0x9F, 0x47, 0xE5, 0xD2, 0xDB, 0xA1, 0x3D, 0x77, 0x8F, 0x3F, 0x3E, 0xE7, 0x00 };
        static readonly byte[] ServerDecryptionKey = { 0x40, 0xAA, 0xD3, 0x92, 0x26, 0x71, 0x43, 0x47, 0x3A, 0x31, 0x08, 0xA6, 0xE7, 0xDC, 0x98, 0x2A };

        SARC4 SARC4Encrypt, SARC4Decrypt;

        public WoWCrypt()
        {
            IsInitialized = false;
        }

        public void Initialize(byte[] sessionKey)
        {
            IsInitialized = false;

            if (IsInitialized)
                throw new InvalidOperationException("PacketCrypt already initialized!");

            SARC4Encrypt = new SARC4();
            SARC4Decrypt = new SARC4();

            var decryptSHA1 = new HMACSHA1(ServerDecryptionKey);
            var encryptSHA1 = new HMACSHA1(ServerEncryptionKey);

            SARC4Encrypt.PrepareKey(encryptSHA1.ComputeHash(sessionKey));
            SARC4Decrypt.PrepareKey(decryptSHA1.ComputeHash(sessionKey));

            var PacketEncryptionDummy = new byte[0x400];
            var PacketDecryptionDummy = new byte[0x400];

            SARC4Encrypt.ProcessBuffer(PacketEncryptionDummy, PacketEncryptionDummy.Length);
            SARC4Decrypt.ProcessBuffer(PacketDecryptionDummy, PacketDecryptionDummy.Length);

            IsInitialized = true;
        }

        public void Initialize(byte[] sessionKey, byte[] clientSeed, byte[] serverSeed)
        {
            IsInitialized = false;

            if (IsInitialized)
                throw new InvalidOperationException("PacketCrypt already initialized!");

            SARC4Encrypt = new SARC4();
            SARC4Decrypt = new SARC4();

            var decryptSHA1 = new HMACSHA1(serverSeed);
            var encryptSHA1 = new HMACSHA1(clientSeed);

            SARC4Encrypt.PrepareKey(encryptSHA1.ComputeHash(sessionKey));
            SARC4Decrypt.PrepareKey(decryptSHA1.ComputeHash(sessionKey));

            var PacketEncryptionDummy = new byte[0x400];
            var PacketDecryptionDummy = new byte[0x400];

            SARC4Encrypt.ProcessBuffer(PacketEncryptionDummy, PacketEncryptionDummy.Length);
            SARC4Decrypt.ProcessBuffer(PacketDecryptionDummy, PacketDecryptionDummy.Length);

            IsInitialized = true;
        }

        public void Encrypt(byte[] data, int count)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("PacketCrypt not initialized!");

            SARC4Encrypt.ProcessBuffer(data, count);
        }

        public void Decrypt(byte[] data, int count)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("PacketCrypt not initialized!");

            SARC4Decrypt.ProcessBuffer(data, count);
        }

        public void Dispose()
        {
            IsInitialized = false;
        }
    }
}
