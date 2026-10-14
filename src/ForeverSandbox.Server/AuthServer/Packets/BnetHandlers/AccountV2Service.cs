using System;
using AuthServer.Network;
using Google.Protobuf;

namespace AuthServer.AuthServer.Packets.BnetHandlers;

internal static class AccountV2Service
{
    public static void Handle(AuthSession session, uint method, byte[] payload)
    {
        try {
            var reply = BgsAccountV2.Reply(method, payload, session.DemoAuthenticated);
            session.SendReply(reply.payload, reply.status);
            global::AuthServer.LocalAuthDiagnostics.Emit("bgs_v2_account_reply", new {
                connection = session.DiagnosticConnectionId, methodId = method, status = reply.status, payloadBytes = reply.payload.Length });
        } catch (Exception error) when (error is InvalidProtocolBufferException || error is FormatException) {
            global::AuthServer.LocalAuthDiagnostics.Emit("bgs_v2_account_invalid_request", new {
                connection = session.DiagnosticConnectionId, methodId = method, errorType = error.GetType().Name });
            session.SendReply(Array.Empty<byte>(), 3);
        }
    }
}
