using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace AuthServer.Network;

internal static class LocalLoginRest
{
    internal sealed class State {
        internal readonly object Gate = new();
        internal DemoSrpSession Srp;
        internal DateTime Expires = DateTime.UtcNow.AddMinutes(2);
    }
    private static readonly ConcurrentDictionary<string, State> Sessions = new();
    public static (int status, string json, string cookie, string route) Handle(LocalHttpRequest request, string host, int port)
    {
        if (host != "localhost" && host != "127.0.0.1" && host != "localhost.actual.battle.net") throw new ArgumentException("Local REST hostname required.");
        var now = DateTime.UtcNow;
        foreach (var entry in Sessions.Where(entry => entry.Value.Expires <= now)) {
            if (Sessions.TryRemove(entry.Key, out var removed)) { lock (removed.Gate) removed.Srp?.Dispose(); }
        }
        string id = null;
        if (request.Headers.TryGetValue("Cookie", out var cookies))
            id = cookies.Split(';').Select(value => value.Trim()).FirstOrDefault(value => value.StartsWith("JSESSIONID="))?[11..];
        if (id == null || id.Length != 64 || id.Any(ch => !Uri.IsHexDigit(ch)) || !Sessions.TryGetValue(id, out var state)) {
            if (Sessions.Count >= 128) return (503, "{}", null, "session_limit");
            id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            state = new State(); Sessions[id] = state;
        }
        var cookie = $"JSESSIONID={id}; Path=/; Secure; HttpOnly; SameSite=None";
        var formPath = request.Path == "/login/" || request.Path == "/bnetserver/login/";
        var srpPath = request.Path == "/login/srp/" || request.Path == "/bnetserver/login/srp/";
        if (request.Method == "GET" && formPath) {
            var form = new { type = "LOGIN_FORM", srp_url = $"https://{host}:{port}/bnetserver/login/srp/", inputs = new[] {
                new {input_id="account_name",type="text",label="Battle.net email",max_length=320},
                new {input_id="password",type="password",label="Password",max_length=128},
                new {input_id="log_in_submit",type="submit",label="Log in",max_length=0}
            }};
            return (200, JsonSerializer.Serialize(form), cookie, "form");
        }
        if (request.Method != "POST" || (!formPath && !srpPath)) return (404, "{}", cookie, "unsupported");
        try {
            using var document = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth=8 });
            var inputs = document.RootElement.GetProperty("inputs").EnumerateArray().ToArray();
            if (inputs.Length > 16) throw new FormatException("Too many login inputs.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var input in inputs) {
                var key = input.GetProperty("input_id").GetString(); var value = input.GetProperty("value").GetString();
                if (key == null || value == null || key.Length > 64 || value.Length > 4096 || !values.TryAdd(key, value)) throw new FormatException("Invalid login input.");
            }
            var account = values.GetValueOrDefault("account_name", "");
            if (!DemoSrpSession.IsAccepted(account)) return Failed(cookie, "account");
            lock (state.Gate) {
                state.Expires = DateTime.UtcNow.AddMinutes(2);
                if (srpPath) {
                    state.Srp?.Dispose(); state.Srp = new DemoSrpSession();
                    var challenge = new { version=2, iterations=DemoSrpSession.Iterations, modulus=DemoSrpSession.ModulusHex,
                        generator="02", hash_function="SHA-256", username=state.Srp.Username,
                        salt=Convert.ToHexString(state.Srp.Salt), public_B=state.Srp.PublicB };
                    return (200, JsonSerializer.Serialize(challenge), cookie, "srp_challenge");
                }
                if (state.Srp != null) {
                    var m2 = state.Srp.Verify(values.GetValueOrDefault("public_A", ""), values.GetValueOrDefault("client_evidence_M1", ""));
                    state.Srp.Dispose(); state.Srp = null;
                    if (m2 == null) return Failed(cookie, "srp_denied");
                    return (200, JsonSerializer.Serialize(new {authentication_state="DONE", login_ticket="FOREVER-SANDBOX-1", server_evidence_M2=m2}), cookie, "srp_complete");
                }
                // Retain the older explicit demo-password route only.
                if (values.GetValueOrDefault("password", "") == DemoSrpSession.Password)
                    return (200, "{\"authentication_state\":\"DONE\",\"login_ticket\":\"FOREVER-SANDBOX-1\"}", cookie, "demo_plaintext");
                return Failed(cookie, "login_denied");
            }
        } catch (Exception error) when (error is JsonException || error is FormatException || error is InvalidOperationException || error is KeyNotFoundException) {
            return (400, "{\"authentication_state\":\"LOGIN\",\"error_code\":\"UNABLE_TO_DECODE\"}", cookie, "invalid");
        }
    }
    private static (int, string, string, string) Failed(string cookie, string route)
        => (200, "{\"authentication_state\":\"DONE\",\"error_code\":\"INVALID_CREDENTIALS\"}", cookie, route);
}
