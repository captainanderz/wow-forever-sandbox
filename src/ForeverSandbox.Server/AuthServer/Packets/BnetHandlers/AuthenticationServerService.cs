using AuthServer.AuthServer.Attributes;
using AuthServer.Network;
using Bgs.Protocol;
using Bgs.Protocol.Authentication.V1;
using Bgs.Protocol.Challenge.V1;
using Framework.Constants.Net;
using Framework.Misc;
using Google.Protobuf;

namespace AuthServer.AuthServer.Packets.BnetHandlers
{
    [BnetService(BnetServiceHash.AuthenticationServerService)]
    public class AuthenticationServerService : BnetServiceBase
    {
        public const string cred = "FOREVER-SANDBOX-1";
        [BnetMethod(1)]
        public static void HandleConnectRequest(AuthSession session, LogonRequest logonRequest)
        {
            global::AuthServer.LocalAuthDiagnostics.Emit("bgs_logon", new { connection = session.DiagnosticConnectionId, applicationVersion = logonRequest.ApplicationVersion });
            session.ClientBuild = (uint)logonRequest.ApplicationVersion;
           // if (logonRequest.WebClientVerification == true)// && logonRequest.Email == "admin@admin")
            {
                //Console.WriteLine("Logon request with account 'admin@admin' and 'WebClientVerification' enabled.");

                var cur = new ChallengeExternalRequest();

                //cur.RequestToken = "asdasd";
                cur.PayloadType = "web_auth_url";
                cur.Payload = ByteString.CopyFromUtf8($"https://{(global::AuthServer.LocalTls.Hostname)}:{Sandbox.RestPort}/login/?app=wow");

                session.Send(cur, 0xBBDA171Fu, 3);
                // The listener notification does not complete the original RPC.
                session.Send(new NoData());
            }

           
        }

        [BnetMethod(7)]
        public static void HandleVerifyWebCredentialsRequest(AuthSession session, VerifyWebCredentialsRequest verifyWebCredentialsRequest)
        {
            global::AuthServer.LocalAuthDiagnostics.Emit("bgs_verify_web_credentials", new { connection = session.DiagnosticConnectionId, credentialBytes = verifyWebCredentialsRequest.WebCredentials.Length });
            // login ticket from previous REST packet.
            if (verifyWebCredentialsRequest.WebCredentials.ToStringUtf8() == cred)
            {
                session.Send(new NoData());
                var logonResult = BgsDemoAuthentication.Success(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
                session.Send(logonResult, 0x71240E35, 5);
            }
            else
            {
                session.SendReply(System.Array.Empty<byte>(), 0x3); // ERROR_DENIED
            }
        }
    }

}
