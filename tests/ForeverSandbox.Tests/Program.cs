using System.Text;
using System.Text.Json;
using AuthServer.Network;
using Bgs.Protocol;
using Bgs.Protocol.Connection.V1;
using Bgs.Protocol.Authentication.V1;
using Bgs.Protocol.Challenge.V1;
using Google.Protobuf;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

var results = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
byte[] MakeFrame(byte[] payload, uint? declaredPayload = null, uint method = 7)
{
    var header = new Header { ServiceHash = 0xAABBCCDD, MethodId = method, Size = declaredPayload ?? (uint)payload.Length }.ToByteArray();
    return new[] { (byte)(header.Length >> 8), (byte)header.Length }.Concat(header).Concat(payload).ToArray();
}
void Run(string name, Action action) { action(); results.Add(name); }
void CheckPayload(Stream input, byte[] expected, uint method = 7)
{
    var frame = BgsFrameReader.ReadNext(input);
    Check(frame != null && frame.Header.MethodId == method && frame.Header.ServiceHash == 0xAABBCCDD, "Header metadata changed.");
    Check(frame.Payload.SequenceEqual(expected), "Payload changed.");
}
void ExpectFailure(byte[] bytes, string stage, string reason, int expected = -1, int read = -1)
{
    using var input = new ChunkedStream(bytes, 1);
    try { BgsFrameReader.ReadNext(input); throw new Exception("Expected framing failure was not raised."); }
    catch (BgsFramingException error)
    {
        Check(error.Stage == stage && error.Reason == reason, "Wrong framing failure classification.");
        if (expected >= 0) Check(error.ExpectedBytes == expected, "Wrong expected byte count.");
        if (read >= 0) Check(error.ReadBytes == read, "Wrong partial byte count.");
        Check(!error.Message.Contains("DO_NOT_LOG_THIS_TICKET"), "Framing failure echoed payload data.");
    }
}

