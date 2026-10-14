namespace Framework.Constants
{
    public enum AccountDataTypes
    {
        GlobalConfigCache             = 0x1,
        CharacterConfigCache          = 0x2,
        GlobalBindingsCache           = 0x4,
        CharacterBindingsCache        = 0x8,
        GlobalMacrosCache             = 0x10,
        CharacterMacrosCache          = 0x20,
        CharacterLayoutCache          = 0x40,
        CharacterChatCache            = 0x80
    }

    public enum AccountDataMasks : uint
    {
        GlobalCacheMask               = 0x15,
        CharacterCacheMask            = 0xEA // 0xEF
    }
}
