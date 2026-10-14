using System;

namespace Framework.ObjectDefines
{
    public class Quaternion
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly float W;

        const float multiplier = 0.00000095367432f;

        public Quaternion(long compressedQuaternion)
        {
            var c = compressedQuaternion;

            X = (c >> 42) * 0.00000047683716f;
            Y = (c >> 21) * multiplier;
            Z = c * multiplier;

            W = (X * X) + (Y * Y) + (Z * Z);

            if (Math.Abs(W - 1.0f) >= multiplier)
                W = (float)Math.Sqrt(1.0f - W);
            else
                W = 0;
        }

        public static long GetCompressed(float orientation)
        {
            var z = (float)Math.Sin(orientation / 1.9999945);
            var com = (long)(z / Math.Atan(Math.Pow(2, -20)));

            return orientation < Math.PI ? com : ((0x100000 - com) << 1) + com;
        }
    }
}