if(args.Length==3 && args[0]=="--probe-world"){
    if(args[1]!="127.0.0.1")throw new ArgumentException("Loopback world probe only.");
    using var tcp=new TcpClient();tcp.ConnectAsync(args[1],int.Parse(args[2])).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    using var stream=tcp.GetStream();stream.ReadTimeout=stream.WriteTimeout=5000;
    Check(ForeverWorldWire.ReadInitializer(stream)==ForeverWorldWire.ServerInitializer,"Wrong world initializer.");
    foreach(var value in Encoding.ASCII.GetBytes(ForeverWorldWire.ClientInitializer))stream.WriteByte(value);
    WorldFixtures.CheckChallenge(ForeverWorldWire.Read(stream));
    stream.Write(ForeverWorldWire.Pack(0x460001,WorldFixtures.AuthPayload()));
    Check(ForeverWorldWire.Read(stream)==null,"Bounded world handshake probe did not close after metadata parsing.");
    Console.WriteLine(JsonSerializer.Serialize(new{passed=true,initializer=true,authChallengeOpcode="0x004E0000",modernHeaderBytes=16,opcodeBytes=4,challengeBytes=32,authSessionShapeAccepted=true,worldAuthenticationCompleted=false}));return;
}
if ((args.Length == 4 || args.Length == 5 && args[0] == "--probe-auth-v2") && (args[0] == "--probe-connect" || args[0] == "--probe-auth" || args[0] == "--probe-auth-v2"))
{
    var hostname = args[1];
    if (hostname != "localhost" && hostname != "127.0.0.1") throw new ArgumentException("Loopback probe only.");
    var port = int.Parse(args[2]);
    using var tcp = new TcpClient();
    tcp.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    using var tls = new SslStream(tcp.GetStream()); // Platform validation, no bypass callback.
    tls.ReadTimeout = tls.WriteTimeout = 5000;
    tls.AuthenticateAsClient(new SslClientAuthenticationOptions { TargetHost = hostname, EnabledSslProtocols = SslProtocols.Tls12,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck });
    using var peer = new X509Certificate2(tls.RemoteCertificate);
    Check(peer.Thumbprint.Equals(args[3],StringComparison.OrdinalIgnoreCase), "Unexpected local certificate.");
    var requestBody = new ConnectRequest { UseBindlessRpc = true }.ToByteArray();
    const uint requestToken = 0x89ABCDEF;
    var header = new Header { ServiceId = 0, ServiceHash = 0x65446991, MethodId = 1, Token = requestToken, Size = (uint)requestBody.Length }.ToByteArray();
    var wire = new[] { (byte)(header.Length >> 8), (byte)header.Length }.Concat(header).Concat(requestBody).ToArray();
    foreach (var value in wire) tls.WriteByte(value); // Exercise fragmented live reads.
    var reply = BgsFrameReader.ReadNext(tls);
    Check(reply != null && reply.Header.Token == requestToken && reply.Header.ServiceId == 0xFE, "Live reply correlation failed.");
    var connected = ConnectResponse.Parser.ParseFrom(reply.Payload);
    Check(connected.ClientId != null && connected.ClientId.Label != 0 && connected.ClientId.Epoch != 0, "Live reply did not assign an identity.");
    Check(connected.ServerId != null && connected.ServerId.Label != 0 && connected.UseBindlessRpc, "Live reply identity/RPC fields missing.");
    Check(Math.Abs((long)connected.ServerTime-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 10000, "Live reply UTC milliseconds invalid.");
    var ciid = ReadCiid(reply.Payload);
    Check(ciid != null && System.Text.RegularExpressions.Regex.IsMatch(ciid,"^[0-9A-F]{16}-[0-9A-F]{16}$"), "Live CIID missing.");
    if (args[0] == "--probe-auth-v2")
    {
        void Rpc(uint service, uint method, uint token, byte[] body)
            => tls.Write(BgsFrameWriter.Notification(token, service, method, body));
        void Ack(uint token, uint status) {
            var response = BgsFrameReader.ReadNext(tls);
            Check(response != null && response.Header.ServiceId == 0xFE && response.Header.Token == token && response.Header.Status == status && response.Payload.Length == 0, "V2 RPC status/correlation changed.");
        }
        Rpc(BgsAccountV2.ServiceHash, 101, 100, Array.Empty<byte>()); Ack(100, 3);
        Rpc(BgsAuthenticationV2.ServiceHash, 1, 101, V2LogonRequest());
        var challenge = BgsFrameReader.ReadNext(tls);
        Check(challenge != null && challenge.Header.ServiceHash == 0x9DA8116B && challenge.Header.MethodId == 4, "Missing v2 external challenge route.");
        var url = ChallengeExternalRequest.Parser.ParseFrom(challenge.Payload);
        Check(url.PayloadType == "web_auth_url" && Uri.TryCreate(url.Payload.ToStringUtf8(), UriKind.Absolute, out var authUri) && authUri.IsLoopback && authUri.Scheme == "https", "V2 challenge was not local HTTPS.");
        Ack(101, 0);
        Rpc(BgsAuthenticationV2.ServiceHash, 3, 102, Array.Empty<byte>()); Ack(102, 3);
        Rpc(BgsAuthenticationV2.ServiceHash, 2, 103, BgsAuthenticationV2.TokenResponse(Encoding.UTF8.GetBytes("FOREVER-SANDBOX-1"))); Ack(103, 0);
        var complete = BgsFrameReader.ReadNext(tls);
        Check(complete != null && complete.Header.ServiceHash == 0x9DA8116B && complete.Header.MethodId == 1, "V2 completion notification missing.");
        var envelope = new CodedInputStream(complete.Payload);
        Check(envelope.ReadTag() == 8 && envelope.ReadUInt32() == 0 && envelope.ReadTag() == 18, "V2 completion envelope invalid.");
        var record = new CodedInputStream(envelope.ReadBytes().ToByteArray());
        ByteString sessionKey = null;
        uint field;
        while ((field = record.ReadTag()) != 0) { if (field == 42) sessionKey = record.ReadBytes(); else record.SkipLastField(); }
        Check(sessionKey != null && sessionKey.Length == 64, "V2 live session key was not 64 raw bytes.");
        // Match the actual beta's four pipelined account RPCs after LogonComplete.
        Rpc(BgsAccountV2.ServiceHash, 101, 120, Array.Empty<byte>());
        Rpc(BgsAccountV2.ServiceHash, 104, 121, Array.Empty<byte>());
        Rpc(BgsAccountV2.ServiceHash, 201, 122, GameAccountRequest());
        Rpc(BgsAccountV2.ServiceHash, 203, 123, GameAccountRequest());
        var infoFrame = BgsFrameReader.ReadNext(tls);
        Check(infoFrame.Header.Token == 120 && infoFrame.Header.ServiceId == 0xFE && infoFrame.Header.Status == 0, "Live account info correlation failed.");
        CheckAccountInfo(infoFrame.Payload);
        Ack(121, 0);
        var gameInfoFrame = BgsFrameReader.ReadNext(tls);
        Check(gameInfoFrame.Header.Token == 122 && gameInfoFrame.Header.ServiceId == 0xFE && gameInfoFrame.Header.Status == 0 && gameInfoFrame.Payload.SequenceEqual(new byte[] {10,8,8,1,18,4,87,111,87,49}), "Live game-account info wire/correlation failed.");
        Ack(123, 0);
        Rpc(BgsAccountV2.ServiceHash, 201, 124, GameAccountRequest(2)); Ack(124, 3);
        byte[] RealmRpc(uint method,uint token,byte[] body) {
            Rpc(0x5DBB51C2,method,token,body);
            var response=BgsFrameReader.ReadNext(tls);
            Check(response!=null && response.Header.ServiceId==0xFE && response.Header.Token==token && response.Header.Status==0,"Realm RPC status/correlation failed.");
            return response.Payload;
        }
        var realmTicket=RealmFixtures.Read(RealmRpc(1,130,RealmFixtures.Ticket()));
        Check(realmTicket["Param_RealmListTicket"].Blob.SequenceEqual(Encoding.ASCII.GetBytes("AuthRealmListTicket\0")),"Live realm ticket invalid.");
        var proxies=RealmFixtures.Read(RealmRpc(1,131,RealmFixtures.Task("Command_FetchBleepProxiesRequest_v1_b9")));
        using(var proxyJson=RealmFixtures.Json(proxies["Param_BleepProxyList"].Blob,"JSONBleepProxyList"))Check(proxyJson.RootElement.GetProperty("proxies").GetArrayLength()==0,"Unexpected bleep proxy.");
        Check(RealmRpc(2,132,RealmFixtures.KeyRequest()).SequenceEqual(new byte[]{10,9,34,7,55,48,45,49,45,55,48}),"Live subregion wire changed.");
        RealmFixtures.CheckList(RealmRpc(1,133,RealmFixtures.Attributes(new[]{("Command_RealmListRequest_v1_b9",RealmFixtures.String("70-1-70"))})));
        var district=RealmFixtures.Read(RealmRpc(1,134,RealmFixtures.Task("Command_SuperDistrictListRequest_v1_b9")));
        using(var districtJson=RealmFixtures.Json(district["Param_SuperDistrictList"].Blob,"JSONSuperDistrictList"))Check(districtJson.RootElement.GetProperty("superDistricts")[0].GetProperty("superDistrictID").GetInt32()==2,"Live ruleset invalid.");
        var joined=RealmFixtures.Read(RealmRpc(1,135,RealmFixtures.Task("Command_RealmJoinRequest_v1_b9",("Param_RealmAddress",RealmFixtures.Number(0x46010046)))));
        using(var addresses=RealmFixtures.Json(joined["Param_ServerAddresses"].Blob,"JSONRealmListServerIPAddresses"))Check(addresses.RootElement.GetProperty("families")[0].GetProperty("addresses")[0].GetProperty("ip").GetString()=="127.0.0.1" && joined["Param_JoinSecret"].Blob.Length==32,"Live join was not local/32 bytes.");
        // Optional 5th argument: the realm port; the joined client secret (0..31) + join secret key the session.
        var worldSession = args.Length == 5 ? WorldFixtures.WorldSessionClient(int.Parse(args[4]), Enumerable.Range(0,32).Select(i=>(byte)i).Concat(joined["Param_JoinSecret"].Blob).ToArray()) : null;
        Rpc(BgsAuthenticationV2.ServiceHash, 3, 104, Array.Empty<byte>());
        var generated = BgsFrameReader.ReadNext(tls);
        Check(generated != null && generated.Header.Token == 104 && generated.Header.ServiceId == 0xFE && generated.Header.Status == 0 && BgsAuthenticationV2.ReadToken(generated.Payload).Length != 0, "V2 token generation failed.");
        Rpc(BgsAuthenticationV2.ServiceHash, 2, 105, BgsAuthenticationV2.TokenResponse(Encoding.UTF8.GetBytes("INVALID_LOCAL_FIXTURE"))); Ack(105, 3);
        Rpc(0x65446991, 5, 106, Array.Empty<byte>()); Ack(106, 0);
        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, normalTlsValidation = true, exactCertificatePinned = true,
            connect = true, authenticationVersion = 2, logonRpcCompleted = true, localChallenge = true,
            unauthenticatedTokenGenerationDenied = true, demoLogonComplete = true, rawSessionKeyBytes = 64,
            authenticatedTokenGeneration = true, invalidTokenDenied = true, keepAliveAnswered = true,
            unauthenticatedAccountAccessDenied = true, accountInfo = true, gameAccountInfo = true,
            accountRestrictions = true, gameAccountRestrictions = true, foreignGameAccountDenied = true,
            pipelinedAccountRequestsCorrelated = true, realmListTicket=true, realmSubregion=true, realmList=true,
            superDistricts=true, bleepProxies=true, localRealmJoin=true, worldSession, modernWorldCompatibility="unverified", gameCompatibility = "unverified" }));
        return;
    }
    if (args[0] == "--probe-auth")
    {
        void SendAuth(IMessage message, uint method, uint token)
        {
            var body = message.ToByteArray();
            var authHeader = new Header { ServiceId = 0, ServiceHash = 0x0DECFC01, MethodId = method, Token = token, Size = (uint)body.Length }.ToByteArray();
            tls.Write(new[] { (byte)(authHeader.Length >> 8), (byte)authHeader.Length }.Concat(authHeader).Concat(body).ToArray());
        }
        SendAuth(new LogonRequest { Program = "WoW", Platform = "Win64", Locale = "enUS", ApplicationVersion = 70338, WebClientVerification = true }, 1, 91);
        var logonFrames = new[] { BgsFrameReader.ReadNext(tls), BgsFrameReader.ReadNext(tls) };
        Check(logonFrames.All(frame => frame != null), "Logon closed before RPC completion/challenge.");
        var logonAck = logonFrames.Single(frame => frame.Header.ServiceId == 0xFE);
        Check(logonAck.Header.Token == 91 && logonAck.Header.Status == 0 && logonAck.Payload.Length == 0, "Logon RPC was not completed.");
        var challengeFrame = logonFrames.Single(frame => frame.Header.ServiceHash == 0xBBDA171F && frame.Header.MethodId == 3);
        var challenge = ChallengeExternalRequest.Parser.ParseFrom(challengeFrame.Payload);
        Check(challenge.PayloadType == "web_auth_url" && Uri.TryCreate(challenge.Payload.ToStringUtf8(), UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Scheme == "https", "Challenge was not loopback HTTPS.");
        SendAuth(new VerifyWebCredentialsRequest { WebCredentials = ByteString.CopyFromUtf8("FOREVER-SANDBOX-1") }, 7, 92);
        var verifiedFrames = new[] { BgsFrameReader.ReadNext(tls), BgsFrameReader.ReadNext(tls) };
        Check(verifiedFrames.All(frame => frame != null), "Credential verification closed before completion.");
        var verifyAck = verifiedFrames.Single(frame => frame.Header.ServiceId == 0xFE);
        Check(verifyAck.Header.Token == 92 && verifyAck.Header.Status == 0 && verifyAck.Payload.Length == 0, "Credential RPC was not completed.");
        var completeFrame = verifiedFrames.Single(frame => frame.Header.ServiceHash == 0x71240E35 && frame.Header.MethodId == 5);
        var complete = LogonResult.Parser.ParseFrom(completeFrame.Payload);
        Check(complete.ErrorCode == 0 && complete.SessionKey.Length == 64 && complete.GameAccountId.Count == 1, "Logon result/key shape was wrong.");
        SendAuth(new VerifyWebCredentialsRequest { WebCredentials = ByteString.CopyFromUtf8("INVALID_LOCAL_FIXTURE") }, 7, 93);
        var denied = BgsFrameReader.ReadNext(tls);
        Check(denied != null && denied.Header.ServiceId == 0xFE && denied.Header.Token == 93 && denied.Header.Status == 3 && denied.Payload.Length == 0, "Invalid local ticket was not denied by RPC status.");
        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, normalTlsValidation = true, exactCertificatePinned = true,
            connect = true, logonRpcCompleted = true, loopbackChallenge = true, credentialRpcCompleted = true,
            demoLogonComplete = true, rawSessionKeyBytes = 64, invalidTicketDenied = true, gameCompatibility = "unverified" }));
        return;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { passed = true, normalTlsValidation = true, exactCertificatePinned = true,
        requestTokenEchoed = true, missingClientIdentityAssigned = true, utcMilliseconds = true, ciidField9 = true,
        bindlessRpc = true, fragmentedRequest = true, gameCompatibility = "unverified" }));
    return;
}

