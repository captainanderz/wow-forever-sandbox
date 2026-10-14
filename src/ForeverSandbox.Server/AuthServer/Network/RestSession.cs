// Local HTTPS adapter: bounded HTTP requests and a single known demo account.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AuthServer.Network;

namespace AuthServer.AuthServer.Network;

public sealed class RestSession : IDisposable
{
    private readonly Socket client;
    public int DiagnosticConnectionId { get; } = global::AuthServer.LocalAuthDiagnostics.NewConnection();
    public RestSession(Socket socket) { client = socket; }
    public string GetCInfo() => client.RemoteEndPoint?.ToString();
    public void Accept()
    {
        using var network = new NetworkStream(client);
        using var tls = new SslStream(network, false);
        tls.ReadTimeout = 15000; tls.WriteTimeout = 5000;
        try {
            tls.AuthenticateAsServer(new SslServerAuthenticationOptions {
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ClientCertificateRequired = false, EnabledSslProtocols = SslProtocols.Tls12,
                ServerCertificate = global::AuthServer.LocalTls.ServerCertificate()
            });
            global::AuthServer.LocalAuthDiagnostics.Emit("tls_ready", new { connection=DiagnosticConnectionId, service="rest", protocol=tls.SslProtocol.ToString(), alpn=tls.NegotiatedApplicationProtocol.ToString() });
            for (int count=0; count<16; ++count) {
                var request = LocalHttpReader.Read(tls);
                if (request == null) break;
                var response = LocalLoginRest.Handle(request, global::AuthServer.LocalTls.Hostname, Sandbox.RestPort);
                global::AuthServer.LocalAuthDiagnostics.Emit("rest_request", new { connection=DiagnosticConnectionId,
                    method=request.Method == "GET" ? "GET" : request.Method == "POST" ? "POST" : "other", bytes=request.Bytes, route=response.route, status=response.status });
                var close = count == 15 || (request.Headers.TryGetValue("Connection", out var connection) && connection.Equals("close", StringComparison.OrdinalIgnoreCase));
                var body = Encoding.UTF8.GetBytes(response.json);
                var reason = response.status == 200 ? "OK" : response.status == 400 ? "Bad Request" : response.status == 404 ? "Not Found" : "Service Unavailable";
                var header = $"HTTP/1.1 {response.status} {reason}\r\nContent-Type: application/json;charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: {(close ? "close" : "keep-alive")}\r\n";
                if (response.cookie != null) header += "Set-Cookie: " + response.cookie + "\r\n";
                tls.Write(Encoding.ASCII.GetBytes(header + "\r\n")); tls.Write(body);
                if (close) break;
            }
        } catch (Exception error) {
            // Bodies, credentials, cookies, SRP values and tickets are excluded.
            global::AuthServer.LocalAuthDiagnostics.Emit("rest_failure", new { connection=DiagnosticConnectionId, errorType=error.GetType().Name });
        } finally { client.Dispose(); }
    }
    public void Dispose() => client.Dispose();
}
