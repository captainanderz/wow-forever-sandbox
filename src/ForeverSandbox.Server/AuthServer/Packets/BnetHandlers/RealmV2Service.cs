using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text;
using System.Diagnostics;
using System.Linq;
using AuthServer.Network;
using Google.Protobuf;

namespace AuthServer.AuthServer.Packets.BnetHandlers;

internal static class RealmV2Service
{
    public static void Handle(AuthSession session,uint method,byte[] payload)
    {
        try {
            var reply=BgsRealmV2.Reply(method,payload,session.RealmState,session.DemoAuthenticated,Sandbox.RealmPort);
            session.SendReply(reply.payload,reply.status);
            global::AuthServer.LocalAuthDiagnostics.Emit("bgs_v2_realm_reply",new {
                connection=session.DiagnosticConnectionId,methodId=method,status=reply.status,payloadBytes=reply.payload.Length,
                route=reply.route,clientSecretBytes=session.RealmState.ClientSecret?.Length ?? 0,
                modernWorldTransportImplemented=false });
        } catch(Exception error) when(error is InvalidProtocolBufferException || error is FormatException || error is JsonException || error is InvalidOperationException || error is KeyNotFoundException || error is OverflowException || error is DecoderFallbackException) {
            // Our own method names classify the stage without recording exception
            // messages (JSON exceptions can contain request data), JSON or values.
            var stage=new StackTrace(error,false).GetFrames()?.Select(frame=>frame.GetMethod()).FirstOrDefault(method=>method?.DeclaringType==typeof(BgsRealmV2))?.Name ?? "other";
            global::AuthServer.LocalAuthDiagnostics.Emit("bgs_v2_realm_invalid_request",new {connection=session.DiagnosticConnectionId,methodId=method,errorType=error.GetType().Name,stage});
            session.SendReply(Array.Empty<byte>(),0xBC5);
        }
    }
}
