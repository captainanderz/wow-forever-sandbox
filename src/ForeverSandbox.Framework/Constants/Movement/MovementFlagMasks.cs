using System;

namespace Framework.Constants.Movement
{
    [Flags]
    public enum MovementFlagMask
    {
        FreeLook     = 0x0,
        MovePlayer   = 0x3,
        StrafePlayer = 0xC,
        TurnPlayer   = 0x30,
        PitchPlayer  = 0xC0
    }
}
