using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AuthServer.Network;
using Google.Protobuf;

internal static class RealmFixtures
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static byte[] Proto(Action<CodedOutputStream> fill){using var memory=new MemoryStream();using(var output=new CodedOutputStream(memory,true)){fill(output);output.Flush();}return memory.ToArray();}
    public static byte[] String(string text)=>Proto(o=>{o.WriteTag(4,WireFormat.WireType.LengthDelimited);o.WriteString(text);});
    public static byte[] Blob(byte[] blob)=>Proto(o=>{o.WriteTag(5,WireFormat.WireType.LengthDelimited);o.WriteBytes(ByteString.CopyFrom(blob));});
    public static byte[] Number(ulong number)=>Proto(o=>{o.WriteTag(6,WireFormat.WireType.Varint);o.WriteUInt64(number);});
    public static byte[] Signed(long number)=>Proto(o=>{o.WriteTag(2,WireFormat.WireType.Varint);o.WriteInt64(number);});
    public static byte[] Task(string command,params (string name,byte[] variant)[] extra)=>Attributes(new[]{(command,Array.Empty<byte>())}.Concat(extra));
    public static byte[] Attributes(IEnumerable<(string name,byte[] variant)> attributes)=>Proto(o=>{
        foreach(var attribute in attributes){var item=Proto(a=>{a.WriteTag(1,WireFormat.WireType.LengthDelimited);a.WriteString(attribute.name);a.WriteTag(2,WireFormat.WireType.LengthDelimited);a.WriteBytes(ByteString.CopyFrom(attribute.variant));});o.WriteTag(1,WireFormat.WireType.LengthDelimited);o.WriteBytes(ByteString.CopyFrom(item));}
    });
    public static byte[] KeyRequest()=>Proto(o=>{o.WriteTag(1,WireFormat.WireType.LengthDelimited);o.WriteString("Command_RealmListRequest_v1_b9b");});
    public static byte[] Ticket(bool base64=false,uint identity=1)=>Task("Command_RealmListTicketRequest_v1_b9b",
        ("Param_Identity",Blob(Encoding.UTF8.GetBytes("JSONRealmListTicketIdentity:"+JsonSerializer.Serialize(new{gameAccountID=identity,gameAccountRegion=2})))),
        ("Param_ClientInfo",Blob(Encoding.UTF8.GetBytes("JSONRealmListTicketClientInformation:"+JsonSerializer.Serialize(new{info=new{
            secret=base64?(object)Convert.ToBase64String(Enumerable.Range(0,32).Select(i=>(byte)i).ToArray()):Enumerable.Range(0,32).ToArray(),
            version=new{versionBuild=70338},platformType=0x57696E,clientArch=0x783634,type=0x576F57}})))));
    public sealed class Result { public int Kind; public byte[] Blob; public string Text; public long Signed; }
    // Fixture decoder uses pinned v2 tags explicitly, independent of the SUT decoder.
    public static Dictionary<string,Result> Read(byte[] payload){
        var output=new Dictionary<string,Result>();var input=new CodedInputStream(payload);uint tag;
        while((tag=input.ReadTag())!=0){
            Check(tag==10,"Expected ProcessTaskResponse.result field 1.");var attribute=new CodedInputStream(input.ReadBytes().ToByteArray());
            Check(attribute.ReadTag()==10,"Attribute.name field changed.");var name=attribute.ReadString();
            Check(attribute.ReadTag()==18,"Attribute.value field changed.");var variant=new CodedInputStream(attribute.ReadBytes().ToByteArray());
            var value=new Result();tag=variant.ReadTag();
            switch(tag){case 16:value.Kind=2;value.Signed=variant.ReadInt64();break;case 34:value.Kind=4;value.Text=variant.ReadString();break;case 42:value.Kind=5;value.Blob=variant.ReadBytes().ToByteArray();break;default:throw new Exception("Unexpected v2 variant tag.");}
            Check(variant.ReadTag()==0 && attribute.ReadTag()==0,"Unexpected trailing realm response fields.");output.Add(name,value);
        }return output;
    }
    public static JsonDocument Json(byte[] blob,string prefix){
        Check(blob.Length>6,"Compressed realm JSON missing.");var expected=BinaryPrimitives.ReadInt32LittleEndian(blob);
        using var input=new MemoryStream(blob,4,blob.Length-4);using var zlib=new ZLibStream(input,CompressionMode.Decompress);using var output=new MemoryStream();zlib.CopyTo(output);
        var data=output.ToArray();Check(data.Length==expected && data[^1]==0,"Compressed JSON length/terminator changed.");
        var text=Encoding.UTF8.GetString(data,0,data.Length-1);Check(text.StartsWith(prefix+":"),"Wrong realm JSON envelope.");return JsonDocument.Parse(text[(prefix.Length+1)..]);
    }
    public static void CheckList(byte[] payload){
        var values=Read(payload);using var realms=Json(values["Param_RealmList"].Blob,"JSONRealmListUpdates");
        var update=realms.RootElement.GetProperty("updates")[0];var entry=update.GetProperty("update");
        Check(!update.GetProperty("deleting").GetBoolean() && entry.GetProperty("wowRealmAddress").GetUInt32()==0x46010046 && entry.GetProperty("cfgRealmsID").GetInt32()==70,"Realm identity/address incorrect.");
        var version=entry.GetProperty("version");Check(version.GetProperty("versionMajor").GetInt32()==1 && version.GetProperty("versionMinor").GetInt32()==60 && version.GetProperty("versionRevision").GetInt32()==1 && version.GetProperty("versionBuild").GetInt32()==70338,"Wrong beta version.");
        Check(entry.GetProperty("cfgContentSetID").GetInt32()==137 && entry.GetProperty("superDistrictID").GetInt32()==2 && entry.GetProperty("flags").GetInt32()==0,"Realm ruleset/flags incorrect.");
        using var counts=Json(values["Param_CharacterCountList"].Blob,"JSONRealmCharacterCountList");Check(counts.RootElement.GetProperty("counts")[0].GetProperty("count").GetInt32()==0,"Unexpected local character count.");
    }
    public static void RunAll(Action<string,Action> run){
        run("realm_v2_command_suffix_is_opaque_and_whitelisted",()=>{
            foreach(var suffix in new[]{"","_b9","_b9b","_beta70338"})Check(BgsRealmV2.Command("Command_RealmListTicketRequest_v1"+suffix)=="Command_RealmListTicketRequest_v1","Opaque command suffix rejected.");
            foreach(var name in new[]{"Command_Unknown_v1_b9b","Command_RealmListTicketRequest_v2_b9b","Prefix_Command_RealmListTicketRequest_v1_b9b"})Check(BgsRealmV2.Command(name)=="","Unknown command/version accepted.");
        });
        run("realm_v2_accepts_empty_marker_variant_and_rejects_empty_required_value",()=>{
            using var state=new BgsRealmState();var ticket=BgsRealmV2.Reply(1,Ticket(),state,true,48002);Check(ticket.status==0,"An empty command marker was rejected.");
            Check(BgsRealmV2.Reply(1,Task("Command_FetchBleepProxiesRequest_v1_b9"),state,true,48002).status==0,"Empty bleep marker rejected.");
            Check(BgsRealmV2.Reply(1,Task("Command_RealmListRequest_v1_b9"),state,true,48002).status==0x80000069,"Empty required subregion value accepted.");
        });
        run("realm_v2_ticket_identity_secret_array_and_base64",()=>{
            foreach(var base64 in new[]{false,true}){using var state=new BgsRealmState();var reply=BgsRealmV2.Reply(1,Ticket(base64),state,true,48002);
                Check(reply.status==0 && state.TicketGranted && state.ClientSecret.SequenceEqual(Enumerable.Range(0,32).Select(i=>(byte)i)),"Client secret or ticket changed.");
                Check(Read(reply.payload)["Param_RealmListTicket"].Blob.SequenceEqual(Encoding.ASCII.GetBytes("AuthRealmListTicket\0")),"Realm ticket framing changed.");}
        });
        run("realm_v2_subregion_string_variant_field_4",()=>{using var state=new BgsRealmState();var reply=BgsRealmV2.Reply(2,KeyRequest(),state,true,48002);var expected=new byte[]{10,9,34,7,55,48,45,49,45,55,48};Check(reply.status==0 && reply.payload.SequenceEqual(expected),"GetAllValues v2 golden wire changed.");});
        run("realm_v2_list_version_ruleset_and_zlib",()=>{using var state=new BgsRealmState();BgsRealmV2.Reply(1,Ticket(),state,true,48002);var reply=BgsRealmV2.Reply(1,Attributes(new[]{("Command_RealmListRequest_v1_b9",String("70-1-70"))}),state,true,48002);Check(reply.status==0,"Realm list denied.");CheckList(reply.payload);});
        run("realm_v2_super_district_and_last_character_order",()=>{
            using var state=new BgsRealmState();var districts=BgsRealmV2.Reply(1,Task("Command_SuperDistrictListRequest_v1_b9"),state,true,48002);using var json=Json(Read(districts.payload)["Param_SuperDistrictList"].Blob,"JSONSuperDistrictList");Check(json.RootElement.GetProperty("superDistricts")[0].GetProperty("superDistrictID").GetInt32()==2,"Wrong normal ruleset.");
            var last=BgsRealmV2.Reply(1,Task("Command_LastCharPlayedRequest_v1_b9",("Param_ContentSetIDFilter",Signed(137))),state,true,48002);var values=Read(last.payload);Check(values.Keys.SequenceEqual(new[]{"Param_RealmEntry","Param_LastPlayedTime","Param_UtilityInfo"}) && Math.Abs(values["Param_LastPlayedTime"].Signed-DateTimeOffset.UtcNow.ToUnixTimeSeconds())<5,"Last-character response ordering/time changed.");
        });
        run("realm_v2_join_returns_only_loopback_and_64_byte_key_data",()=>{using var state=new BgsRealmState();BgsRealmV2.Reply(1,Ticket(),state,true,48002);var reply=BgsRealmV2.Reply(1,Task("Command_RealmJoinRequest_v1_b9",("Param_RealmAddress",Number(0x46010046))),state,true,48002);var values=Read(reply.payload);Check(reply.status==0 && values["Param_JoinSecret"].Blob.Length==32 && state.WorldKeyData.Length==64 && state.WorldKeyData.Take(32).SequenceEqual(state.ClientSecret) && state.WorldKeyData.Skip(32).SequenceEqual(values["Param_JoinSecret"].Blob),"Join secret/key material incorrect.");
            using var addresses=Json(values["Param_ServerAddresses"].Blob,"JSONRealmListServerIPAddresses");var address=addresses.RootElement.GetProperty("families")[0].GetProperty("addresses")[0];Check(address.GetProperty("ip").GetString()=="127.0.0.1" && address.GetProperty("port").GetInt32()==48002,"Join exposed a nonlocal address.");
            using var ticket=JsonDocument.Parse(values["Param_RealmJoinTicket"].Blob);Check(ticket.RootElement.GetProperty("gameAccount").GetString()=="sandbox" && ticket.RootElement.GetProperty("clientArch").GetUInt32()==0x783634,"Modern join ticket changed.");
        });
        run("realm_v2_denies_foreign_identity_missing_ticket_and_unknown_realm",()=>{using var state=new BgsRealmState();Check(BgsRealmV2.Reply(1,Ticket(identity:2),state,true,48002).status==3 && !state.TicketGranted,"Foreign identity accepted.");var join=Task("Command_RealmJoinRequest_v1_b9",("Param_RealmAddress",Number(0x46010046)));Check(BgsRealmV2.Reply(1,join,state,true,48002).status==3 && BgsRealmV2.Reply(1,Ticket(),state,false,48002).status==3,"Missing login/ticket accepted.");BgsRealmV2.Reply(1,Ticket(),state,true,48002);Check(BgsRealmV2.Reply(1,Task("Command_RealmJoinRequest_v1_b9",("Param_RealmAddress",Number(1))),state,true,48002).status==0x80000069,"Unknown realm accepted.");});
        run("realm_v2_bounds_duplicate_attributes_and_unknown_rpc_reply",()=>{using var state=new BgsRealmState();try{BgsRealmV2.Reply(1,new byte[BgsRealmV2.MaxRequestBytes+1],state,true,48002);throw new Exception("Oversized realm request accepted.");}catch(FormatException){}try{BgsRealmV2.Reply(1,Attributes(new[]{("Command_RealmListRequest_v1_b9",String("70-1-70")),("Command_RealmListRequest_v1_b9",String("70-1-70"))}),state,true,48002);throw new Exception("Duplicate attributes accepted.");}catch(FormatException){}Check(BgsRealmV2.Reply(1,Task("Command_Unknown_v1_b9"),state,true,48002).status==0xBC7,"Unknown command did not return RPC not implemented.");});
        run("realm_v2_state_dispose_clears_secret_buffers",()=>{var state=new BgsRealmState();BgsRealmV2.Reply(1,Ticket(),state,true,48002);var secret=state.ClientSecret;state.Dispose();Check(secret.All(b=>b==0) && state.ClientSecret==null && !state.TicketGranted,"Realm state secret was retained after disposal.");});
    }
}
