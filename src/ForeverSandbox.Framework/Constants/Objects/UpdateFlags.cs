using System;

namespace Framework.Constants
{
    [Flags]
    public enum UpdateFlag
    {
        Self                = 0x0001,
        Alive               = 0x0002,
        Rotation            = 0x0004,
        StationaryPosition  = 0x0008,
        Target              = 0x0010,
        Transport           = 0x0020,
        GoTransportPosition = 0x0040,
        AnimKits            = 0x0080,
        Vehicle             = 0x0100,
    }
}
