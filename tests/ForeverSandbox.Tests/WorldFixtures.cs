using System.Buffers.Binary;
using System.Text;
using AuthServer.Network;

internal static class WorldFixtures
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    public static byte[] AuthPayload(){
        var ticket=Encoding.UTF8.GetBytes("{\"gameAccount\":\"DO_NOT_LOG_THIS_WORLD_TICKET\",\"platform\":1,\"type\":2,\"clientArch\":3}");
        var payload=new byte[81+ticket.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8),70);BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12),1);BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16),70);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(77),(uint)ticket.Length);ticket.CopyTo(payload,81);return payload;
    }
    public static void CheckChallenge(ForeverWorldFrame frame){Check(frame!=null && frame.Opcode==0x4E0000 && frame.Payload.Length==65 && frame.Payload[64]==1,"70338 world challenge opcode/shape incorrect.");}
    static byte[] Hex(string text)=>Convert.FromHexString(text);
    static byte[] Range(int start,int count)=>Enumerable.Range(start,count).Select(i=>(byte)i).ToArray();
    public static byte[] SessionAuthPayload(byte[] localChallenge){
        var ticket=Encoding.UTF8.GetBytes("{\"gameAccount\":\"sandbox\",\"platform\":1,\"type\":2,\"clientArch\":3}");
        var payload=new byte[81+ticket.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8),70);BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12),1);BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16),70);
        localChallenge.CopyTo(payload,20);BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(77),(uint)ticket.Length);ticket.CopyTo(payload,81);return payload;
    }
    // Client-side mirror with explicit IVs: send counter uses CLNT, receive SRVR; both count every frame.
    sealed class ClientCrypt:IDisposable{
        System.Security.Cryptography.AesGcm aes;ulong sent,received;
        static byte[] Iv(ulong counter,uint magic){var iv=new byte[12];BinaryPrimitives.WriteUInt64LittleEndian(iv,counter);BinaryPrimitives.WriteUInt32LittleEndian(iv.AsSpan(8),magic);return iv;}
        public void Enable(byte[] key){aes=new System.Security.Cryptography.AesGcm(key,12);}
        public byte[] Seal(uint opcode,byte[] payload){var plain=BitConverter.GetBytes(opcode).Concat(payload).ToArray();var cipher=new byte[plain.Length];var tag=new byte[12];if(aes==null)plain.CopyTo(cipher,0);else aes.Encrypt(Iv(sent,0x544E4C43),plain,cipher,tag);sent++;return BitConverter.GetBytes((uint)plain.Length).Concat(tag).Concat(cipher).ToArray();}
        public ForeverWorldFrame Read(Stream stream){var header=new byte[16];stream.ReadExactly(header);var body=new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];stream.ReadExactly(body);var plain=new byte[body.Length];if(aes==null)body.CopyTo(plain,0);else aes.Decrypt(Iv(received,0x52565253),body,header[4..],plain);received++;return new ForeverWorldFrame(BinaryPrimitives.ReadUInt32LittleEndian(plain),plain[4..]);}
        public void Dispose()=>aes?.Dispose();
    }
    // Independent loopback client for the encrypted session; keyData = client secret 32 + join secret 32.
    // inProcess: the server runs in this process (synthetic fixture), so its character store can be inspected directly.
    public static object WorldSessionClient(int port,byte[] keyData,bool inProcess=false){
        using var tcp=new System.Net.Sockets.TcpClient();
        tcp.Connect(System.Net.IPAddress.Loopback,port);
        using var stream=tcp.GetStream();stream.ReadTimeout=stream.WriteTimeout=10000;using var crypt=new ClientCrypt();
        Check(ForeverWorldWire.ReadInitializer(stream)==ForeverWorldWire.ServerInitializer,"Wrong world initializer.");
        stream.Write(Encoding.ASCII.GetBytes(ForeverWorldWire.ClientInitializer));
        var challenge=crypt.Read(stream);CheckChallenge(challenge);var serverChallenge=challenge.Payload[32..64];
        var local=Range(96,32);stream.Write(crypt.Seal(0x460001,SessionAuthPayload(local)));
        var enter=crypt.Read(stream);Check(enter.Opcode==0x4E0004 && enter.Payload.Length==69 && BinaryPrimitives.ReadInt32LittleEndian(enter.Payload)==8,"EnterEncryptedMode opcode/shape wrong.");
        var session=ForeverWorldKeys.SessionKey(keyData,serverChallenge,local);var encrypt=ForeverWorldKeys.EncryptKey(session,local,serverChallenge);
        Check(Ed25519Ctx.Verify(Hex("02596F0D0C061A8B30745988FD72C59E29EC367FB0F341F28E0F08D037BAFC69"),ForeverWorldKeys.SignedDigest(encrypt,true),enter.Payload[4..68],ForeverWorldKeys.EnableEncryptionContext),"Server did not sign the client-derived encryption key.");
        stream.Write(crypt.Seal(0x460005,Array.Empty<byte>()));crypt.Enable(encrypt);
        var response=crypt.Read(stream);Check(response.Opcode==0x470001 && response.Payload.Take(5).SequenceEqual(new byte[]{0,0,0,0,0x80}),"Encrypted AuthResponse (server counter 2) wrong.");
        Check(crypt.Read(stream).Opcode==0x470124 && crypt.Read(stream).Opcode==0x470064,"Glue follow-up packets missing.");
        var available=crypt.Read(stream);Check(available.Opcode==0x4B0001 && BinaryPrimitives.ReadUInt32LittleEndian(available.Payload)==BgsRealmV2.RealmAddress && BinaryPrimitives.ReadUInt32LittleEndian(available.Payload.AsSpan(4))*8+8==available.Payload.Length,"Available hotfixes missing.");
        var times=crypt.Read(stream);Check(times.Opcode==0x4701B6 && times.Payload.Length==2+8+20*8,"Account data times wrong.");
        var status=crypt.Read(stream);Check(status.Opcode==0x4702B2 && status.Payload.SequenceEqual(new byte[]{0x40}),"Battle.net connection status missing.");
        for(var i=0;i<2;i++){var subscribed=crypt.Read(stream);Check(subscribed.Opcode==0x4702B0 && BinaryPrimitives.ReadUInt32LittleEndian(subscribed.Payload)==0,"Friends/block-list subscribe results missing.");}
        stream.Write(crypt.Seal(0x440014,Array.Empty<byte>()));
        var list=crypt.Read(stream);Check(list.Opcode==0x470018 && list.Payload[0]==0x80,"Encrypted empty character list wrong.");
        Check(crypt.Read(stream).Opcode==0x470363,"Character list release packet missing.");
        // Battle.net over the world socket: character create's realm request (filter -1), a change-realm ticket, and the Ruleset button's list.
        byte[] Relay(uint token,byte[] body)=>BitConverter.GetBytes((0x5DBB51C2UL<<32)|1).Concat(BitConverter.GetBytes(0UL)).Concat(BitConverter.GetBytes(token)).Concat(BitConverter.GetBytes((uint)body.Length)).Concat(body).ToArray();
        stream.Write(crypt.Seal(0x440124,Relay(77,RealmFixtures.Task("Command_LastCharPlayedRequest_v1_b9",("Param_ContentSetIDFilter",RealmFixtures.Signed(-1))))));
        var relayed=crypt.Read(stream);
        Check(relayed.Opcode==0x4702B0 && BinaryPrimitives.ReadUInt32LittleEndian(relayed.Payload)==0 && BinaryPrimitives.ReadUInt64LittleEndian(relayed.Payload.AsSpan(4))==((0x5DBB51C2UL<<32)|1) && BinaryPrimitives.ReadUInt32LittleEndian(relayed.Payload.AsSpan(20))==77,"Relayed realm response status/method/token wrong.");
        Check(RealmFixtures.Read(relayed.Payload[28..]).ContainsKey("Param_RealmEntry"),"Filter -1 realm entry missing.");
        stream.Write(crypt.Seal(0x440129,BitConverter.GetBytes(5u).Concat(Range(0,32)).ToArray()));
        var ticket=crypt.Read(stream);Check(ticket.Opcode==0x4702B3 && BinaryPrimitives.ReadUInt32LittleEndian(ticket.Payload)==5 && ticket.Payload[4]==0x80 && BinaryPrimitives.ReadUInt32LittleEndian(ticket.Payload.AsSpan(5))==27,"Change-realm-ticket response wrong.");
        stream.Write(crypt.Seal(0x440124,Relay(78,RealmFixtures.Attributes(new[]{("Command_RealmListRequest_v1_b9",RealmFixtures.String("70-1-70"))}))));
        var realms=crypt.Read(stream);Check(realms.Opcode==0x4702B0 && BinaryPrimitives.ReadUInt32LittleEndian(realms.Payload)==0,"Relayed realm list failed.");RealmFixtures.CheckList(realms.Payload[28..]);
        // Startup prerequisites: hotfix push request, TactKey DB query, account data load.
        if(BinaryPrimitives.ReadUInt32LittleEndian(available.Payload.AsSpan(4))>0){
            var push=BinaryPrimitives.ReadInt32LittleEndian(available.Payload.AsSpan(8));
            stream.Write(crypt.Seal(0x440011,new uint[]{70338,70338,1}.SelectMany(BitConverter.GetBytes).Concat(BitConverter.GetBytes(push)).ToArray()));
            var hotfix=crypt.Read(stream);Check(hotfix.Opcode==0x4B0003 && BinaryPrimitives.ReadUInt32LittleEndian(hotfix.Payload)>=1 && BinaryPrimitives.ReadInt32LittleEndian(hotfix.Payload.AsSpan(4))==push,"Hotfix connect wrong.");
        }
        stream.Write(crypt.Seal(0x440010,BitConverter.GetBytes(0xDF2F53CFu).Concat(new byte[]{0x00,0x08}).Concat(BitConverter.GetBytes(8341u)).ToArray()));
        var reply=crypt.Read(stream);Check(reply.Opcode==0x4B0000 && BinaryPrimitives.ReadUInt32LittleEndian(reply.Payload.AsSpan(4))==8341,"TactKey DB reply missing.");
        stream.Write(crypt.Seal(0x4400C3,new byte[]{0,0}.Concat(BitConverter.GetBytes(0)).ToArray()));
        var account=crypt.Read(stream);Check(account.Opcode==0x4701B4 && BinaryPrimitives.ReadInt32LittleEndian(account.Payload.AsSpan(14))==0,"Account data load wrong.");
        // Name check, create (hand-assembled 70338 layout), populated list, login decline, delete.
        stream.Write(crypt.Seal(0x440071,BitConverter.GetBytes(9u).Concat(new byte[]{0x18,0x00}).Concat(Encoding.ASCII.GetBytes("Arthas")).ToArray()));
        var check=crypt.Read(stream);Check(check.Opcode==0x47001B && BinaryPrimitives.ReadUInt32LittleEndian(check.Payload)==9 && BinaryPrimitives.ReadUInt32LittleEndian(check.Payload.AsSpan(4))==96,"Name availability result wrong.");
        stream.Write(crypt.Seal(0x440070,CreatePayload("Arthas",1,1,0)));
        var create=crypt.Read(stream);Check(create.Opcode==0x4701AC && BinaryPrimitives.ReadUInt32LittleEndian(create.Payload)==24 && create.Payload.AsSpan(4).SequenceEqual(new byte[]{0x01,0xE0,0x01,0x18,0x01,0x08}),"Character create result/GUID wrong.");
        stream.Write(crypt.Seal(0x440014,Array.Empty<byte>()));
        var populated=crypt.Read(stream);Check(populated.Opcode==0x470018 && populated.Payload[0]==0xC0 && BinaryPrimitives.ReadUInt32LittleEndian(populated.Payload.AsSpan(6))==1,"Populated realmless character list wrong.");
        var restrictions=crypt.Read(stream);Check(restrictions.Opcode==0x470019 && BinaryPrimitives.ReadUInt32LittleEndian(restrictions.Payload)==1,"Character restrictions missing.");
        Check(crypt.Read(stream).Opcode==0x470363,"Populated list release packet missing.");
        // PLAYER_LOGIN (guid, FarClip, RPE bit): ConnectTo with a one-time instance key, tutorial flags on the realm connection.
        stream.Write(crypt.Seal(0x440016,new byte[]{0x01,0xE0,0x01,0x18,0x01,0x08}.Concat(BitConverter.GetBytes(1000f)).Concat(new byte[]{0}).ToArray()));
        var connectTo=crypt.Read(stream);
        Check(connectTo.Opcode==0x4E0008 && connectTo.Payload.Length==45 && BinaryPrimitives.ReadUInt32LittleEndian(connectTo.Payload)==1 && BinaryPrimitives.ReadUInt32LittleEndian(connectTo.Payload.AsSpan(4))==17 && connectTo.Payload[8]==1,"ConnectTo header wrong.");
        var connectKey=BinaryPrimitives.ReadUInt64LittleEndian(connectTo.Payload.AsSpan(9));
        Check((uint)connectKey==1 && (connectKey>>32&1)==1 && BinaryPrimitives.ReadUInt32LittleEndian(connectTo.Payload.AsSpan(17))==BgsRealmV2.RealmAddress,"ConnectToKey account/instance type wrong.");
        Check(connectTo.Payload.AsSpan(25,5).SequenceEqual(new byte[]{1,127,0,0,1}) && connectTo.Payload.AsSpan(32,13).SequenceEqual(new byte[]{0,0,0,8,0,0,0,0,0,0,0,0,0}),"ConnectTo IPv4 payload or empty bleep token wrong.");
        var world=InstanceClient(BinaryPrimitives.ReadUInt16LittleEndian(connectTo.Payload.AsSpan(30)),session,connectKey,readInstance=>{
            // Realm-connection login packets, sent while the instance socket logs in (reference order around LOGIN_VERIFY_WORLD).
            Check(crypt.Read(stream) is {Opcode:0x470269,Payload.Length:32},"Tutorial flags missing.");
            var characterTimes=crypt.Read(stream);Check(characterTimes.Opcode==0x4701B6 && characterTimes.Payload.AsSpan(0,6).SequenceEqual(new byte[]{0x01,0xE0,0x01,0x18,0x01,0x08}) && characterTimes.Payload.Length==6+8+20*8,"Character account data times missing.");
            var features=crypt.Read(stream);
            Check(features.Opcode==0x470063 && features.Payload.Length==818 && BinaryPrimitives.ReadInt32LittleEndian(features.Payload.AsSpan(147))==137 && BinaryPrimitives.ReadUInt32LittleEndian(features.Payload.AsSpan(155))==45,"In-game feature status (content set, game rules) wrong.");
            Check(crypt.Read(stream).Opcode==0x470124,"In-game time zone missing.");
            Check(crypt.Read(stream) is {Opcode:0x4C0001} hint && Encoding.UTF8.GetString(hint.Payload,44,hint.Payload.Length-44).Contains(".fly"),"Flight hint system message missing.");
            // Chat commands typed in /say (realm connection) act on the mover (instance connection) and answer as system messages.
            stream.Write(crypt.Seal(0x2D0023,SayPayload(".speed 2")));
            Check(crypt.Read(stream) is {Opcode:0x4C0001} reply && Encoding.UTF8.GetString(reply.Payload,44,reply.Payload.Length-44).Contains("x2"),".speed reply missing.");
            CheckSpeeds(readInstance,7,2f,3f);
            stream.Write(crypt.Seal(0x2D0023,SayPayload(".fly off")));
            Check(crypt.Read(stream).Opcode==0x4C0001,".fly reply missing.");
            Check(readInstance() is {Opcode:0x600036} unset && unset.Payload.AsSpan(6).SequenceEqual(BitConverter.GetBytes(13u)),"UnsetCanFly (sequence 13) missing.");
        });
        Check(crypt.Read(stream) is {Opcode:0x4C0001} flyspeed && Encoding.UTF8.GetString(flyspeed.Payload,44,flyspeed.Payload.Length-44).Contains("x4"),".flyspeed reply (instance connection) missing.");
        var echo=crypt.Read(stream);
        string Reply(){var r=crypt.Read(stream);Check(r.Opcode==0x4C0001,$"System message expected, got 0x{r.Opcode:X}.");return Encoding.UTF8.GetString(r.Payload,44,r.Payload.Length-44);}
        Check(echo.Opcode==0x4C0001 && echo.Payload[0]==1 && BinaryPrimitives.ReadUInt32LittleEndian(echo.Payload.AsSpan(1))==7 && Encoding.UTF8.GetString(echo.Payload.AsSpan(echo.Payload.Length-11))=="Arthashello","/say echo missing.");
        Check(Reply().Contains("teleported to"),"Same-map teleport reply missing.");
        Check(crypt.Read(stream) is {Opcode:0x470071} pending && pending.Payload.Length==17 && BinaryPrimitives.ReadInt32LittleEndian(pending.Payload)==1 && BitConverter.ToSingle(pending.Payload,4)==-8000.5f,"TransferPending wrong.");
        Check(Reply().Contains("Orgrimmar"),"Far teleport reply missing.");
        Check(crypt.Read(stream) is {Opcode:0x47002B} newWorld && newWorld.Payload.Length==56 && BinaryPrimitives.ReadInt32LittleEndian(newWorld.Payload)==1 && BitConverter.ToSingle(newWorld.Payload,4)==1629.36f
            && BinaryPrimitives.ReadInt32LittleEndian(newWorld.Payload.AsSpan(20))==-1 && BinaryPrimitives.ReadUInt32LittleEndian(newWorld.Payload.AsSpan(28))==16 && BinaryPrimitives.ReadInt32LittleEndian(newWorld.Payload.AsSpan(44))==1,"NewWorld wrong.");
        Check(Reply().Contains("blink 20"),".blink reply missing.");
        Check(Reply().Contains("slow fall on"),".slowfall reply missing.");
        Check(Reply().Contains("saved"),".loc reply missing.");
        Check(crypt.Read(stream) is {Opcode:0x470131,Payload.Length:0},"LogoutComplete missing.");
        var saved=inProcess?ForeverCharacterStore.Current.List().Single(c=>c.Name=="Arthas"):null;
        Check(!inProcess || saved.Map==1 && saved.Zone==1637 && saved.ActionButtons.Count==7 && saved.SlowFall && ForeverLocations.Current.Find("home") is {Map:1},"Map, zone, action bar or saved location not kept.");
        Check(!inProcess || saved.X==-8900.5f && saved.Y==-120.25f && saved.Z==90f && saved.O==1.5f && !saved.CanFly && saved.SpeedMultiplier==2f && saved.FlightMultiplier==2f,"Position or flight preferences not kept.");
        // Relog: logout closed the instance connection (reference LogoutPlayer), so a second PLAYER_LOGIN gets a new CONNECT_TO and a
        // fresh instance connection at the saved place. This time the logout comes on the realm connection; it closes the instance too.
        stream.Write(crypt.Seal(0x440016,new byte[]{0x01,0xE0,0x01,0x18,0x01,0x08}.Concat(BitConverter.GetBytes(1000f)).Concat(new byte[]{0}).ToArray()));
        var relogTo=crypt.Read(stream);Check(relogTo.Opcode==0x4E0008 && relogTo.Payload.Length==45,"Relog ConnectTo missing.");
        InstanceRelog(BinaryPrimitives.ReadUInt16LittleEndian(relogTo.Payload.AsSpan(30)),session,BinaryPrimitives.ReadUInt64LittleEndian(relogTo.Payload.AsSpan(9)),()=>{
            foreach(var opcode in new uint[]{0x470269,0x4701B6,0x470063,0x470124})Check(crypt.Read(stream).Opcode==opcode,$"Relog realm packet 0x{opcode:X} missing.");
            stream.Write(crypt.Seal(0x3F0074,new byte[]{0}));
        });
        Check(crypt.Read(stream) is {Opcode:0x470131,Payload.Length:0},"Relog LogoutComplete missing.");
        stream.Write(crypt.Seal(0x4400CB,new byte[]{0x01,0xE0,0x01,0x18,0x01,0x08}));
        var delete=crypt.Read(stream);Check(delete.Opcode==0x4701AD && BinaryPrimitives.ReadUInt32LittleEndian(delete.Payload)==67,"Character delete failed.");
        stream.Write(crypt.Seal(0x460006,BitConverter.GetBytes(7u).Concat(BitConverter.GetBytes(20u)).ToArray()));
        var pong=crypt.Read(stream);Check(pong.Opcode==0x4E0009 && BinaryPrimitives.ReadUInt32LittleEndian(pong.Payload)==7,"Encrypted ping/pong wrong.");
        return new{passed=true,enterEncryptedModeSignatureVerified=true,regionGroup=8,encryptedAuthResponse=true,glueFollowUps=2,emptyCharacterList=true,raceUnlocks=10,battlenetRelay=true,realmEntryForCharacterCreate=true,relayedRealmList=true,nameCheck=true,characterCreated=true,populatedList=true,connectTo=true,worldEntry=world,positionSaved=true,logout=true,characterDeleted=true,encryptedPingPong=true};
    }
    // Instance socket: AuthContinuedSession proof from the realm session key, a second signed EnterEncryptedMode, then the login packets.
    static void CheckActionBar(ForeverWorldFrame bar){
        Check(bar.Opcode==0x470083 && bar.Payload.Length==360*8+1 && bar.Payload[^1]==0,"Action buttons packet wrong.");
        // Character macros as the 70338 client sets them: type 0x41 (ACTION_BUTTON_CMACRO), action = macro index 121+.
        for(var i=0;i<7;i++)Check(BinaryPrimitives.ReadUInt64LittleEndian(bar.Payload.AsSpan(i*8))==(0x41UL<<56|(ulong)(121+i)),$"Sandbox macro {i} not on the action bar.");
        Check(BinaryPrimitives.ReadUInt64LittleEndian(bar.Payload.AsSpan(7*8))==0,"Unexpected action button.");
    }
    // CMSG_CHAT_MESSAGE_SAY: int32 language, 11-bit length + secure bit, text.
    static byte[] SayPayload(string text){var t=Encoding.UTF8.GetBytes(text);return new byte[4].Concat(new[]{(byte)(t.Length>>3),(byte)((t.Length&7)<<5)}).Concat(t).ToArray();}
    // Run, run back, swim, swim back, flight, flight back speeds for one change, sequence numbers from firstSequence.
    static void CheckSpeeds(Func<ForeverWorldFrame> read,uint firstSequence,float multiplier,float flight){
        var expected=new[]{(0x600020u,7f*multiplier),(0x600021u,4.5f*multiplier),(0x600022u,4.722222f*multiplier),(0x600023u,2.5f*multiplier),(0x600024u,7f*multiplier*flight),(0x600025u,4.5f*multiplier*flight)};
        for(var i=0;i<expected.Length;i++){var f=read();Check(f.Opcode==expected[i].Item1 && f.Payload.AsSpan(0,6).SequenceEqual(new byte[]{0x01,0xE0,0x01,0x18,0x01,0x08}) && BinaryPrimitives.ReadUInt32LittleEndian(f.Payload.AsSpan(6))==firstSequence+i && BitConverter.ToSingle(f.Payload,10)==expected[i].Item2,$"Speed change {i} wrong.");}
    }
    static object InstanceClient(int port,byte[] session,ulong key,Action<Func<ForeverWorldFrame>> inWorld){
        using var tcp=new System.Net.Sockets.TcpClient();
        tcp.Connect(System.Net.IPAddress.Loopback,port);
        using var stream=tcp.GetStream();stream.ReadTimeout=stream.WriteTimeout=10000;using var crypt=new ClientCrypt();
        Check(ForeverWorldWire.ReadInitializer(stream)==ForeverWorldWire.ServerInitializer,"Wrong instance initializer.");
        stream.Write(Encoding.ASCII.GetBytes(ForeverWorldWire.ClientInitializer));
        var challenge=crypt.Read(stream);CheckChallenge(challenge);var serverChallenge=challenge.Payload[32..64];
        var local=Range(160,32);var digest=ForeverWorldKeys.ContinuedSessionDigest(session,key,local,serverChallenge);
        stream.Write(crypt.Seal(0x460003,new byte[8].Concat(local).Concat(digest).Concat(BitConverter.GetBytes(key)).Concat(BitConverter.GetBytes(BgsRealmV2.RealmAddress)).Concat(new byte[4]).ToArray()));
        var enter=crypt.Read(stream);var encrypt=ForeverWorldKeys.EncryptKey(session,local,serverChallenge);
        Check(enter.Opcode==0x4E0004 && Ed25519Ctx.Verify(Ed25519Ctx.PublicKey(ForeverWorldKeys.InteropSigningSeed),ForeverWorldKeys.SignedDigest(encrypt,true),enter.Payload[4..68],ForeverWorldKeys.EnableEncryptionContext),"Instance EnterEncryptedMode not signed for the continued session.");
        stream.Write(crypt.Seal(0x460005,Array.Empty<byte>()));crypt.Enable(encrypt);
        Check(crypt.Read(stream) is {Opcode:0x4E0006,Payload.Length:0},"ResumeComms missing.");
        var verify=crypt.Read(stream);Check(verify.Opcode==0x47002F && verify.Payload.Length==24 && BinaryPrimitives.ReadInt32LittleEndian(verify.Payload)==0,"LoginVerifyWorld wrong.");
        Check(crypt.Read(stream) is {Opcode:0x600000,Payload.Length:4},"TimeSyncRequest missing.");
        // Known spells: the Human's language (Common, spell 668), so the client can /say at all.
        Check(crypt.Read(stream) is {Opcode:0x690019} spells && spells.Payload.SequenceEqual(new byte[]{0x80,1,0,0,0,0,0,0,0,0x9C,2,0,0}),"Known spells (language) missing.");
        CheckActionBar(crypt.Read(stream));
        var speed=crypt.Read(stream);Check(speed.Opcode==0x4701B9 && speed.Payload.Length==20 && BitConverter.ToSingle(speed.Payload,8)==0.01666667f,"LoginSetTimeSpeed wrong.");
        var update=crypt.Read(stream);var u=update.Payload;
        Check(update.Opcode==0x5E0000 && BinaryPrimitives.ReadUInt16LittleEndian(u)==0 && BinaryPrimitives.ReadUInt32LittleEndian(u.AsSpan(2))==1 && u[6]==0x80 && BinaryPrimitives.ReadUInt32LittleEndian(u.AsSpan(7))==u.Length-11,"UpdateObject header wrong.");
        Check(u[11]==2 && u.AsSpan(12,6).SequenceEqual(new byte[]{0x01,0xE0,0x01,0x18,0x01,0x08}) && u[18]==7,"Active player create block header wrong.");
        // Create bits: entity position, this-is-you, movement update | ... | active player; then no pause times and the mover GUID.
        Check(u.AsSpan(19,13).SequenceEqual(new byte[]{0x8C,0x00,0x80,0,0,0,0,0x01,0xE0,0x01,0x18,0x01,0x08}),"Movement create bits or mover GUID wrong.");
        stream.Write(crypt.Seal(0x440024,BitConverter.GetBytes(0).Concat(new byte[]{0}).ToArray()));
        // MOVE_INIT_ACTIVE_MOVER_COMPLETE: the server turns flying on (sequence 0) and sets the default speeds (flight x3).
        stream.Write(crypt.Seal(0x420064,BitConverter.GetBytes(1234u)));
        Check(crypt.Read(stream) is {Opcode:0x600035} fly && fly.Payload.SequenceEqual(new byte[]{0x01,0xE0,0x01,0x18,0x01,0x08,0,0,0,0}),"SetCanFly missing or wrong.");
        CheckSpeeds(()=>crypt.Read(stream),1,1f,3f);
        inWorld(()=>crypt.Read(stream));
        // In the world the client sends chat on this connection: commands work here too (sequence 14-19 after .speed 2 and .fly off).
        stream.Write(crypt.Seal(0x2D0023,SayPayload(".flyspeed 2")));
        CheckSpeeds(()=>crypt.Read(stream),14,2f,2f);
        var hello=SayPayload("hello");hello[0]=7;   // language 7 (Common)
        stream.Write(crypt.Seal(0x2D0023,hello));
        // Same map: SMSG_MOVE_TELEPORT (sequence 20): mover GUID, sequence, x y z, facing, 3 bits.
        stream.Write(crypt.Seal(0x2D0023,SayPayload(".tele -8000.5 -100 90")));
        Check(crypt.Read(stream) is {Opcode:0x600034} near && near.Payload.Length==27 && BinaryPrimitives.ReadUInt32LittleEndian(near.Payload.AsSpan(6))==20 && BitConverter.ToSingle(near.Payload,10)==-8000.5f && near.Payload[26]==0,"Same-map MoveTeleport wrong.");
        // Another map: suspend token (sequence 21, reason 1) here, transfer pending / new world on the realm connection.
        stream.Write(crypt.Seal(0x2D0023,SayPayload(".tele Orgrimmar")));
        Check(crypt.Read(stream) is {Opcode:0x470041} suspend && suspend.Payload.SequenceEqual(new byte[]{21,0,0,0,0x40}),"SuspendToken wrong.");
        stream.Write(crypt.Seal(0x460008,BitConverter.GetBytes(21u)));
        stream.Write(crypt.Seal(0x440025,Array.Empty<byte>()));
        Check(crypt.Read(stream) is {Opcode:0x470042} resume && resume.Payload.SequenceEqual(new byte[]{21,0,0,0,0x40}),"ResumeToken wrong.");
        Check(crypt.Read(stream) is {Opcode:0x600000} sync && BinaryPrimitives.ReadUInt32LittleEndian(sync.Payload)==1,"Teleport time sync missing.");
        Check(crypt.Read(stream).Opcode==0x690019,"Teleport known spells missing.");
        CheckActionBar(crypt.Read(stream));
        Check(crypt.Read(stream).Opcode==0x4701B9,"Teleport time speed missing.");
        Check(crypt.Read(stream) is {Opcode:0x5E0000} arrived && BinaryPrimitives.ReadUInt16LittleEndian(arrived.Payload)==1 && arrived.Payload[11]==2,"Create block on the new map (1) missing.");
        stream.Write(crypt.Seal(0x420064,BitConverter.GetBytes(99u)));
        CheckSpeeds(()=>crypt.Read(stream),21,2f,2f);   // flying stays off (.fly off earlier): speeds only
        // .blink: on the ground 20 yards along the facing, 3 yards up (no terrain data); .slowfall: feather fall flag.
        stream.Write(crypt.Seal(0x2D0023,SayPayload(".blink")));
        float o=3.54839f,bx=1629.36f+20f*MathF.Cos(o),by=-4373.39f+20f*MathF.Sin(o),bz=31.2564f+3f;
        Check(crypt.Read(stream) is {Opcode:0x600034} blink && BinaryPrimitives.ReadUInt32LittleEndian(blink.Payload.AsSpan(6))==27 && BitConverter.ToSingle(blink.Payload,10)==bx && BitConverter.ToSingle(blink.Payload,14)==by && BitConverter.ToSingle(blink.Payload,18)==bz,"Blink teleport wrong.");
        stream.Write(crypt.Seal(0x2D0023,SayPayload(".slowfall")));
        Check(crypt.Read(stream) is {Opcode:0x60002F} feather && feather.Payload.SequenceEqual(new byte[]{0x01,0xE0,0x01,0x18,0x01,0x08,28,0,0,0}),"Feather fall flag wrong.");
        stream.Write(crypt.Seal(0x2D0023,SayPayload(".loc home")));
        // CMSG_MOVE_HEARTBEAT: MovementInfo (mover GUID, flags, time, x, y, z, facing, ...): the server keeps the position.
        stream.Write(crypt.Seal(0x42002E,new byte[]{0x01,0xE0,0x01,0x18,0x01,0x08}.Concat(new byte[12]).Concat(new[]{-8900.5f,-120.25f,90f,1.5f}.SelectMany(BitConverter.GetBytes)).Concat(new byte[22]).ToArray()));
        stream.Write(crypt.Seal(0x460006,BitConverter.GetBytes(8u).Concat(BitConverter.GetBytes(20u)).ToArray()));
        Check(crypt.Read(stream) is {Opcode:0x4E0009},"Instance ping/pong wrong.");
        // CMSG_LOGOUT_REQUEST (1 byte): instant logout, LOGOUT_RESPONSE here (result 0, instant), LOGOUT_COMPLETE on the realm connection.
        stream.Write(crypt.Seal(0x3F0074,new byte[]{0}));
        Check(crypt.Read(stream) is {Opcode:0x470130} logout && logout.Payload.SequenceEqual(new byte[]{0,0,0,0,0x80}),"LogoutResponse wrong.");
        Check(Closed(stream),"Instance connection still open after logout.");
        return new{continuedSession=true,resumeComms=true,loginVerifyWorld=true,timeSync=true,timeSpeed=true,updateObjectBytes=u.Length,flightEnabled=true,chatCommands=true};
    }
    // A second login on the same realm session: handshake and login packets on a new instance connection (saved map 1), then
    // realm-side checks and a logout sent by the caller; the instance connection gets LOGOUT_RESPONSE and is closed.
    static void InstanceRelog(int port,byte[] session,ulong key,Action realm){
        using var tcp=new System.Net.Sockets.TcpClient();
        tcp.Connect(System.Net.IPAddress.Loopback,port);
        using var stream=tcp.GetStream();stream.ReadTimeout=stream.WriteTimeout=10000;using var crypt=new ClientCrypt();
        Check(ForeverWorldWire.ReadInitializer(stream)==ForeverWorldWire.ServerInitializer,"Wrong relog instance initializer.");
        stream.Write(Encoding.ASCII.GetBytes(ForeverWorldWire.ClientInitializer));
        var challenge=crypt.Read(stream);var serverChallenge=challenge.Payload[32..64];
        var local=Range(200,32);var digest=ForeverWorldKeys.ContinuedSessionDigest(session,key,local,serverChallenge);
        stream.Write(crypt.Seal(0x460003,new byte[8].Concat(local).Concat(digest).Concat(BitConverter.GetBytes(key)).Concat(BitConverter.GetBytes(BgsRealmV2.RealmAddress)).Concat(new byte[4]).ToArray()));
        Check(crypt.Read(stream).Opcode==0x4E0004,"Relog EnterEncryptedMode missing.");
        stream.Write(crypt.Seal(0x460005,Array.Empty<byte>()));crypt.Enable(ForeverWorldKeys.EncryptKey(session,local,serverChallenge));
        Check(crypt.Read(stream) is {Opcode:0x4E0006},"Relog ResumeComms missing.");
        Check(crypt.Read(stream) is {Opcode:0x47002F} verify && BinaryPrimitives.ReadInt32LittleEndian(verify.Payload)==1,"Relog LoginVerifyWorld (saved map 1) wrong.");
        foreach(var opcode in new uint[]{0x600000,0x690019,0x470083,0x4701B9,0x5E0000})Check(crypt.Read(stream).Opcode==opcode,$"Relog login packet 0x{opcode:X} missing.");
        realm();
        Check(crypt.Read(stream) is {Opcode:0x470130},"Relog LogoutResponse missing.");
        Check(Closed(stream),"Relog instance connection still open after logout.");
    }
    // The server closed the connection: end of stream (or a reset).
    static bool Closed(Stream stream){try{return stream.Read(new byte[1])==0;}catch(IOException){return true;}}
    // CMSG_CREATE_CHARACTER: 6-bit name, 4 flags, 2 unknown, 6-bit surname; race, class, sex; count; -1; season; name; choices.
    public static byte[] CreatePayload(string name,byte race,byte playerClass,byte sex){
        var length=(byte)name.Length;
        var header=new byte[]{(byte)(length<<2),0,0,race,playerClass,sex,2,0,0,0,0xFF,0xFF,0xFF,0xFF,0,0,0,0};
        return header.Concat(Encoding.ASCII.GetBytes(name)).Concat(new uint[]{20,3,10,1}.SelectMany(BitConverter.GetBytes)).ToArray();
    }
    public static void RunAll(Action<string,Action> run){
        run("world_hotfix_and_account_data_layouts",()=>{
            var store=new ForeverHotfixStore(new[]{new ForeverHotfixStore.Record(5,6,0xDF2F53CF,8341,1,Range(1,16))});
            Check(store.AvailableHotfixes(BgsRealmV2.RealmAddress).AsSpan(4).SequenceEqual(new byte[]{1,0,0,0,5,0,0,0,6,0,0,0}),"AvailableHotfixes layout changed.");
            var connect=store.HotfixConnect(new[]{5,5,99});
            Check(connect.Length==4+21+4+16 && connect[24]==0x20 && BinaryPrimitives.ReadUInt32LittleEndian(connect.AsSpan(20))==16 && connect.AsSpan(29).SequenceEqual(Range(1,16)),"HotfixConnect layout changed.");
            var reply=store.DbReply(0xDF2F53CF,8341,7);Check(reply.Length==33 && reply[12]==0x20 && reply.AsSpan(17).SequenceEqual(Range(1,16)),"DBReply layout changed.");
            Check(store.DbReply(0xDF2F53CF,1,7).Length==17 && store.DbReply(0xDF2F53CF,1,7)[12]==0x60,"Unknown DB record not marked invalid.");
            var (table,ids)=ForeverHotfixStore.ReadDbQueryBulk(BitConverter.GetBytes(0xDF2F53CFu).Concat(new byte[]{0x00,0x10}).Concat(BitConverter.GetBytes(1u)).Concat(BitConverter.GetBytes(2u)).ToArray());
            Check(table==0xDF2F53CF && ids.SequenceEqual(new uint[]{1,2}),"DBQueryBulk 13-bit count misparsed.");
            // Real data built by tools/extract_reference_hotfixes.py from the pinned reference.
            var real=ForeverHotfixStore.Load("data/hotfixes.json");
            Check(real.Count==111 && Convert.ToHexString(real.DbReply(0xDF2F53CF,8341,0).AsSpan(17))=="88C375C40F07C2F861774C16E66F6B6D","Reference TactKey data changed.");
            var account=new ForeverAccountData(null);var payload=Encoding.UTF8.GetBytes("SET x \"1\"");
            using var zip=new MemoryStream();using(var z=new System.IO.Compression.ZLibStream(zip,System.IO.Compression.CompressionLevel.Fastest,true))z.Write(payload);
            var update=BitConverter.GetBytes(1234L).Concat(BitConverter.GetBytes((uint)payload.Length)).Concat(new byte[]{0,0}).Concat(BitConverter.GetBytes(4)).Concat(BitConverter.GetBytes((uint)zip.Length)).Concat(zip.ToArray()).ToArray();
            Check(account.Update(update).SequenceEqual(new byte[]{0,0,4,0,0,0,0,0,0,0}),"Account data update completion changed.");
            Check(BinaryPrimitives.ReadInt64LittleEndian(account.AccountDataTimes(99).AsSpan(2+8+4*8))==1234,"Account data time not recorded.");
            var loaded=account.Request(new byte[]{0,0,4,0,0,0});Check(BinaryPrimitives.ReadUInt32LittleEndian(loaded.AsSpan(8))==payload.Length && BinaryPrimitives.ReadInt32LittleEndian(loaded.AsSpan(14))==4,"Account data load changed.");
        });
        run("world_relay_content_handle_declined_and_friends_lists",()=>{
            // ContentHandleRequest BN / apft / enUS (15 bytes, what the real client sent to 0xECBE75BA method 1): declined so the
            // client never fetches from Blizzard's depot; it only disables profanity filtering (client Aurora.log).
            var request=Hex("0D4E420000"+"1574667061"+"1D53556E65");
            var (status,payload,route)=ForeverBattlenetRelay.Dispatch(new ForeverBattlenetRelay.Request((0xECBE75BAUL<<32)|1,1,9,request),new BgsRealmState(),8085);
            Check(status==0xBC7 && payload.Length==0 && route=="content_handle_declined","Content handle was answered.");
            foreach(var method in new ulong[]{3,4,5})Check(ForeverBattlenetRelay.Dispatch(new ForeverBattlenetRelay.Request((0x5869BE8CUL<<32)|method,1,9,new byte[2]),new BgsRealmState(),8085).status==0,"Friends list request not answered.");
        });
        run("world_character_packets_parse_captured_client_bytes",()=>{
            // Captured client bytes quoted in the pinned reference (CharacterPackets.cpp CheckCharacterNameAvailability / CreateCharacter).
            var (sequence,name)=ForeverCharacterPackets.ReadCheckName(new byte[]{5,0,0,0,0x10,0x04}.Concat(Hex("67656172")).Concat(Hex("6664")).ToArray());
            Check(sequence==5 && name=="gear","Name-check bit layout misparsed.");
            var create=Hex("0800805F0B0112000000FFFFFFFF00000000").Concat(Encoding.ASCII.GetBytes("DfDf")).Concat(Enumerable.Range(0,36).SelectMany(i=>BitConverter.GetBytes((uint)(36-i)))).ToArray();
            var character=ForeverCharacterPackets.ReadCreate(create);
            Check(character.Name=="Df" && character.Surname=="Df" && character.Race==0x5F && character.Class==11 && character.Sex==1 && character.Customizations.Length==36,"Create-character bit layout misparsed.");
            Check(character.Customizations.Where((_,i)=>i%2==0).SequenceEqual(character.Customizations.Where((_,i)=>i%2==0).OrderBy(v=>v)),"Customizations not sorted by option.");
            // The captured client create is a race-95 Druid: a valid Forever combo (reference class_expansion_requirement 11/95).
            Check(new ForeverCharacterStore(null).Create(character)==24,"Captured Forever race 95 Druid was rejected.");
            Check(ForeverCharacterPackets.CreateChar(24,ForeverCharacterPackets.PlayerGuid(1)).AsSpan(4).SequenceEqual(new byte[]{0x01,0xE0,0x01,0x18,0x01,0x08}),"Packed player GUID (realm 70) changed.");
            var basic=new ForeverPacketWriter().Basic(new ForeverCharacter{Counter=1,Name="Arthas",Race=1,Class=1,Customizations=new uint[]{20,3}},0).ToArray();
            Check(basic.Length==6+4+2+3+2+4+1+8+12+8+2+17+12+19*23+4+16+4+20+24+8+2+6,"CharacterInfoBasic length changed.");
        });
        run("world_character_store_persists_validates_and_deletes",()=>{
            var file=Path.Combine(Path.GetTempPath(),"forever-characters-"+Guid.NewGuid().ToString("N")+".json");
            try{
                var store=new ForeverCharacterStore(file);
                Check(store.Create(ForeverCharacterPackets.ReadCreate(CreatePayload("Jaina",1,8,1)))==24 && store.Create(ForeverCharacterPackets.ReadCreate(CreatePayload("jaina",1,1,0)))==27,"Duplicate names not rejected.");
                Check(store.Create(ForeverCharacterPackets.ReadCreate(CreatePayload("Thrall",2,2,0)))==26 && store.Create(ForeverCharacterPackets.ReadCreate(CreatePayload("X",1,1,0)))==97,"Invalid race/class or name accepted.");
                var reloaded=new ForeverCharacterStore(file).List();
                Check(reloaded.Length==1 && reloaded[0].Name=="Jaina" && reloaded[0].Map==0 && reloaded[0].Zone==12 && reloaded[0].Customizations.SequenceEqual(new uint[]{10,1,20,3}),"Character store did not persist.");
                Check(new ForeverCharacterStore(file).Delete(reloaded[0].Counter) && new ForeverCharacterStore(file).List().Length==0,"Character delete did not persist.");
            }finally{File.Delete(file);}
        });
        run("world_entry_continued_session_connect_to_and_create_block",()=>{
            // Golden digest: Python hmac/hashlib over the same session key as the key-derivation fixture.
            var session=Hex("1E17BF4C22887362B5AF971B4D98635B87510FDF1923BE3A3DF6BCC77798096AB9C7CDDB00C5056D");
            var key=(0x1234UL<<33)|(1UL<<32)|1;
            Check(Convert.ToHexString(ForeverWorldKeys.ContinuedSessionDigest(session,key,Range(96,32),Range(64,32)))=="F6E4085E254E1969D6922ECE7847F30985312FDBA26A8806","Continued-session digest changed.");
            var issued=ForeverInstanceRegistry.Issue(session,new ForeverCharacter{Counter=3});
            Check((uint)issued==1 && (issued>>32&1)==1 && ForeverInstanceRegistry.Take(issued)?.Character.Counter==3 && ForeverInstanceRegistry.Take(issued)==null,"Instance connect key is not one-time or lost its account/type fields.");
            // Game rules: 44 official integer rules (251-253 new in 70338) plus rule 200 = 0.5f, 12 bytes each, before the flag bits.
            var status=ForeverLoginPackets.FeatureSystemStatus();
            Check(status.Length==818 && BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(155))==45 && BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(237+43*12))==253 && BitConverter.ToSingle(status,237+44*12+8)==0.5f,"Feature status game-rule layout changed.");
            Check(status[777]==0xE1 && status.AsSpan(785,33).SequenceEqual(new byte[]{0x70,10,0,0,0,0x60,0xEA,0,0,0,0,0,0,0x71,0xFF,1,0,1,0,0,0,0x60,0xEA,0,0,0,0,0,0,0x71,0xFF,1,0}),"Feature status flags or ticket throttle changed.");
            Check(ForeverLoginPackets.ConnectTo(key,3724).Length==45 && ForeverLoginPackets.WowTime(new DateTime(2026,10,13,21,5,0))==0x1A931545,"ConnectTo size or packed WowTime changed.");
            var (local,digest,parsed)=ForeverLoginPackets.ReadContinuedSession(new byte[8].Concat(Range(1,32)).Concat(Range(50,24)).Concat(BitConverter.GetBytes(key)).Concat(new byte[8]).ToArray());
            Check(local.SequenceEqual(Range(1,32)) && digest.SequenceEqual(Range(50,24)) && parsed==key,"AuthContinuedSession misparsed.");
            // Real 70338 client evidence (crash reports, JamScopedSize): it rejected these values blocks as too short ("expected 20239,
            // received 20240" for the Human Warrior, 20309/20310 for the race-96 Druid). The gap was PerksVendorItem's int64 AvailableUntil
            // (4 bytes); the client's position ends one byte past the end when it runs out of data. Movement block: 177 bytes (6-byte GUID).
            foreach(var (name,surname,race,playerClass,choices,rejected) in new[]{("Abcde","Ab",(byte)1,(byte)1,20,20239u),("Abcdefghijk","Ab",(byte)96,(byte)11,36,20309u)}){
                var shaped=ForeverPlayerObject.CreateSelf(new ForeverCharacter{Counter=3,Name=name,Surname=surname,Race=race,Class=playerClass,Sex=1,Customizations=Enumerable.Range(0,choices).Select(i=>(uint)i).ToArray()});
                var size=BinaryPrimitives.ReadUInt32LittleEndian(shaped.AsSpan(11+8+177));
                // +4: PerksVendorItem's int64; +4: the WoW and Battle.net account GUIDs now set (packed: 2 bytes each beyond empty).
                Check(size==rejected+4+4 && shaped.Length==11+8+177+4+size,$"Active player values size {size} differs from the client-checked layout ({rejected}+4+4).");
            }
            // Owner|PartyMember create values: every visibility-gated field of the active player is present, so the size is fixed per character shape.
            foreach(var (race,playerClass) in new (byte,byte)[]{(1,1),(96,7),(95,11)}){
                var create=ForeverPlayerObject.CreateSelf(new ForeverCharacter{Counter=1,Name="Arthas",Race=race,Class=playerClass,Map=race>=95?2991:0,X=1,Y=2,Z=3,Customizations=new uint[]{20,3,10,1}});
                Check(BinaryPrimitives.ReadUInt32LittleEndian(create.AsSpan(7))==create.Length-11 && BinaryPrimitives.ReadUInt16LittleEndian(create)==(race>=95?2991:0),"UpdateObject block size or map wrong for race "+race+".");
            }
        });
        run("world_flight_and_chat_command_layouts",()=>{
            Check(ForeverChat.TryReadMessage(SayPayload(".fly on"),out var text) && text==".fly on" && !ForeverChat.TryReadMessage(SayPayload(".fly").Concat(new byte[]{0}).ToArray(),out _),"Chat message length/text misparsed.");
            var message=ForeverChat.SystemMessage("hi");
            Check(message.Length==44+2 && message[0]==0 && message.AsSpan(5,8).SequenceEqual(new byte[8]) && message.AsSpan(37,7).SequenceEqual(new byte[]{0,0,0,0,0,0x08,0}) && message.AsSpan(44).SequenceEqual("hi"u8),"System chat layout changed.");
            var sent=new List<(uint op,byte[] payload)>();
            var player=new ForeverWorldPlayer(new ForeverCharacter{Counter=1},(op,p)=>sent.Add((op,p)),(_,_)=>{});
            Check(ForeverChat.HandleCommand("hello",player)==null && ForeverChat.HandleCommand(".speed 99",player).Contains("usage") && sent.Count==0,"Non-commands or bad speeds acted.");
            Check(ForeverChat.HandleCommand(".fly",player).Contains("on") && sent[0].op==0x600035 && player.CanFly,"Fly toggle failed.");
            Check(ForeverChat.HandleCommand(".flyspeed 5",player)!=null && sent.Count==7 && sent[5].op==0x600024 && BitConverter.ToSingle(sent[5].payload,10)==35f,"Flight speed multiplier failed.");
            Check(ForeverMovementPackets.MoveSetSpeed(ForeverCharacterPackets.PlayerGuid(1),3,7f).SequenceEqual(new byte[]{0x01,0xE0,0x01,0x18,0x01,0x08,3,0,0,0,0,0,0xE0,0x40}),"MoveSetSpeed layout changed.");
        });
        run("world_teleport_action_bar_and_macro_layouts",()=>{
            Check(ForeverTeleportPackets.TransferPending(0,1,2,3).Length==17 && ForeverTeleportPackets.NewWorld(1,1,2,3,4,7).Length==56 && ForeverTeleportPackets.Token(5).SequenceEqual(new byte[]{5,0,0,0,0x40}),"Teleport packet sizes changed.");
            Check(ForeverActionBar.ReadSetActionButton(BitConverter.GetBytes(0x4000000000000079UL).Concat(BitConverter.GetBytes((ushort)3)).ToArray())==(0x4000000000000079UL,3),"SetActionButton misparsed.");
            var macros=ForeverMacros.CacheText();
            Check(macros.StartsWith("VER 3 0100000000000079 \"Fly\" \"Ability_Mount_Gryphon_01\"\r\n/s .fly\r\nEND\r\n") && macros.Split("END\r\n").Length==8,"Macro cache text changed.");
            // The client's own copy (as uploaded by the 70338 client, with the player's edits) keeps everything; missing defaults follow.
            var uploaded="VER 3 0100000000000079 \"Fly\" \"A\"\r\n/s .fly\r\nEND\r\nVER 3 010000000000007A \"Fly Fast\" \"B\"\r\n/s .flyspeed 50\r\nEND\r\nVER 3 010000000000007B \"Fly Normal\" \"C\"\r\n/s .flyspeed 10\r\nEND\r\n"
                +"VER 3 010000000000007C \"Run Fast\" \"D\"\r\n/s .speed 2\r\nEND\r\nVER 3 010000000000007D \"Speed Reset\" \"E\"\r\n/s .speed 1\r\nEND\r\n";
            var merged=ForeverMacros.Merge(uploaded,out var added);
            Check(merged.StartsWith(uploaded) && merged.Contains("VER 3 010000000000007E \"Blink\"") && merged.Contains("VER 3 010000000000007F \"Slow Fall\"") && added.SequenceEqual(new[]{126,127}),"Macro merge changed or lost edits.");
            Check(ForeverMacros.Merge(merged,out var none)==merged && none.Count==0,"Merge is not idempotent.");
            var bar=new Dictionary<int,ulong>{[0]=1,[1]=1,[2]=1,[4]=1};ForeverMacros.PlaceOnEmptyBar(bar,added);
            Check(bar[3]==(0x41UL<<56|126) && bar[5]==(0x41UL<<56|127),"New macros not placed on the first empty buttons.");
            var data=new ForeverAccountData(null);
            Check(data.SeedIfMissing(3,5,macros,100) && !data.SeedIfMissing(3,5,"x",200) && data.SeedIfMissing(4,5,macros,100),"Per-character macro seeding wrong.");
            var times=data.AccountDataTimes(1,ForeverCharacterPackets.PlayerGuid(3));
            Check(BinaryPrimitives.ReadInt64LittleEndian(times.AsSpan(6+8+5*8))==100 && BinaryPrimitives.ReadInt64LittleEndian(data.AccountDataTimes(1).AsSpan(2+8+5*8))==0,"Character / account-wide data times mixed up.");
            Check(ForeverLocations.ValidName("home_2") && !ForeverLocations.ValidName("a b") && ForeverLocations.BuiltIn.Any(l=>l.Name=="Zephras" && l.Map==2991),"Location names or built-ins changed.");
        });
        run("client_install_terrain_and_fog_rows_when_present",()=>{
            // Real local client files (skipped when absent): the Battle.net install and a frozen snapshot (FOREVER_SANDBOX_CLIENT,
            // set by scripts/Test.ps1 -Client). Storage, decryption keys, Map table, heights, an encrypted-capable client table.
            var installs=new List<string>{@"C:\Blizzard Games\World of Warcraft"};
            if(Environment.GetEnvironmentVariable("FOREVER_SANDBOX_CLIENT") is {Length:>0} snapshot)installs.Add(snapshot);
            foreach(var install in installs.Where(i=>File.Exists(Path.Combine(i,".build.info"))))CheckClientInstall(install);
        });
        static void CheckClientInstall(string install){
            var hot=ForeverHotfixStore.Load("data/hotfixes.json");
            ForeverTerrain.Current=null;ForeverTerrain.StartLoading(install,"1.60.1.70338",id=>hot.RecordData(ForeverHotfixStore.TactKeyTable,id));
            var clock=System.Diagnostics.Stopwatch.StartNew();
            while(ForeverTerrain.Current==null && !ForeverTerrain.Status.StartsWith("unavailable") && clock.Elapsed.TotalSeconds<180)Thread.Sleep(100);
            var t=ForeverTerrain.Current;Check(t!=null,"Client install did not open: "+ForeverTerrain.Status);
            Check(t.HasMap(0) && t.HasMap(1) && t.HasMap(2991),"Map table missing maps.");
            var northshire=t.Height(0,-8949.95f,-132.493f);var goldshire=t.Height(0,-9464f,62f);
            Check(northshire is >83f and <84.2f && goldshire is >55.4f and <56.6f,$"Terrain heights off: {northshire} {goldshire}.");
            var light=t.Table(1375580);   // LightData (WoWDBDefs layout 360DA016 for 1.60.1)
            Check(light.LayoutHash==0x360DA016 && light.TableHash==0x0AD67EBF && light.Ids.Count()>5000,"LightData table unreadable or its layout changed.");
            // Impassable walls (MCNK flag 0x2) at the southern edge of Burning Steppes: edges lie on the chunk grid, and a shared
            // edge between two impassable chunks is red from both sides (the transposed-neighbour bug would break this symmetry).
            var walls=t.ImpassWalls(0,-8086f,-1867f,1);
            Check(walls.Count>0 && walls.All(e=>float.IsFinite(e.Floor) && (e.X1==e.X2)!=(e.Y1==e.Y2) && MathF.Abs(MathF.Abs(e.X1-e.X2)+MathF.Abs(e.Y1-e.Y2)-ForeverTerrain.ChunkSize)<0.01f),"Impassable walls missing or off the chunk grid.");
            static (float,float,float,float) Key(ForeverTerrain.ImpassEdge e)=>(MathF.Round(MathF.Min(e.X1,e.X2),1),MathF.Round(MathF.Min(e.Y1,e.Y2),1),MathF.Round(MathF.Max(e.X1,e.X2),1),MathF.Round(MathF.Max(e.Y1,e.Y2),1));
            var shared=walls.GroupBy(Key).Where(g=>g.Count()==2).ToList();
            Check(shared.Count>0 && shared.All(g=>g.All(e=>e.Red)) && walls.GroupBy(Key).Where(g=>g.Count()==1).Any(g=>!g.Single().Red),"Impassable wall colours are inconsistent across shared edges.");
        }
        run("game_object_create_block_and_removal_layout",()=>{
            // BaseEntity::BuildCreateUpdateBlockForPlayer / BuildMovementUpdate for a GameObject, UpdateData::BuildPacket removals.
            var guid=ForeverGameObjects.Guid(1,900000,5);
            Check(BinaryPrimitives.ReadUInt64LittleEndian(guid.AsSpan(8))==((11UL<<58)|(70UL<<42)|(1UL<<29)|(900000UL<<6)) && BinaryPrimitives.ReadUInt64LittleEndian(guid)==5,"GameObject GUID layout wrong.");
            Check(ForeverGameObjects.PackRotation(ForeverGameObjects.Rotation(MathF.PI/2))==(ulong)(int)(MathF.Sin(MathF.PI/4)*(1<<20)) && ForeverGameObjects.PackRotation((0,0,0,1))==0,"Packed rotation differs from GameObject::UpdatePackedRotation.");
            var block=ForeverGameObjects.CreateBlock(new ForeverGameObjects.Spawn(guid,900000,523,1f,2f,3f,0.5f,ForeverGameObjects.Rotation(0.5f),1.5f));
            var g=new ForeverPacketWriter().Guid(guid).ToArray();var at=1+g.Length;
            Check(block[0]==2 && block.AsSpan(1,g.Length).SequenceEqual(g) && block[at]==8 && block[at+1]==0x81 && block[at+2]==0x08 && block[at+3]==0,"Create header or update flags wrong.");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(at+4))==0 && BinaryPrimitives.ReadSingleLittleEndian(block.AsSpan(at+8))==1f && BinaryPrimitives.ReadSingleLittleEndian(block.AsSpan(at+20))==0.5f,"Stationary position wrong.");
            var size=BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(at+32));
            Check(size==block.Length-(at+36) && block.AsSpan(at+36,5).SequenceEqual(new byte[]{0,3,206,255,1}) && BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(at+41))==900000 && BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(at+49+4))==523,"Values size, fragments, entry or display wrong.");
            var packet=ForeverGameObjects.Packets(1,new List<byte[]>(),new List<byte[]>{guid}).Single();
            Check(packet.AsSpan(0,7).SequenceEqual(new byte[]{1,0,0,0,0,0,0xC0}) && BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(7))==0 && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(9))==1 && packet.AsSpan(13,g.Length).SequenceEqual(g),"Out-of-range packet layout wrong.");
            Check(!ForeverGameObjects.Packets(1,new List<byte[]>(),new List<byte[]>()).Any() && ForeverGameObjects.Packets(1,Enumerable.Repeat(block,250).ToList(),new List<byte[]>()).Count()==3,"Packet batching wrong.");
            // GameObjectDisplayInfo hotfix rows for the wall models: layout 16D1DD61 is 52 bytes (ID not inline), one push.
            var rows=ForeverImpassWalls.DisplayHotfixes().ToList();
            Check(rows.Count==2+2*ForeverWallModels.Tints.Length && rows.All(r=>r.Data.Length==52 && r.Table==0x6D100DCB && r.Push==rows[0].Push)
                && rows.Select(r=>r.RecordId).SequenceEqual(Enumerable.Range(900001,rows.Count)) && rows.Select(r=>r.Unique).Distinct().Count()==rows.Count
                && BinaryPrimitives.ReadInt32LittleEndian(rows[2].Data.AsSpan(24))==ForeverWallModels.ModelFile(0,false)
                && BinaryPrimitives.ReadInt32LittleEndian(rows[^1].Data.AsSpan(24))==ForeverWallModels.ModelFile(ForeverWallModels.Tints.Length-1,true),"Wall display hotfix rows wrong.");
        });
        run("ed25519ctx_rfc8032_section_7_2_vectors",()=>{
            foreach(var (seed,publicKey,message,context,signature) in new[]{
                ("0305334e381af78f141cb666f6199f57bc3495335a256a95bd2a55bf546663f6","dfc9425e4f968f7f0c29f0259cf5f9aed6851c2bb4ad8bfb860cfee0ab248292","f726936d19c800494e3fdaff20b276a8","666f6f","55a4cc2f70a54e04288c5f4cd1e45a7bb520b36292911876cada7323198dd87a8b36950b95130022907a7fb7c4e9b2d5f6cca685a587b4b21f4b888e4e7edb0d"),
                ("0305334e381af78f141cb666f6199f57bc3495335a256a95bd2a55bf546663f6","dfc9425e4f968f7f0c29f0259cf5f9aed6851c2bb4ad8bfb860cfee0ab248292","f726936d19c800494e3fdaff20b276a8","626172","fc60d5872fc46b3aa69f8b5b4351d5808f92bcc044606db097abab6dbcb1aee3216c48e8b3b66431b5b186d1d28f8ee15a5ca2df6668346291c2043d4eb3e90d"),
                ("0305334e381af78f141cb666f6199f57bc3495335a256a95bd2a55bf546663f6","dfc9425e4f968f7f0c29f0259cf5f9aed6851c2bb4ad8bfb860cfee0ab248292","508e9e6882b979fea900f62adceaca35","666f6f","8b70c1cc8310e1de20ac53ce28ae6e7207f33c3295e03bb5c0732a1d20dc64908922a8b052cf99b7c4fe107a5abb5b2c4085ae75890d02df26269d8945f84b0b"),
                ("ab9c2853ce297ddab85c993b3ae14bcad39b2c682beabc27d6d4eb20711d6560","0f1d1274943b91415889152e893d80e93275a1fc0b65fd71b4b0dda10ad7d772","f726936d19c800494e3fdaff20b276a8","666f6f","21655b5f1aa965996b3f97b3c849eafba922a0a62992f73b3d1b73106a84ad85e9b86a7b6005ea868337ff2d20a7f5fbd4cd10b0be49a68da2b2e0dc0ad8960f")}){
                Check(Ed25519Ctx.PublicKey(Hex(seed)).SequenceEqual(Hex(publicKey)),"Ed25519 public key derivation differs from RFC 8032.");
                Check(Ed25519Ctx.Sign(Hex(seed),Hex(message),Hex(context)).SequenceEqual(Hex(signature)),"Ed25519ctx signature differs from RFC 8032.");
                Check(Ed25519Ctx.Verify(Hex(publicKey),Hex(message),Hex(signature),Hex(context)),"RFC 8032 signature did not verify.");
                var tampered=Hex(signature);tampered[5]^=1;
                Check(!Ed25519Ctx.Verify(Hex(publicKey),Hex(message),tampered,Hex(context)) && !Ed25519Ctx.Verify(Hex(publicKey),Hex(message),Hex(signature),Hex("00")),"Tampered signature or wrong context verified.");
            }
        });
        run("world_interop_signing_key_matches_launcher_group8_replacement",()=>Check(Convert.ToHexString(Ed25519Ctx.PublicKey(ForeverWorldKeys.InteropSigningSeed))=="02596F0D0C061A8B30745988FD72C59E29EC367FB0F341F28E0F08D037BAFC69","Interop public key differs from ForeverLauncher's default store replacement."));
        run("world_session_encrypt_key_and_signed_digest_match_independent_python",()=>{
            // Golden values from fixtures/world_keys_reference.py (standard-library HMAC/SHA-512).
            var session=ForeverWorldKeys.SessionKey(Range(0,64),Range(64,32),Range(96,32));
            Check(Convert.ToHexString(session)=="1E17BF4C22887362B5AF971B4D98635B87510FDF1923BE3A3DF6BCC77798096AB9C7CDDB00C5056D","40-byte session key derivation changed.");
            var encrypt=ForeverWorldKeys.EncryptKey(session,Range(96,32),Range(64,32));
            Check(Convert.ToHexString(encrypt)=="A9D164F2437A286D5C9129EF7D47BE9AB42FDF8D52AE5496DCAF4022E5099A91","Encryption key derivation changed.");
            Check(Convert.ToHexString(ForeverWorldKeys.SignedDigest(encrypt,true))=="B663356E24E8AB58BFDE4E67F0D2C5E05A29AA40D128AB62F003934C227DD5557E2D4EA4157F71379939472BCD49AADC035ED49B400FB189C84ED34CB6E40626","EnterEncryptedMode signed digest changed.");
            var payload=ForeverWorldKeys.EnterEncryptedMode(encrypt,ForeverWorldKeys.InteropSigningSeed);
            Check(payload.Length==69 && BinaryPrimitives.ReadInt32LittleEndian(payload)==8 && payload[68]==0x80,"EnterEncryptedMode region/bit layout changed.");
            Check(Ed25519Ctx.Verify(Ed25519Ctx.PublicKey(ForeverWorldKeys.InteropSigningSeed),ForeverWorldKeys.SignedDigest(encrypt,true),payload[4..68],ForeverWorldKeys.EnableEncryptionContext),"EnterEncryptedMode signature does not verify.");
            foreach(var invalid in new[]{new byte[63],new byte[65]}){try{ForeverWorldKeys.SessionKey(invalid,Range(64,32),Range(96,32));throw new Exception("Wrong-width key data accepted.");}catch(ArgumentException){}}
        });
        run("world_glue_packet_bit_layouts",()=>{
            var auth=ForeverWorldPackets.AuthResponseOk(0x1122334455667788);
            Check(auth.Take(5).SequenceEqual(new byte[]{0,0,0,0,0x80}) && BinaryPrimitives.ReadUInt32LittleEndian(auth.AsSpan(5))==BgsRealmV2.RealmAddress && BinaryPrimitives.ReadUInt32LittleEndian(auth.AsSpan(9))==1,"AuthResponse header changed.");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(auth.AsSpan(23))==10 && BinaryPrimitives.ReadInt64LittleEndian(auth.AsSpan(48))==0x1122334455667788 && auth[^1]==0,"AuthResponse counts/time/trailing bits changed.");
            // IsLocal 1, IsInternal 0, then unaligned 8-bit sizes 15 and 14: 10 000011|11 000011|10 000000.
            var realm=auth.AsSpan(56);Check(BinaryPrimitives.ReadUInt32LittleEndian(realm)==BgsRealmV2.RealmAddress && realm[4]==0x83 && realm[5]==0xC3 && realm[6]==0x80,"Virtual realm name header changed.");
            // 30-byte header + ClassDisableMask, then 8 race unlocks: 8*(race 1 + count 4 + bits 1) + 42 classes*(1 + 4 + 1).
            // Only Success (and Realmless with characters) is set; IsNewcomerChatCompleted locked every race in 70338.
            var characters=ForeverWorldPackets.EnumCharactersResult(Array.Empty<ForeverCharacter>());
            Check(characters.Length==34+60+312 && characters[0]==0x80 && characters[1]==0x80 && BinaryPrimitives.ReadInt32LittleEndian(characters.AsSpan(10))==1 && BinaryPrimitives.ReadUInt32LittleEndian(characters.AsSpan(14))==10 && BinaryPrimitives.ReadUInt32LittleEndian(characters.AsSpan(30))==0,"EnumCharactersResult layout changed.");
            Check(characters[34]==1 && BinaryPrimitives.ReadUInt32LittleEndian(characters.AsSpan(35))==6 && characters[39]==1 && characters[44]==0xE0 && characters[75]==0xD0 && characters[^1]==0x90 && characters[^36]==96,"Race/class unlock bits changed (classic races: achievement earned; race 96 last: none needed).");
            Check(ForeverWorldPackets.IsAvailable(5,2) && ForeverWorldPackets.IsAvailable(3,7) && !ForeverWorldPackets.IsAvailable(6,8) && ForeverWorldPackets.IsAvailable(95,8) && ForeverWorldPackets.IsAvailable(96,7) && ForeverWorldPackets.StartPosition(95).map==2991,"Forever race/class additions changed.");
            var zone=ForeverWorldPackets.SetTimeZoneInformation("Europe/Paris");Check(zone.Length==3+36 && zone[0]==(12<<1) && zone[1]==(12<<2)>>0 && zone[2]==(12<<3)>>0,"Time zone 7-bit size packing changed.");
            Check(ForeverWorldPackets.FeatureSystemStatusGlueScreen().Length==6+4+8+18*4+2*2,"Classic glue feature status length changed.");
        });
        run("world_encrypted_session_synthetic_end_to_end",()=>{
            // A present launcher signal releases EnterEncryptedMode at once, well before the 10 s upper bound.
            var signal=Path.Combine(Path.GetTempPath(),"forever-key-signal-"+Guid.NewGuid().ToString("N"));File.WriteAllText(signal,"patched");
            ForeverWorldHandshake.EncryptDelayMilliseconds=10000;ForeverWorldHandshake.RealmKeySignalPath=signal;
            ForeverJoinRegistry.Publish(BgsRealmV2.DemoGameAccount,Range(0,64));ForeverCharacterStore.Current=new ForeverCharacterStore(null);
            var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
            var server=new Thread(()=>new ForeverWorldHandshake(listener.AcceptSocket(),true).Accept());server.Start();
            var instance=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);instance.Start();ForeverWorldHandshake.InstancePort=((System.Net.IPEndPoint)instance.LocalEndpoint).Port;
            // Two instance connections: the first login and the relog after logout.
            var instanceServer=new Thread(()=>{for(var i=0;i<2;i++)new ForeverInstanceSocket(instance.AcceptSocket()).Accept();});instanceServer.Start();
            var clock=System.Diagnostics.Stopwatch.StartNew();
            ForeverHotfixStore.Current=new ForeverHotfixStore(new[]{new ForeverHotfixStore.Record(5,6,0xDF2F53CF,8341,1,Range(1,16))});ForeverAccountData.Current=new ForeverAccountData(null);
            try{WorldSessionClient(((System.Net.IPEndPoint)listener.LocalEndpoint).Port,Range(0,64),true);}
            finally{ForeverWorldHandshake.RealmKeySignalPath=null;ForeverWorldHandshake.EncryptDelayMilliseconds=0;File.Delete(signal);}
            Check(clock.ElapsedMilliseconds<5000,"Key signal did not release EnterEncryptedMode early.");
            Check(server.Join(5000) && instanceServer.Join(5000),"World or instance session did not close after client disconnect.");listener.Stop();instance.Stop();
        });
        run("world_encrypted_frame_rejects_tampered_tag",()=>{
            using var serverCrypt=new ForeverPacketCrypt();using var client=new ClientCrypt();var key=Range(1,32);
            client.Seal(0x460001,new byte[4]);ForeverWorldWire.ReadSealed(new MemoryStream(ForeverWorldWire.Seal(new ForeverPacketCrypt(),1,new byte[4])),serverCrypt);
            serverCrypt.Enable(key);client.Enable(key);var wire=client.Seal(0x440014,Array.Empty<byte>());wire[6]^=1;
            try{ForeverWorldWire.ReadSealed(new MemoryStream(wire),serverCrypt);throw new Exception("Tampered GCM tag accepted.");}catch(System.Security.Cryptography.AuthenticationTagMismatchException){}
        });
        run("world_70338_auth_challenge_golden_header_and_widths",()=>{
            var dos=Enumerable.Range(0,32).Select(i=>(byte)i).ToArray();var nonce=Enumerable.Range(32,32).Select(i=>(byte)i).ToArray();var wire=ForeverWorldWire.Challenge(dos,nonce);
            Check(wire.Length==85 && wire.Take(20).SequenceEqual(new byte[]{69,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,78,0}),"Modern 16-byte header/uint32 opcode changed.");
            Check(wire.AsSpan(20,32).SequenceEqual(dos) && wire.AsSpan(52,32).SequenceEqual(nonce) && wire[84]==1,"World challenge fields changed.");
        });
        run("world_fragmented_and_coalesced_pre_encryption_frames",()=>{var challenge=ForeverWorldWire.Challenge(new byte[32],new byte[32]);var auth=ForeverWorldWire.Pack(0x460001,AuthPayload());using var stream=new ChunkedStream(challenge.Concat(auth).ToArray(),1);CheckChallenge(ForeverWorldWire.Read(stream));var frame=ForeverWorldWire.Read(stream);Check(frame.Opcode==0x460001 && ForeverWorldWire.AuthShape(frame.Payload).Realm==70 && ForeverWorldWire.Read(stream)==null,"World frame boundary or auth shape changed.");});
        run("world_initializer_requires_bounded_complete_line",()=>{using var stream=new ChunkedStream(Encoding.ASCII.GetBytes(ForeverWorldWire.ClientInitializer),1);Check(ForeverWorldWire.ReadInitializer(stream)==ForeverWorldWire.ClientInitializer,"World initializer fragmentation failed.");foreach(var bytes in new[]{Encoding.ASCII.GetBytes("partial"),new byte[128]}){using var invalid=new MemoryStream(bytes);try{ForeverWorldWire.ReadInitializer(invalid);throw new Exception("Invalid initializer accepted.");}catch(Exception error)when(error is IOException || error is FormatException){}}});
        run("world_rejects_truncation_oversize_and_early_encryption_tag",()=>{
            var header=new byte[16];BinaryPrimitives.WriteUInt32LittleEndian(header,65541);
            var tagged=ForeverWorldWire.Pack(1,Array.Empty<byte>());tagged[4]=1;
            foreach(var bytes in new[]{new byte[]{1},header,tagged,ForeverWorldWire.Pack(1,new byte[3])[..^1]}){using var stream=new ChunkedStream(bytes,1);try{ForeverWorldWire.Read(stream);throw new Exception("Invalid world frame accepted.");}catch(Exception error)when(error is IOException || error is FormatException){}}
        });
        run("world_auth_ticket_exact_length_and_metadata_only_shape",()=>{var payload=AuthPayload();var shape=ForeverWorldWire.AuthShape(payload);Check(shape.Region==70 && shape.Site==1 && shape.Realm==70 && !shape.UseIpv6 && shape.TicketBytes==payload.Length-81,"World auth coordinates wrong.");foreach(var bytes in new[]{payload[..80],payload[..^1],payload.Concat(new byte[]{0}).ToArray()}){try{ForeverWorldWire.AuthShape(bytes);throw new Exception("Ambiguous auth payload accepted.");}catch(FormatException){}}});
    }
}