var payload = Encoding.UTF8.GetBytes("DO_NOT_LOG_THIS_TICKET");
var frameBytes = MakeFrame(payload);
var headerLength = (frameBytes[0] << 8) | frameBytes[1];
Run("split_prefix", () => { using var input = new ChunkedStream(frameBytes, 1, 1, int.MaxValue); CheckPayload(input, payload); Check(BgsFrameReader.ReadNext(input) == null, "EOF after frame was not normal."); });
Run("split_header", () => { using var input = new ChunkedStream(frameBytes, 2, 1, 2, 1, 1, int.MaxValue); CheckPayload(input, payload); });
Run("split_payload", () => { using var input = new ChunkedStream(frameBytes, 2, headerLength, 1, 2, 1, 3, 1); CheckPayload(input, payload); });
Run("one_byte_reads", () => { using var input = new ChunkedStream(frameBytes, 1); CheckPayload(input, payload); });
Run("coalesced_frames", () => { using var input = new MemoryStream(frameBytes.Concat(MakeFrame(new byte[] { 9, 8, 7 }, method: 9)).ToArray()); CheckPayload(input, payload); CheckPayload(input, new byte[] { 9, 8, 7 }, 9); Check(BgsFrameReader.ReadNext(input) == null, "Coalesced EOF was not normal."); });
Run("clean_empty_eof", () => { using var input = new MemoryStream(); Check(BgsFrameReader.ReadNext(input) == null, "Empty EOF was not normal."); });
Run("zero_payload", () => { using var input = new ChunkedStream(MakeFrame(Array.Empty<byte>()), 1); CheckPayload(input, Array.Empty<byte>()); });
Run("partial_prefix_eof", () => ExpectFailure(new byte[] { 0 }, "prefix", "partial-eof", 2, 1));
Run("partial_header_eof", () => ExpectFailure(frameBytes.Take(2 + headerLength - 1).ToArray(), "header", "partial-eof", headerLength, headerLength - 1));
Run("partial_payload_eof", () => ExpectFailure(frameBytes.Take(frameBytes.Length - 1).ToArray(), "payload", "partial-eof", payload.Length, payload.Length - 1));
Run("oversized_header", () => ExpectFailure(new byte[] { 0x10, 0x01 }, "header-length", "header-too-large"));
Run("zero_header", () => ExpectFailure(new byte[] { 0, 0 }, "header-length", "zero-header-length"));
Run("oversized_payload", () => ExpectFailure(MakeFrame(Array.Empty<byte>(), (uint)BgsFrameReader.MaxPayloadBytes + 1), "payload-length", "payload-too-large"));
Run("invalid_protobuf_header", () => ExpectFailure(new byte[] { 0, 1, 0x80 }, "protobuf-header", "invalid-protobuf"));
Run("maximum_payload_boundary", () => { var maximum = new byte[BgsFrameReader.MaxPayloadBytes]; maximum[^1] = 17; using var input = new ChunkedStream(MakeFrame(maximum), 511); CheckPayload(input, maximum); });

