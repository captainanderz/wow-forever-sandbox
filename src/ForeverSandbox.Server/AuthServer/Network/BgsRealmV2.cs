// Independent local realm/game-utilities-v2 codec. Factual wire/JSON contracts:
// pinned 5a125dfe attribute_types, game_utilities_service, RealmList.proto,
// Realm.{h,cpp} and Classic realm ruleset metadata. No reference code imported.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;

namespace AuthServer.Network;

internal sealed class BgsRealmState : IDisposable
{
    internal byte[] ClientSecret, WorldKeyData;
    internal uint Platform, Arch, Type;
    internal bool TicketGranted;
    public void Dispose() {
        if (ClientSecret != null) CryptographicOperations.ZeroMemory(ClientSecret);
        if (WorldKeyData != null) CryptographicOperations.ZeroMemory(WorldKeyData);
        ClientSecret = WorldKeyData = null; TicketGranted = false;
    }
}
internal static class BgsRealmV2
{
    public const uint ServiceHash = 0x5DBB51C2, NameHash = 0x0FD547D8;
    public const uint RealmAddress = 0x46010046;
    public const string SubRegion = "70-1-70", DemoGameAccount = "sandbox";
    public const int MaxRequestBytes = 131072;
    private const uint Malformed = 0xBC5, Unimplemented = 0xBC7;
    internal sealed class Value {
        internal int Kind;
        internal string Text;
        internal byte[] Blob;
        internal ulong Unsigned;
        internal long Signed;
    }
    public static bool Handles(uint method) => method is 1 or 2;
    internal static string Command(string name) {
        // The protocol's final underscore-delimited suffix is opaque. Modern
        // clients use suffixes beyond the older decimal-looking b9.
        var separator=name.LastIndexOf('_');
        var unsuffixed=separator>=0 ? name[..separator] : name;
        foreach (var command in new[] { "RealmListTicketRequest", "RealmListRequest", "RealmJoinRequest", "LastCharPlayedRequest", "FetchBleepProxiesRequest", "SuperDistrictListRequest" }) {
            var key = "Command_" + command + "_v1";
            if (name == key || unsuffixed == key) return key;
        }
        return "";
    }
    public static (uint status, byte[] payload, string route) Reply(uint method, byte[] payload, BgsRealmState state, bool authenticated, int realmPort)
    {
        if (!authenticated) return (3, Array.Empty<byte>(), "unauthenticated");
        if (payload == null || payload.Length > MaxRequestBytes) throw new FormatException("Realm request exceeds limit.");
        if (realmPort < 1 || realmPort > 65535) throw new ArgumentException("Invalid local realm port.");
        if (method == 2) {
            var reader = new CodedInputStream(payload); string key = ""; uint tag;
            while ((tag = reader.ReadTag()) != 0) { if (tag == 10) key = reader.ReadString(); else reader.SkipLastField(); }
            if (key.Length > 128) throw new FormatException("Realm attribute key exceeds limit.");
            if (Command(key) != "Command_RealmListRequest_v1") return (Unimplemented, Array.Empty<byte>(), "unsupported_values");
            return (0, Pack(output => Bytes(output, 1, Variant(new Value { Kind=4, Text=SubRegion }))), "subregions");
        }
        if (method != 1) return (Unimplemented, Array.Empty<byte>(), "unsupported_method");
        var attributes = ReadAttributes(payload);
        var commands = attributes.Keys.Where(key => key.StartsWith("Command_", StringComparison.Ordinal)).ToArray();
        if (commands.Length != 1) return (Malformed, Array.Empty<byte>(), "invalid_command_count");
        var commandName = Command(commands[0]);
        var result = new Dictionary<string, Value>();
        switch (commandName) {
            case "Command_RealmListTicketRequest_v1":
                if (!attributes.TryGetValue("Param_Identity", out var identity) || identity.Kind != 5) return (3, Array.Empty<byte>(), "identity_missing");
                using (var document = Envelope(identity.Blob, "JSONRealmListTicketIdentity")) {
                    var root = document.RootElement;
                    if (root.GetProperty("gameAccountID").GetUInt32() != 1 || root.GetProperty("gameAccountRegion").GetUInt32() != 2)
                        return (3, Array.Empty<byte>(), "identity_denied");
                }
                if (!attributes.TryGetValue("Param_ClientInfo", out var clientInfo) || clientInfo.Kind != 5) return (3, Array.Empty<byte>(), "client_info_missing");
                using (var document = Envelope(clientInfo.Blob, "JSONRealmListTicketClientInformation")) {
                    var info = document.RootElement.GetProperty("info");
                    if (info.GetProperty("version").GetProperty("versionBuild").GetUInt32() != 70338) return (3, Array.Empty<byte>(), "build_denied");
                    var encoded = info.GetProperty("secret");
                    byte[] secret;
                    if (encoded.ValueKind == JsonValueKind.String) secret = Convert.FromBase64String(encoded.GetString());
                    else if (encoded.ValueKind == JsonValueKind.Array && encoded.GetArrayLength() == 32) secret = encoded.EnumerateArray().Select(value => value.GetByte()).ToArray();
                    else return (3, Array.Empty<byte>(), "secret_shape_denied");
                    if (secret.Length != 32) { CryptographicOperations.ZeroMemory(secret); return (3, Array.Empty<byte>(), "secret_width_denied"); }
                    var platform = info.GetProperty("platformType").GetUInt32();
                    var arch = info.GetProperty("clientArch").GetUInt32();
                    var type = info.GetProperty("type").GetUInt32();
                    state.Dispose(); state.ClientSecret = secret; state.Platform = platform; state.Arch = arch; state.Type = type; state.TicketGranted = true;
                }
                result.Add("Param_RealmListTicket", Blob(Encoding.ASCII.GetBytes("AuthRealmListTicket\0")));
                return Success(result, "ticket");
            case "Command_FetchBleepProxiesRequest_v1":
                result.Add("Param_BleepProxyList", JsonBlob("JSONBleepProxyList", new { proxies=Array.Empty<object>() }));
                return Success(result, "bleep_proxies");
            case "Command_SuperDistrictListRequest_v1":
                result.Add("Param_SuperDistrictList", JsonBlob("JSONSuperDistrictList", new { superDistricts=new[] { new {superDistrictID=2, disallowLogin=false, holdDownUntilTime=0} } }));
                return Success(result, "super_districts");
            case "Command_RealmListRequest_v1":
                if (!state.TicketGranted) return (3, Array.Empty<byte>(), "ticket_required");
                if (attributes[commands[0]].Kind != 4 || attributes[commands[0]].Text != SubRegion) return (0x80000069, Array.Empty<byte>(), "unknown_subregion");
                result.Add("Param_RealmList", JsonBlob("JSONRealmListUpdates", new { updates=new[] { new { wowRealmAddress=RealmAddress, update=RealmEntry(), deleting=false } } }));
                result.Add("Param_CharacterCountList", JsonBlob("JSONRealmCharacterCountList", new { counts=new[] { new {wowRealmAddress=RealmAddress, count=0} } }));
                return Success(result, "realm_list");
            case "Command_LastCharPlayedRequest_v1":
                // Like the reference: the realm for any ruleset filter falls back to this one realm, and filter -1 is the
                // request character create waits for. Param_LastPlayedTime must follow Param_RealmEntry.
                result.Add("Param_RealmEntry", JsonBlob("JamJSONRealmEntry", RealmEntry()));
                result.Add("Param_LastPlayedTime", new Value { Kind=2, Signed=DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                result.Add("Param_UtilityInfo", JsonBlob("JSONUtilityInfo", new { realmPermissions=512, entitlements=Array.Empty<int>() }));
                return Success(result, "last_character");
            case "Command_RealmJoinRequest_v1":
                if (!state.TicketGranted) return (3, Array.Empty<byte>(), "ticket_required");
                if (!attributes.TryGetValue("Param_RealmAddress", out var address) || address.Kind != 6 || address.Unsigned != RealmAddress)
                    return (0x80000069, Array.Empty<byte>(), "unknown_realm");
                var serverSecret = RandomNumberGenerator.GetBytes(32);
                if (state.WorldKeyData != null) CryptographicOperations.ZeroMemory(state.WorldKeyData);
                state.WorldKeyData = state.ClientSecret.Concat(serverSecret).ToArray();
                ForeverJoinRegistry.Publish(DemoGameAccount, state.WorldKeyData);
                result.Add("Param_RealmJoinTicket", Blob(JsonSerializer.SerializeToUtf8Bytes(new { gameAccount=DemoGameAccount, platform=state.Platform, type=state.Type, clientArch=state.Arch })));
                result.Add("Param_ServerAddresses", JsonBlob("JSONRealmListServerIPAddresses", new { families=new[] { new {family=1, addresses=new[] {new {ip="127.0.0.1", port=realmPort}}} } }));
                result.Add("Param_JoinSecret", Blob(serverSecret));
                return Success(result, "realm_join");
            default: return (Unimplemented, Array.Empty<byte>(), "unsupported_command");
        }
    }
    private static object RealmEntry() => new { wowRealmAddress=RealmAddress, cfgTimezonesID=1, populationState=1, cfgCategoriesID=1,
        version=new {versionMajor=1,versionMinor=60,versionRevision=1,versionBuild=70338}, cfgRealmsID=70,flags=0,name="Forever Sandbox",
        cfgConfigsID=1,cfgLanguagesID=1,cfgContentSetID=137,superDistrictID=2,useBleepChance=0.0 };
    private static JsonDocument Envelope(byte[] bytes, string prefix) {
        if (bytes == null || bytes.Length > 32768) throw new FormatException("Invalid bounded realm JSON.");
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0');
        if (!text.StartsWith(prefix + ":", StringComparison.Ordinal)) throw new FormatException("Unexpected realm JSON envelope.");
        return JsonDocument.Parse(text[(prefix.Length+1)..], new JsonDocumentOptions {MaxDepth=16});
    }
    internal static Dictionary<string, Value> ReadAttributes(byte[] payload) {
        if (payload == null || payload.Length > MaxRequestBytes) throw new FormatException("Realm request exceeds limit.");
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        var input = new CodedInputStream(payload); uint tag;
        while ((tag=input.ReadTag()) != 0) {
            if (tag != 10) { input.SkipLastField(); continue; }
            if (result.Count >= 64) throw new FormatException("Too many realm attributes.");
            var attribute = new CodedInputStream(input.ReadBytes().ToByteArray()); string name=null; Value value=null; uint field;
            while ((field=attribute.ReadTag()) != 0) {
                if (field == 10) name=attribute.ReadString();
                else if (field == 18) value=ReadVariant(attribute.ReadBytes().ToByteArray());
                else attribute.SkipLastField();
            }
            if (name == null || name.Length > 128 || value == null || !result.TryAdd(name,value)) throw new FormatException("Invalid realm attribute.");
        }
        return result;
    }
    private static Value ReadVariant(byte[] bytes) {
        var input=new CodedInputStream(bytes); var value=new Value(); uint tag;
        while ((tag=input.ReadTag()) != 0) {
            switch(tag) {
                case 8: value=new Value {Kind=1,Unsigned=input.ReadBool()?1UL:0UL}; break;
                case 16: value=new Value {Kind=2,Signed=input.ReadInt64()}; break;
                case 25: input.ReadDouble(); value=new Value {Kind=3}; break;
                case 34: value=new Value {Kind=4,Text=input.ReadString()}; if(value.Text.Length>4096)throw new FormatException("Realm string exceeds limit."); break;
                case 42: value=new Value {Kind=5,Blob=input.ReadBytes().ToByteArray()}; if(value.Blob.Length>65536)throw new FormatException("Realm blob exceeds limit."); break;
                case 48: value=new Value {Kind=6,Unsigned=input.ReadUInt64()}; break;
                default: input.SkipLastField(); break;
            }
        }
        // An optional v2 Variant may legitimately have no selected oneof value.
        // Marker commands (ticket/proxies/rulesets) do not consume that value.
        // Commands needing a string/integer validate its kind before use.
        return value;
    }
    private static (uint,byte[],string) Success(Dictionary<string,Value> values,string route) => (0,Pack(output => {
        foreach(var value in values) Bytes(output,1,Pack(attribute => {Text(attribute,1,value.Key);Bytes(attribute,2,Variant(value.Value));}));
    }),route);
    private static Value Blob(byte[] value) => new() {Kind=5,Blob=value};
    private static Value JsonBlob(string name,object value) {
        var bytes=Encoding.UTF8.GetBytes(name+":"+JsonSerializer.Serialize(value)+"\0");
        using var output=new MemoryStream(); var length=new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length,bytes.Length); output.Write(length);
        using(var zlib=new ZLibStream(output,CompressionLevel.Fastest,true))zlib.Write(bytes);
        return Blob(output.ToArray());
    }
    private static byte[] Variant(Value value) => Pack(output => {
        if(value.Kind==2){output.WriteTag(2,WireFormat.WireType.Varint);output.WriteInt64(value.Signed);}
        else if(value.Kind==4)Text(output,4,value.Text);
        else if(value.Kind==5)Bytes(output,5,value.Blob);
        else if(value.Kind==6){output.WriteTag(6,WireFormat.WireType.Varint);output.WriteUInt64(value.Unsigned);}
        else throw new ArgumentException("Unsupported local response variant.");
    });
    private static byte[] Pack(Action<CodedOutputStream> fill){using var memory=new MemoryStream();using(var output=new CodedOutputStream(memory,true)){fill(output);output.Flush();}return memory.ToArray();}
    private static void Bytes(CodedOutputStream output,int field,byte[] value){output.WriteTag(field,WireFormat.WireType.LengthDelimited);output.WriteBytes(ByteString.CopyFrom(value));}
    private static void Text(CodedOutputStream output,int field,string value){output.WriteTag(field,WireFormat.WireType.LengthDelimited);output.WriteString(value);}
}
