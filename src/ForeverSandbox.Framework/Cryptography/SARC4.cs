namespace Framework.Cryptography
{
    public sealed class SARC4
    {
        byte[] s;
        byte tmp, tmp2;

        public SARC4()
        {
            s = new byte[0x100];
            tmp = 0;
            tmp2 = 0;
        }

        public void PrepareKey(byte[] key)
        {
            for (int i = 0; i < 0x100; i++)
                s[i] = (byte)i;

            var i2 = 0;

            for (int i = 0; i < 0x100; i++)
            {
                i2 = (byte)((i2 + s[i] + key[i % key.Length]) % 0x100);

                var tempS = s[i];

                s[i] = s[i2];
                s[i2] = tempS;
            }
        }

        public void ProcessBuffer(byte[] data, int length)
        {
            for (int i = 0; i < length; i++)
            {
                tmp = (byte)((tmp + 1) % 0x100);
                tmp2 = (byte)((tmp2 + s[tmp]) % 0x100);

                var sTemp = s[tmp];

                s[tmp] = s[tmp2];
                s[tmp2] = sTemp;

                data[i] = (byte)(s[(s[tmp] + s[tmp2]) % 0x100] ^ data[i]);
            }
        }
    }
}
