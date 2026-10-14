using System;

namespace Framework.Constants
{
    [Serializable()]
    public enum GuidType
    {
        Null = 0,
        Uniq = 1,
        Player = 2,
        Item = 3,
        WorldTransaction = 4,
        StaticDoor = 5,
        Transport = 6,
        Conversation = 7,
        Creature = 8,
        Vehicle = 9,
        Pet = 10,
        GameObject = 11,
        //Transport       = 0xF12,
        Unit            = 8,
        //Pet             = 0xF14,
        //Vehicle         = 0xF15,
    }
}
