namespace Framework.Constants.Net
{
    public enum AuthServerMessage : ushort
    {
        #region None
        Pong = 0x00,
        Complete = 0x00,
        ProofRequest = 0x02,
        #endregion
        #region WoW
        RealmUpdate = 0x02,
        JoinResponse = 0x08
        #endregion
    }
}
