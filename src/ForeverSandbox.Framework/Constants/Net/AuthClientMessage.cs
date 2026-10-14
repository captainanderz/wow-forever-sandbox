namespace Framework.Constants.Net
{
    public enum AuthClientMessage : ushort
    {
        #region None
        Ping = 0x00,
        ProofResponse = 0x02,
        InformationRequest = 0x09,
        #endregion
        #region WoW
        RealmUpdate = 0x00,
        JoinRequest = 0x08,
        #endregion
    }
}
