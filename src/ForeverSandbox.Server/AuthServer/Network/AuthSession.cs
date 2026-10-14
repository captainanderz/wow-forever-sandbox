using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using AuthServer.Packets;
using Bgs.Protocol;
using Framework.Constants.Misc;
using Framework.Cryptography.BNet;
using Framework.Database.Auth.Entities;
using Framework.Logging;
using Google.Protobuf;

namespace AuthServer.Network
{
    public class AuthSession : IDisposable
    {
        public uint ClientBuild;
        public bool DemoAuthenticated;
        internal BgsRealmState RealmState { get; } = new();
        internal uint RequestToken { get; set; }
        public int DiagnosticConnectionId { get; } = global::AuthServer.LocalAuthDiagnostics.NewConnection();
        public Account Account { get; set; }
        public List<Module> Modules { get; set; }
        public SRP6a SecureRemotePassword { get; set; }
        public BNetCrypt Crypt { get; set; }
        public bool IsMounted;

        Socket client;
        NetworkStream nStream;
        SslStream sslStream;

        public AuthSession(Socket socket)
        {
            client = socket;
        }

        public string GetCInfo()
        {
            var endPoint = client.RemoteEndPoint as IPEndPoint;
            return endPoint.Address + ":" + endPoint.Port;
        }

        public void Accept()
        {
            nStream = new NetworkStream(client);
            sslStream = new SslStream(nStream, false, App_CertificateValidation);

            var cert = global::AuthServer.LocalTls.ServerCertificate();
            //var chain = X509Chain.Create().Build(cert);
            SslServerAuthenticationOptions op = new SslServerAuthenticationOptions();
            op.ApplicationProtocols = new List<SslApplicationProtocol>();
            op.ApplicationProtocols.Add(SslApplicationProtocol.Http11);
            op.ApplicationProtocols.Add(SslApplicationProtocol.Http2);
            op.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
            op.ClientCertificateRequired = false;
            op.EnabledSslProtocols = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12 | SslProtocols.Tls13;
            op.RemoteCertificateValidationCallback = App_CertificateValidation;
            op.ServerCertificate = cert;
            op.AllowRenegotiation = true;

            try
            {
                sslStream.AuthenticateAsServer(op);
                global::AuthServer.LocalAuthDiagnostics.Emit("tls_ready", new { connection = DiagnosticConnectionId, service = "bgs", protocol = sslStream.SslProtocol.ToString(), alpn = sslStream.NegotiatedApplicationProtocol.ToString() });
            }
            catch (Exception e)
            {
                Console.WriteLine("SSL stream failure");
                global::AuthServer.LocalAuthDiagnostics.Emit("tls_failure", new { connection = DiagnosticConnectionId, service = "bgs", errorType = e.GetType().Name });
                client.Dispose();
                return;
            }

            try
            {
                while (true)
                {
                    var frame = BgsFrameReader.ReadNext(sslStream);
                    if (frame == null)
                    {
                        global::AuthServer.LocalAuthDiagnostics.Emit("bgs_eof", new { connection = DiagnosticConnectionId });
                        return;
                    }
                    PacketManager.InvokeHandler(this, frame.Header, frame.Payload);
                }
            }
            catch (BgsFramingException ex)
            {
                global::AuthServer.LocalAuthDiagnostics.Emit("bgs_framing_failure", new { connection = DiagnosticConnectionId, stage = ex.Stage, reason = ex.Reason, expectedBytes = ex.ExpectedBytes, readBytes = ex.ReadBytes, declaredBytes = ex.DeclaredBytes, limitBytes = ex.LimitBytes });
            }
            catch (Exception ex)
            {
                global::AuthServer.LocalAuthDiagnostics.Emit("bgs_read_failure", new { connection = DiagnosticConnectionId, errorType = ex.GetType().Name });
            }
            finally
            {
                sslStream?.Dispose();
                nStream?.Dispose();
                Dispose();
            }

        }


        bool App_CertificateValidation(Object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }

        public void Send(byte[] rawdata) => sslStream.Write(rawdata);

        uint token = 0;

        public void Send(IMessage message)
        {
            SendReply(message.ToByteArray());
        }

        internal void SendReply(byte[] payload, uint status = 0)
        {
            try
            {
                var frame = BgsFrameWriter.Reply(RequestToken, payload, status);
                sslStream.Write(frame);
            }
            catch (SocketException ex)
            {
                Log.Message(LogType.Error, "{0}", ex.Message);

                client.Close();
            }
        }

        public void Send(IMessage message, uint serviceHash, uint methodId)
            => SendNotification(message.ToByteArray(), serviceHash, methodId);

        internal void SendNotification(byte[] payload, uint serviceHash, uint methodId)
        {
            try
            {
                sslStream.Write(BgsFrameWriter.Notification(token++, serviceHash, methodId, payload));
            }
            catch (Exception ex)
            {
                Log.Message(LogType.Error, "{0}", ex.Message);

                client.Close();
            }
        }

        void SendCompleted(object sender, SocketAsyncEventArgs e)
        {
        }

        public void Dispose()
        {
            RealmState.Dispose();
            client?.Dispose();

            client = null;
            Account = null;
            SecureRemotePassword = null;
        }
    }
}
