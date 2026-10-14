// Independent demo-account wire response. No account database or external login.
// Protocol evidence: pinned Forever AuthenticationService.cpp sends 64 raw bytes,
// not their 128-character hex representation, in v1 LogonResult.session_key.
using System;
using Bgs.Protocol;
using Bgs.Protocol.Authentication.V1;
using Google.Protobuf;

namespace AuthServer.Network;

internal static class BgsDemoAuthentication
{
    public static LogonResult Success(byte[] sessionKey)
    {
        if (sessionKey == null || sessionKey.Length != 64)
            throw new ArgumentException("Demo BGS session key must contain exactly 64 bytes.");
        var result = new LogonResult {
            ErrorCode = 0,
            AccountId = new EntityId { High = 0x100000000000000, Low = 1 },
            SessionKey = ByteString.CopyFrom(sessionKey)
        };
        result.GameAccountId.Add(new EntityId { High = 0x200000200576F57, Low = 1 });
        return result;
    }
}
