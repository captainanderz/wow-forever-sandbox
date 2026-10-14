namespace Framework.Constants.Authentication
{
    public enum AuthResult : byte
    {
        GlobalSuccess       = 0x00,
        BadLoginInformation = 0x68,
        InvalidProgram      = 0x6D,
        InvalidPlatform     = 0x6E,
        InvalidLocale       = 0x6F,
        InvalidGameVersion  = 0x70,
        AlreadyLoggedIn     = 0xCD,
    }
}
