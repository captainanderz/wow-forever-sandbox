using System;
using System.Security.Cryptography;
using AuthServer.Network;
using Google.Protobuf;

namespace AuthServer.AuthServer.Packets.BnetHandlers;

internal static class AuthenticationV2Service
{
    public static bool Handles(uint method) => method >= 1 && method <= 3;
    private static readonly byte[] DemoTicket = System.Text.Encoding.UTF8.GetBytes(AuthenticationServerService.cred);
    private static bool IsDemo(ByteString token) => token.Length == DemoTicket.Length && CryptographicOperations.FixedTimeEquals(token.ToByteArray(), DemoTicket);
    private static void Complete(AuthSession session)
    {
        session.DemoAuthenticated = true;
        session.SendReply(Array.Empty<byte>());
        session.SendNotification(BgsAuthenticationV2.Complete(RandomNumberGenerator.GetBytes(64)), BgsAuthenticationV2.ListenerHash, 1);
        global::AuthServer.LocalAuthDiagnostics.Emit("bgs_v2_logon_complete", new { connection = session.DiagnosticConnectionId, sessionKeyBytes = 64 });
    }
    public static void Handle(AuthSession session, uint method, byte[] payload)
    {
        if (payload.Length > BgsAuthenticationV2.MaxRequestBytes) { session.SendReply(Array.Empty<byte>(), 3); return; }
        try {
            switch (method)
            {
                case 1:
                    var request = BgsAuthenticationV2.ReadLogon(payload);
                    global::AuthServer.LocalAuthDiagnostics.Emit("bgs_v2_logon", new { connection = session.DiagnosticConnectionId,
                        titleId = request.TitleId, applicationVersion = request.Build, platform = request.Platform, locale = request.Locale });
                    if (request.TitleId != BgsAuthenticationV2.WowTitleId || request.Build != 70338) { session.SendReply(Array.Empty<byte>(), 3); return; }
                    session.ClientBuild = request.Build;
                    if (IsDemo(request.AuthToken)) { Complete(session); return; }
                    // A foreign cached token never grants access to the local demo.
                    // Offer the local login challenge without logging/forwarding it.
                    session.SendNotification(BgsAuthenticationV2.Challenge(global::AuthServer.LocalTls.Hostname, Sandbox.RestPort), BgsAuthenticationV2.ListenerHash, 4);
                    session.SendReply(Array.Empty<byte>());
                    return;
                case 2:
                    var token = BgsAuthenticationV2.ReadToken(payload);
                    global::AuthServer.LocalAuthDiagnostics.Emit("bgs_v2_verify_token", new { connection = session.DiagnosticConnectionId, credentialBytes = token.Length });
                    if (IsDemo(token)) Complete(session);
                    else session.SendReply(Array.Empty<byte>(), 3);
                    return;
                case 3:
                    if (!session.DemoAuthenticated) { session.SendReply(Array.Empty<byte>(), 3); return; }
                    session.SendReply(BgsAuthenticationV2.TokenResponse(DemoTicket));
                    return;
                default: session.SendReply(Array.Empty<byte>(), 3); return;
            }
        } catch (Exception error) when (error is InvalidProtocolBufferException || error is FormatException) {
            global::AuthServer.LocalAuthDiagnostics.Emit("bgs_v2_invalid_request", new { connection = session.DiagnosticConnectionId, errorType = error.GetType().Name });
            session.SendReply(Array.Empty<byte>(), 3);
        }
    }
}