string ReadCiid(byte[] bytes)
{
    var input = new CodedInputStream(bytes);
    string value = null;
    uint tag;
    while ((tag = input.ReadTag()) != 0) { if (tag == 74) value = input.ReadString(); else input.SkipLastField(); }
    return value;
}
var instant = new DateTimeOffset(2026, 10, 13, 11, 0, 0, TimeSpan.Zero);
Run("connect_assigns_absent_client_id", () => {
    var body = BgsConnectionHandshake.Build(new ConnectRequest { UseBindlessRpc = true }, 17, 42, instant);
    var response = ConnectResponse.Parser.ParseFrom(body);
    Check(response.ClientId != null && response.ClientId.Label == 17 && response.ClientId.Epoch == (uint)instant.ToUnixTimeSeconds(), "Absent client identity was not assigned.");
    Check(response.ServerId != null && response.ServerId.Label == 42 && response.UseBindlessRpc, "Connect identity/RPC fields are missing.");
});
Run("connect_preserves_supplied_client_id", () => {
    var request = new ConnectRequest { ClientId = new ProcessId { Label = 81, Epoch = 123 } };
    var response = ConnectResponse.Parser.ParseFrom(BgsConnectionHandshake.Build(request, 17, 42, instant));
    Check(response.ClientId.Label == 81 && response.ClientId.Epoch == 123, "Supplied client identity changed.");
    Check(request.ClientId.Label == 81 && request.ClientId.Epoch == 123, "Request identity was mutated.");
});
Run("connect_utc_millisecond_time", () => {
    var request = new ConnectRequest { UseBindlessRpc = true };
    var a = BgsConnectionHandshake.Build(request, 17, 42, instant);
    var b = BgsConnectionHandshake.Build(request, 17, 42, instant.ToOffset(TimeSpan.FromHours(2)));
    Check(a.SequenceEqual(b), "Local offset leaked into the wire response.");
    Check(ConnectResponse.Parser.ParseFrom(a).ServerTime == (ulong)instant.ToUnixTimeMilliseconds(), "Server time is not UTC milliseconds.");
});
Run("connect_ciid_wire_field_9", () => {
    var ciid = ReadCiid(BgsConnectionHandshake.Build(new ConnectRequest { UseBindlessRpc = true }, 17, 42, instant));
    Check(ciid != null && System.Text.RegularExpressions.Regex.IsMatch(ciid, "^[0-9A-F]{16}-[0-9A-F]{16}$"), "CIID wire field9 is missing or malformed.");
});
Run("reply_echoes_nonzero_request_token", () => {
    using var stream = new MemoryStream(BgsFrameWriter.Reply(0x89ABCDEF, payload));
    var decoded = BgsFrameReader.ReadNext(stream);
    Check(decoded.Header.Token == 0x89ABCDEF && decoded.Header.ServiceId == 0xFE, "Reply correlation token/service changed.");
    Check(decoded.Payload.SequenceEqual(payload), "Reply payload changed.");
});
Run("reply_payload_limit", () => {
    try { BgsFrameWriter.Reply(1, new byte[BgsFrameReader.MaxPayloadBytes + 1]); throw new Exception("Oversized response accepted."); }
    catch (ArgumentException) { }
});
Run("empty_rpc_success_correlates_request", () => {
    using var stream = new MemoryStream(BgsFrameWriter.Reply(0xABCDEF12, new NoData().ToByteArray()));
    var response = BgsFrameReader.ReadNext(stream);
    Check(response.Header.Token == 0xABCDEF12 && response.Header.Status == 0 && response.Payload.Length == 0, "NoData RPC success changed.");
});
Run("rpc_error_has_header_status", () => {
    using var stream = new MemoryStream(BgsFrameWriter.Reply(17, Array.Empty<byte>(), 3));
    var response = BgsFrameReader.ReadNext(stream);
    Check(response.Header.Token == 17 && response.Header.Status == 3 && response.Payload.Length == 0, "RPC error status or token changed.");
});
Run("logon_session_key_is_64_raw_bytes", () => {
    var expected = Enumerable.Range(0, 64).Select(value => (byte)(value * 3)).ToArray();
    var supplied = expected.ToArray();
    var result = BgsDemoAuthentication.Success(supplied);
    Array.Fill(supplied, (byte)0);
    var decoded = LogonResult.Parser.ParseFrom(result.ToByteArray());
    Check(decoded.SessionKey.Length == 64 && decoded.SessionKey.ToByteArray().SequenceEqual(expected), "Session key was hex encoded, aliased or changed.");
    Check(decoded.AccountId.High == 0x100000000000000 && decoded.GameAccountId.Single().High == 0x200000200576F57, "Demo account namespace changed.");
});
Run("logon_rejects_wrong_session_key_lengths", () => {
    foreach (var bytes in new byte[][] { null, Array.Empty<byte>(), new byte[63], new byte[65], new byte[128] }) {
        try { BgsDemoAuthentication.Success(bytes); throw new Exception("Wrong key length accepted."); }
        catch (ArgumentException) { }
    }
});
byte[] Proto(Action<CodedOutputStream> action) {
    using var memory = new MemoryStream();
    using (var output = new CodedOutputStream(memory, true)) { action(output); output.Flush(); }
    return memory.ToArray();
}
byte[] V2LogonRequest(byte[] token = null) => Proto(output => {
    output.WriteTag(1, WireFormat.WireType.Varint); output.WriteUInt32(0x576F57);
    output.WriteTag(2, WireFormat.WireType.LengthDelimited); output.WriteString("Win64");
    output.WriteTag(3, WireFormat.WireType.LengthDelimited); output.WriteString("enUS");
    output.WriteTag(4, WireFormat.WireType.Varint); output.WriteUInt32(70338);
    if (token != null) { output.WriteTag(10, WireFormat.WireType.LengthDelimited); output.WriteBytes(ByteString.CopyFrom(BgsAuthenticationV2.TokenResponse(token))); }
    output.WriteTag(100, WireFormat.WireType.Varint); output.WriteUInt32(42); // forward-compatible unknown field
});
byte[] GameAccountRequest(ulong id = 1, uint title = 0x576F57, uint region = 2) => Proto(output => {
    var handle = Proto(nested => {
        nested.WriteTag(1, WireFormat.WireType.Varint); nested.WriteUInt64(id);
        nested.WriteTag(2, WireFormat.WireType.Varint); nested.WriteUInt32(title);
        nested.WriteTag(3, WireFormat.WireType.Varint); nested.WriteUInt32(region);
    });
    output.WriteTag(1, WireFormat.WireType.LengthDelimited); output.WriteBytes(ByteString.CopyFrom(handle));
});
void CheckAccountInfo(byte[] payloadBytes) {
    var outer = new CodedInputStream(payloadBytes);
    Check(outer.ReadTag() == 10, "Account response missing info field 1.");
    var info = new CodedInputStream(outer.ReadBytes().ToByteArray());
    var numbers = new Dictionary<uint, ulong>();
    var strings = new Dictionary<uint, string>();
    uint tag;
    while ((tag = info.ReadTag()) != 0) {
        if ((tag & 7) == 0) numbers.Add(tag >> 3, info.ReadUInt64());
        else if ((tag & 7) == 2) strings.Add(tag >> 3, info.ReadString());
        else throw new Exception("Unexpected account field type.");
    }
    Check(outer.ReadTag() == 0 && numbers[1] == 1 && numbers[2] == 1 && numbers[8] == 1 && numbers[11] == 1 && numbers[14] == 7,
        "Account identity/region/privacy fields differ from wire contract.");
    Check(strings[5] == "Sandbox#0001" && strings[7] == "USA" && strings[9] == "enUS" && strings[10] == "USD" && !strings.ContainsKey(3),
        "Demo account metadata differs from wire contract or includes an email.");
}
Run("account_v2_info_fields_match_pinned_schema", () => {
    var reply = BgsAccountV2.Reply(101, Array.Empty<byte>(), true);
    Check(reply.status == 0, "Account info request denied."); CheckAccountInfo(reply.payload);
});
Run("account_v2_game_info_golden_wire", () => {
    var request = GameAccountRequest();
    Check(request.Length == 11, "Game account request differs from observed client width.");
    var reply = BgsAccountV2.Reply(201, request, true);
    Check(reply.status == 0 && reply.payload.SequenceEqual(new byte[] {10,8,8,1,18,4,87,111,87,49}), "Game info wire layout changed.");
});
Run("account_v2_restrictions_complete_with_empty_lists", () => {
    foreach (var method in new uint[] {104,203}) {
        var reply = BgsAccountV2.Reply(method, method == 203 ? GameAccountRequest() : Array.Empty<byte>(), true);
        Check(reply.status == 0 && reply.payload.Length == 0, "Restriction RPC did not complete with an empty list.");
    }
});
Run("account_v2_requires_login_and_exact_game_handle", () => {
    foreach (var method in new uint[] {101,104,201,203}) {
        var reply = BgsAccountV2.Reply(method, GameAccountRequest(), false);
        Check(reply.status == 3 && reply.payload.Length == 0, "Unauthenticated account access allowed.");
    }
    foreach (var request in new[] { Array.Empty<byte>(), GameAccountRequest(2), GameAccountRequest(title: 1), GameAccountRequest(region: 1) })
        Check(BgsAccountV2.Reply(201, request, true).status == 3 && BgsAccountV2.Reply(203, request, true).status == 3, "Foreign/missing game handle accepted.");
});
Run("account_v2_bounded_malformed_requests", () => {
    try { BgsAccountV2.Reply(101, new byte[BgsAccountV2.MaxRequestBytes + 1], true); throw new Exception("Oversized account request accepted."); } catch (FormatException) { }
    try { BgsAccountV2.Reply(201, new byte[] {10,5,1}, true); throw new Exception("Truncated account request accepted."); } catch (InvalidProtocolBufferException) { }
    try { BgsAccountV2.Reply(201, GameAccountRequest().Concat(GameAccountRequest()).ToArray(), true); throw new Exception("Duplicate game handle accepted."); } catch (FormatException) { }
});
Run("v2_logon_fields_and_nested_token", () => {
    var expected = Encoding.UTF8.GetBytes("LOCAL_FIXTURE_TOKEN");
    var request = BgsAuthenticationV2.ReadLogon(V2LogonRequest(expected));
    Check(request.TitleId == 0x576F57 && request.Build == 70338 && request.Platform == "Win64" && request.Locale == "enUS", "V2 field coordinates are wrong.");
    Check(request.AuthToken.ToByteArray().SequenceEqual(expected), "Nested v2 token changed.");
});
Run("v2_challenge_uses_fields_2_and_3", () => {
    var challenge = ChallengeExternalRequest.Parser.ParseFrom(BgsAuthenticationV2.Challenge("localhost", 8001));
    Check(challenge.PayloadType == "web_auth_url" && challenge.Payload.ToStringUtf8() == "https://localhost:8001/login/?app=wow", "V2 challenge field shape changed.");
});
Run("v2_challenge_rejects_external_host", () => {
    try { BgsAuthenticationV2.Challenge("example.com", 8001); throw new Exception("External challenge accepted."); } catch (ArgumentException) { }
});
Run("v2_complete_nested_account_and_raw_session_key", () => {
    var key = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
    var notification = new CodedInputStream(BgsAuthenticationV2.Complete(key));
    Check(notification.ReadTag() == 8 && notification.ReadUInt32() == 0 && notification.ReadTag() == 18, "V2 notification envelope changed.");
    var record = new CodedInputStream(notification.ReadBytes().ToByteArray());
    Check(record.ReadTag() == 8 && record.ReadUInt64() == 1 && record.ReadTag() == 18, "V2 record/account fields changed.");
    var account = new CodedInputStream(record.ReadBytes().ToByteArray());
    Check(account.ReadTag() == 8 && account.ReadUInt64() == 1 && account.ReadTag() == 16 && account.ReadUInt32() == 0x576F57 && account.ReadTag() == 24 && account.ReadUInt32() == 2, "V2 game account handle fields changed.");
    Check(record.ReadTag() == 26 && record.ReadString() == "Sandbox#0001" && record.ReadTag() == 42 && record.ReadBytes().ToByteArray().SequenceEqual(key), "V2 session key was changed/hex encoded.");
    Check(record.ReadTag() == 0 && notification.ReadTag() == 0, "Unexpected trailing v2 fields.");
});
Run("v2_auth_request_and_token_bounds", () => {
    try { BgsAuthenticationV2.ReadLogon(new byte[BgsAuthenticationV2.MaxRequestBytes + 1]); throw new Exception("Oversized v2 request accepted."); } catch (FormatException) { }
    var oversized = Proto(output => { output.WriteTag(1, WireFormat.WireType.LengthDelimited); output.WriteBytes(ByteString.CopyFrom(new byte[BgsAuthenticationV2.MaxTokenBytes + 1])); });
    try { BgsAuthenticationV2.ReadToken(oversized); throw new Exception("Oversized v2 token accepted."); } catch (FormatException) { }
    try { BgsAuthenticationV2.ReadToken(new byte[] { 10, 5, 1 }); throw new Exception("Truncated v2 token accepted."); } catch (InvalidProtocolBufferException) { }
});
Run("v2_complete_rejects_hex_length_key", () => {
    try { BgsAuthenticationV2.Complete(new byte[128]); throw new Exception("128-byte v2 session key accepted."); } catch (ArgumentException) { }
});
Run("v2_notification_service_and_method", () => {
    var payloadBytes = BgsAuthenticationV2.Challenge("127.0.0.1", 8001);
    using var stream = new MemoryStream(BgsFrameWriter.Notification(44, BgsAuthenticationV2.ListenerHash, 4, payloadBytes));
    var decoded = BgsFrameReader.ReadNext(stream);
    Check(decoded.Header.ServiceHash == 0x9DA8116B && decoded.Header.MethodId == 4 && decoded.Header.Token == 44 && decoded.Payload.SequenceEqual(payloadBytes), "V2 notification route/framing changed.");
});

using var srpVectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "srp-v2-vectors.json")));
var vectors = srpVectors.RootElement.GetProperty("vectors").EnumerateArray().ToArray();
DemoSrpSession SrpFixture(JsonElement vector) {
    var seed = vector.GetProperty("seed").GetInt32();
    return new DemoSrpSession(Convert.FromHexString(vector.GetProperty("salt").GetString()),
        Enumerable.Range(0, 256).Select(i => (byte)((i + seed + 1) % 256)).ToArray());
}
string VectorValue(JsonElement vector, string key) => vector.GetProperty(key).GetString();
string NormalizeHex(string hex) => hex.TrimStart('0').ToUpperInvariant();
Run("srp_independent_client_transcripts_both_pbkdf_signs", () => {
    Check(vectors.Length == 2 && vectors.Select(v => v.GetProperty("negativeExponentInput").GetBoolean()).Distinct().Count() == 2, "Signed PBKDF cases missing.");
    foreach (var vector in vectors) {
        using var srp = SrpFixture(vector);
        Check(srp.Username == VectorValue(vector, "username"), "SRP username derivation differs from client.");
        Check(NormalizeHex(srp.PublicB) == NormalizeHex(VectorValue(vector, "publicB")), "SRP public B differs from independent transcript.");
        var proof = srp.Verify(VectorValue(vector, "publicA"), VectorValue(vector, "clientM1"));
        Check(proof != null && NormalizeHex(proof) == NormalizeHex(VectorValue(vector, "serverM2")), "SRP client proof/server proof failed.");
        Check(srp.Verify(VectorValue(vector, "publicA"), VectorValue(vector, "clientM1")) == null, "SRP challenge accepted a replay.");
    }
});
Run("srp_rejects_bad_proof_and_consumes_challenge", () => {
    using var srp = SrpFixture(vectors[0]);
    Check(srp.Verify(VectorValue(vectors[0], "publicA"), "01") == null, "Bad client proof accepted.");
    Check(srp.Verify(VectorValue(vectors[0], "publicA"), VectorValue(vectors[0], "clientM1")) == null, "Bad attempt did not consume challenge.");
});
Run("srp_rejects_invalid_public_values_and_encoding", () => {
    foreach (var a in new[] { "0", DemoSrpSession.ModulusHex, "ZZ", "-1", new string('1', 513), "" }) {
        using var srp = SrpFixture(vectors[0]);
        Check(srp.Verify(a, VectorValue(vectors[0], "clientM1")) == null, "Invalid public A accepted.");
    }
    using var oversizedProof = SrpFixture(vectors[0]);
    Check(oversizedProof.Verify(VectorValue(vectors[0], "publicA"), new string('F', 65)) == null, "Oversized proof accepted.");
});
byte[] HttpWire(string method, string path, string body = "") {
    var bytes = Encoding.UTF8.GetBytes(body);
    return Encoding.ASCII.GetBytes($"{method} {path} HTTP/1.1\r\nHost: localhost\r\nContent-Length: {bytes.Length}\r\n\r\n").Concat(bytes).ToArray();
}
Run("http_fragmented_utf8_body_exact_length", () => {
    var body = "{\"value\":\"æøå\"}";
    var wire = HttpWire("POST", "/login/?app=wow", body);
    using var stream = new ChunkedStream(wire, 1);
    var request = LocalHttpReader.Read(stream);
    Check(request.Method == "POST" && request.Path == "/login/" && request.Body == body && request.Bytes == wire.Length, "Fragmented UTF8 HTTP body was changed.");
    Check(LocalHttpReader.Read(stream) == null, "HTTP EOF was not normal.");
});
Run("http_coalesced_requests_preserve_boundaries", () => {
    using var stream = new MemoryStream(HttpWire("GET", "/login/").Concat(HttpWire("POST", "/login/srp/", "{}" )).ToArray());
    Check(LocalHttpReader.Read(stream).Path == "/login/", "First HTTP request lost.");
    Check(LocalHttpReader.Read(stream).Body == "{}" && LocalHttpReader.Read(stream) == null, "HTTP framing consumed another request.");
});
Run("http_rejects_truncation_ambiguous_framing_and_bounds", () => {
    foreach (var wire in new[] {
        Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n"),
        HttpWire("POST", "/login/", "abc")[..^1],
        Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nContent-Length: -1\r\n\r\n"),
        Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nContent-Length: 65537\r\n\r\n"),
        Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nContent-Length: 0\r\ncontent-length: 0\r\n\r\n"),
        Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n"),
        Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nX-Long: " + new string('a', LocalHttpReader.MaxHeaderBytes) + "\r\n\r\n")
    }) {
        using var stream = new ChunkedStream(wire, 1);
        try { LocalHttpReader.Read(stream); throw new Exception("Malformed HTTP accepted."); }
        catch (Exception error) when (error is IOException || error is FormatException) { }
    }
});
Run("rest_form_advertises_local_srp_and_session_cookie", () => {
    var reply = LocalLoginRest.Handle(new LocalHttpRequest { Method="GET", Path="/login/" }, "localhost", 48001);
    using var form = JsonDocument.Parse(reply.json);
    Check(reply.status == 200 && form.RootElement.GetProperty("srp_url").GetString() == "https://localhost:48001/bnetserver/login/srp/", "SRP URL missing or wrong.");
    Check(reply.cookie.StartsWith("JSESSIONID=") && reply.cookie.Contains("Secure") && reply.cookie.Contains("HttpOnly"), "REST session cookie missing.");
});
Run("rest_challenge_cookie_persists_and_bad_proof_is_denied", () => {
    var form = LocalLoginRest.Handle(new LocalHttpRequest { Method="GET", Path="/login/" }, "localhost", 48001);
    var request = new LocalHttpRequest { Method="POST", Path="/bnetserver/login/srp/", Body=JsonSerializer.Serialize(new { inputs = new[] { new {input_id="account_name", value="admin"} } }) };
    request.Headers["Cookie"] = form.cookie.Split(';')[0];
    var challenge = LocalLoginRest.Handle(request, "localhost", 48001);
    using var parsed = JsonDocument.Parse(challenge.json);
    Check(challenge.status == 200 && challenge.cookie == form.cookie && parsed.RootElement.GetProperty("version").GetInt32() == 2 && parsed.RootElement.GetProperty("public_B").GetString().Length <= 512, "SRP challenge schema/session failed.");
    request.Path = "/login/";
    request.Body = JsonSerializer.Serialize(new { inputs = new[] { new {input_id="account_name", value="admin"}, new {input_id="public_A", value="0"}, new {input_id="client_evidence_M1", value="01"} } });
    var denied = LocalLoginRest.Handle(request, "localhost", 48001);
    Check(denied.route == "srp_denied" && !denied.json.Contains("login_ticket"), "Invalid SRP exchange produced a ticket.");
});

RealmFixtures.RunAll(Run);
WorldFixtures.RunAll(Run);
var report = new { passed = true, fixtureCount = results.Count, maxHeaderBytes = BgsFrameReader.MaxHeaderBytes, maxPayloadBytes = BgsFrameReader.MaxPayloadBytes, fixtures = results };
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (args.Length == 1) File.WriteAllText(Path.GetFullPath(args[0]), json);
Console.WriteLine(json);

internal sealed class ChunkedStream : Stream
{
    private readonly byte[] bytes;
    private readonly int[] chunks;
    private int position;
    private int calls;
    public ChunkedStream(byte[] bytes, params int[] chunks) { this.bytes = bytes; this.chunks = chunks; }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (position == bytes.Length) return 0;
        var chunk = chunks[Math.Min(calls++, chunks.Length - 1)];
        var read = Math.Min(Math.Min(count, chunk), bytes.Length - position);
        Array.Copy(bytes, position, buffer, offset, read);
        position += read;
        return read;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => bytes.Length;
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
