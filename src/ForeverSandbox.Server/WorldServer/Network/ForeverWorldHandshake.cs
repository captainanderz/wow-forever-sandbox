// Forever 70338 realm-socket handler. Probe mode (1) measures one auth session and
// closes. Session mode (2) derives keys from the realm join, sends the signed
// EnterEncryptedMode, enables AES-GCM after the client's ACK and answers glue-screen
// requests; PLAYER_LOGIN hands over to the instance socket (ForeverWorldEntry.cs). Logs contain metadata only, never keys/tickets.
using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AuthServer.Network;

internal sealed class ForeverWorldHandshake
{
    // The client verifies EnterEncryptedMode with a key store it builds during login;
    // a short wait gives an owning launcher time to write the replacement key first.
    internal static int EncryptDelayMilliseconds = 2500;
    // With a signal file (written by the owning launcher after a verified key-store write)
    // the delay becomes an upper bound: EnterEncryptedMode goes out as soon as it exists.
    internal static string RealmKeySignalPath;
    // Realm port advertised by realm joins relayed over this socket (ruleset switch).
    internal static int RealmPort = 8085;
    // Instance port advertised by SMSG_CONNECT_TO for world entry.
    internal static int InstancePort = 3724;
    private const int MaxFrameEvents = 256;
    private readonly Socket socket;
    private readonly bool session;
    public ForeverWorldHandshake(Socket client, bool fullSession = false) { socket = client; session = fullSession; }

    public void Accept()
    {
        var connection = global::AuthServer.LocalAuthDiagnostics.NewConnection();
        using var crypt = new ForeverPacketCrypt();
        byte[] keyData = null, sessionKey = null, encryptKey = null;
        using var realmState = new BgsRealmState();
        try
        {
            socket.Blocking = true; socket.NoDelay = true; socket.ReceiveTimeout = session ? 1800000 : 15000; socket.SendTimeout = 5000;
            using var stream = new NetworkStream(socket, true);
            stream.Write(Encoding.ASCII.GetBytes(ForeverWorldWire.ServerInitializer));
            if (ForeverWorldWire.ReadInitializer(stream) != ForeverWorldWire.ClientInitializer) throw new FormatException("Unexpected world initializer.");
            Emit("world_initializer", new { connection, profile = "Forever70338", accepted = true });
            var serverChallenge = RandomNumberGenerator.GetBytes(32);
            var challenge = new byte[65]; RandomNumberGenerator.Fill(challenge.AsSpan(0, 32)); serverChallenge.CopyTo(challenge, 32); challenge[64] = 1;
            stream.Write(ForeverWorldWire.Seal(crypt, ForeverWorldWire.AuthChallengeOpcode, challenge));
            Emit("world_auth_challenge_sent", new { connection, opcode = "0x004E0000", dosBytes = 32, challengeBytes = 32, dosZeroBits = 1 });
            var frame = ForeverWorldWire.ReadSealed(stream, crypt);
            if (frame == null) { Emit("world_eof", new { connection, encrypted = false }); return; }
            Emit("world_request", new { connection, opcode = $"0x{frame.Opcode:X8}", payloadBytes = frame.Payload.Length });
            if (frame.Opcode != ForeverWorldWire.AuthSessionOpcode) return;
            var auth = ForeverWorldWire.AuthSession(frame.Payload);
            var shape = auth.Shape;
            Emit("world_auth_session_received", new { connection, region = shape.Region, site = shape.Site, realm = shape.Realm,
                useIpv6 = shape.UseIpv6, ticketBytes = shape.TicketBytes, localChallengeBytes = 32, digestBytes = 24,
                worldAuthenticationCompleted = false, nextStage = session ? "enter_encrypted_mode" : "world_key_derivation_and_signed_encrypted_mode" });
            if (!session) return;

            // The 24-byte digest needs the unknown per-build auth key; like the
            // reference's SkipBuildAuthKeyCheck, it is not verified locally.
            keyData = ForeverJoinRegistry.Find(auth.GameAccount);
            if (keyData == null || shape.Realm != 70) { Emit("world_auth_denied", new { connection, joinKeyFound = keyData != null, realmMatches = shape.Realm == 70 }); return; }
            // Relayed realm-list RPCs use the last join's client secret as their ticket, like the reference.
            realmState.ClientSecret = keyData[..32]; realmState.TicketGranted = true;
            realmState.Platform = auth.Platform; realmState.Type = auth.Type; realmState.Arch = auth.Arch;
            sessionKey = ForeverWorldKeys.SessionKey(keyData, serverChallenge, auth.LocalChallenge);
            encryptKey = ForeverWorldKeys.EncryptKey(sessionKey, auth.LocalChallenge, serverChallenge);
            var (waited, keySignal) = WaitForRealmKey();
            stream.Write(ForeverWorldWire.Seal(crypt, ForeverWorldPackets.EnterEncryptedModeOpcode, ForeverWorldKeys.EnterEncryptedMode(encryptKey, ForeverWorldKeys.InteropSigningSeed)));
            Emit("world_enter_encrypted_mode_sent", new { connection, opcode = $"0x{ForeverWorldPackets.EnterEncryptedModeOpcode:X8}", regionGroup = ForeverWorldKeys.RegionGroup,
                signatureBytes = 64, waitedMilliseconds = waited, maxDelayMilliseconds = EncryptDelayMilliseconds, keySignal, signingKey = "interop-group-8" });

            for (var frames = 0; ; frames++)
            {
                frame = ForeverWorldWire.ReadSealed(stream, crypt);
                if (frame == null) { Emit("world_eof", new { connection, encrypted = crypt.Enabled }); return; }
                if (frames < MaxFrameEvents) Emit("world_frame", new { connection, opcode = $"0x{frame.Opcode:X8}", payloadBytes = frame.Payload.Length, encrypted = crypt.Enabled });
                try { if (crypt.Enabled && (HandleCharacterSelect(stream, crypt, connection, frame, sessionKey) || HandleBattlenet(stream, crypt, connection, frame, realmState) || HandleGlueData(stream, crypt, connection, frame) || HandleChat(stream, crypt, connection, frame))) continue; }
                catch (Exception error) when (error is FormatException or ArgumentException) { Emit("world_packet_malformed", new { connection, opcode = $"0x{frame.Opcode:X8}", payloadBytes = frame.Payload.Length }); continue; }
                switch (frame.Opcode)
                {
                    case ForeverWorldPackets.EnterEncryptedModeAckOpcode when !crypt.Enabled:
                        crypt.Enable(encryptKey);
                        Emit("world_encryption_enabled", new { connection, cipher = "AES-256-GCM", tagBytes = 12 });
                        Send(stream, crypt, ForeverWorldPackets.AuthResponseOpcode, ForeverWorldPackets.AuthResponseOk(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                        Send(stream, crypt, ForeverWorldPackets.SetTimeZoneInformationOpcode, ForeverWorldPackets.SetTimeZoneInformation());
                        Send(stream, crypt, ForeverWorldPackets.FeatureSystemStatusGlueScreenOpcode, ForeverWorldPackets.FeatureSystemStatusGlueScreen());
                        // Startup prerequisites the character list waits for (client General.log): hotfixes and account data.
                        Send(stream, crypt, ForeverHotfixStore.AvailableHotfixesOpcode, ForeverHotfixStore.Current.AvailableHotfixes(BgsRealmV2.RealmAddress));
                        Send(stream, crypt, ForeverAccountData.AccountDataTimesOpcode, ForeverAccountData.Current.AccountDataTimes(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                        // The client drops its direct BGS connection after this login; tell it Battle.net is reachable here.
                        Send(stream, crypt, ForeverBattlenetRelay.ConnectionStatusOpcode, ForeverBattlenetRelay.ConnectionStatus());
                        foreach (var (opcode, payload) in ForeverBattlenetRelay.SubscribeResults()) Send(stream, crypt, opcode, payload);
                        Emit("world_auth_response_sent", new { connection, result = 0, worldAuthenticationCompleted = true,
                            followUps = new[] { "set_time_zone_information", "feature_system_status_glue_screen", "available_hotfixes", "account_data_times", "battlenet_connection_status", "friends_subscribe", "block_list_subscribe" },
                            availableHotfixes = ForeverHotfixStore.Current.Count, sandboxHotfixPushes = ForeverHotfixStore.Current.OverlayPushes });
                        break;
                    case ForeverWorldPackets.LogDisconnectOpcode:
                        Emit("world_log_disconnect", new { connection, reason = frame.Payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload) : (uint?)null, encrypted = crypt.Enabled });
                        break;
                    case ForeverWorldPackets.PingOpcode when frame.Payload.Length >= 4:
                        Send(stream, crypt, ForeverWorldPackets.PongOpcode, ForeverWorldPackets.Pong(BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload)));
                        break;
                }
            }
        }
        catch (AuthenticationTagMismatchException) { Emit("world_decrypt_failure", new { connection }); }
        catch (Exception error) { Emit("world_handshake_failure", new { connection, errorType = error.GetType().Name }); }
        finally
        {
            foreach (var secret in new[] { keyData, sessionKey, encryptKey }) if (secret != null) CryptographicOperations.ZeroMemory(secret);
            socket.Dispose();
        }
    }

    // Character select requests; returns false for opcodes it does not own. Names are
    // player-chosen display text, but only their lengths are logged.
    private static bool HandleCharacterSelect(NetworkStream stream, ForeverPacketCrypt crypt, int connection, ForeverWorldFrame frame, byte[] sessionKey)
    {
        var store = ForeverCharacterStore.Current;
        switch (frame.Opcode)
        {
            case ForeverWorldPackets.EnumCharactersOpcode:
                var characters = store.List();
                Send(stream, crypt, ForeverWorldPackets.EnumCharactersResultOpcode, ForeverWorldPackets.EnumCharactersResult(characters));
                if (characters.Length > 0) Send(stream, crypt, ForeverCharacterPackets.RestrictionsOpcode, ForeverCharacterPackets.Restrictions(characters));
                Send(stream, crypt, ForeverCharacterPackets.RecentAllyReleaseOpcode, ForeverCharacterPackets.RecentAllyRelease());
                Emit("world_enum_characters_sent", new { connection, characters = characters.Length, raceUnlocks = 10 });
                return true;
            case ForeverCharacterPackets.RandomNameOpcode:
                var randomName = ForeverCharacterPackets.RandomName(store);
                Send(stream, crypt, ForeverCharacterPackets.RandomNameResultOpcode, ForeverCharacterPackets.RandomNameResult(randomName));
                Emit("world_random_name_sent", new { connection, nameLength = randomName.Length });
                return true;
            case ForeverCharacterPackets.CheckNameOpcode:
                var (sequence, name) = ForeverCharacterPackets.ReadCheckName(frame.Payload);
                var available = ForeverCharacterStore.ValidName(name) && !store.NameTaken(name);
                Send(stream, crypt, ForeverCharacterPackets.CheckNameResultOpcode, ForeverCharacterPackets.CheckNameResult(sequence, available ? ForeverCharacterPackets.CharNameSuccess : ForeverCharacterPackets.CharNameFailure));
                Emit("world_check_name", new { connection, nameLength = name.Length, available });
                return true;
            case ForeverCharacterPackets.CreateCharacterOpcode:
                var character = ForeverCharacterPackets.ReadCreate(frame.Payload);
                var code = store.Create(character);
                var created = code == ForeverCharacterPackets.CharCreateSuccess;
                Send(stream, crypt, ForeverCharacterPackets.CreateCharOpcode, ForeverCharacterPackets.CreateChar(code, created ? ForeverCharacterPackets.PlayerGuid(character.Counter) : null));
                Emit("world_character_create", new { connection, code, race = character.Race, playerClass = character.Class, sex = character.Sex,
                    customizations = character.Customizations.Length / 2, nameLength = character.Name.Length, surnameLength = character.Surname.Length });
                return true;
            case ForeverCharacterPackets.DeleteOpcode:
                var deleted = store.Delete(ForeverCharacterPackets.Counter(new ForeverPacketReader(frame.Payload).PackedGuid()));
                Send(stream, crypt, ForeverCharacterPackets.DeleteCharOpcode, ForeverCharacterPackets.DeleteChar(deleted ? ForeverCharacterPackets.CharDeleteSuccess : ForeverCharacterPackets.CharDeleteFailed));
                Emit("world_character_delete", new { connection, deleted });
                return true;
            case ForeverCharacterPackets.PlayerLoginOpcode:
                // Like the reference: hand the login to the instance socket with a one-time connect key.
                var counter = ForeverCharacterPackets.Counter(new ForeverPacketReader(frame.Payload).PackedGuid());
                var chosen = store.List().FirstOrDefault(c => c.Counter == counter);
                if (chosen == null)
                {
                    Send(stream, crypt, ForeverCharacterPackets.LoginFailedOpcode, ForeverCharacterPackets.LoginFailed(ForeverCharacterPackets.LoginFailedNoWorld));
                    Emit("world_player_login_declined", new { connection, reason = "unknown_character" });
                    return true;
                }
                var connectKey = ForeverInstanceRegistry.Issue(sessionKey, chosen, (opcode, payload) => Send(stream, crypt, opcode, payload));
                Send(stream, crypt, ForeverLoginPackets.ConnectToOpcode, ForeverLoginPackets.ConnectTo(connectKey, InstancePort));
                Emit("world_connect_to_sent", new { connection, serial = ForeverLoginPackets.WorldAttempt1, instancePort = InstancePort, map = chosen.Map, race = chosen.Race });
                return true;
            // Client replies that may come on this connection while in the world: forwarded to the character in the world.
            case ForeverActionBar.SetActionButtonOpcode when ForeverWorldPlayer.Current != null:
                var (packed, index) = ForeverActionBar.ReadSetActionButton(frame.Payload);
                ForeverWorldPlayer.Current.SetActionButton(index, packed);
                Emit("world_set_action_button", new { connection, index, type = $"0x{packed >> 56:X2}", action = packed & 0x00FFFFFFFFFFFFFF });
                return true;
            case ForeverTeleportPackets.SuspendTokenResponseOpcode when ForeverWorldPlayer.Current != null:
                Emit("world_suspend_token_response", new { connection, handled = ForeverWorldPlayer.Current.OnSuspendTokenResponse() });
                return true;
            case ForeverTeleportPackets.WorldPortResponseOpcode when ForeverWorldPlayer.Current != null:
                Emit("world_world_port_response", new { connection, handled = ForeverWorldPlayer.Current.OnWorldPortResponse() });
                return true;
            case ForeverLoginPackets.LogoutRequestOpcode when ForeverWorldPlayer.Current != null:
                ForeverWorldPlayer.Current.Logout();
                Emit("world_logout", new { connection });
                return true;
            case ForeverLoginPackets.ConnectToFailedOpcode:
                var (failedCon, failedSerial) = ForeverLoginPackets.ReadConnectToFailed(frame.Payload);
                Emit("world_connect_to_failed", new { connection, con = failedCon, serial = failedSerial });
                return true;
            default: return false;
        }
    }

    private static bool HandleBattlenet(NetworkStream stream, ForeverPacketCrypt crypt, int connection, ForeverWorldFrame frame, BgsRealmState realmState)
    {
        switch (frame.Opcode)
        {
            case ForeverBattlenetRelay.RequestOpcode:
                var request = ForeverBattlenetRelay.ReadRequest(frame.Payload);
                var (status, payload, route) = ForeverBattlenetRelay.Dispatch(request, realmState, RealmPort);
                Send(stream, crypt, ForeverBattlenetRelay.ResponseOpcode, ForeverBattlenetRelay.Response(status, request.Type, 1, request.Token, payload));
                Emit("world_bnet_request", new { connection, serviceHash = $"0x{request.ServiceHash:X8}", methodId = request.MethodId, token = request.Token,
                    requestBytes = request.Data.Length, status, route, responseBytes = payload.Length });
                return true;
            case ForeverBattlenetRelay.ChangeRealmTicketOpcode:
                var (token, secret) = ForeverBattlenetRelay.ReadChangeRealmTicket(frame.Payload);
                if (realmState.ClientSecret != null) CryptographicOperations.ZeroMemory(realmState.ClientSecret);
                realmState.ClientSecret = secret; realmState.TicketGranted = true;
                Send(stream, crypt, ForeverBattlenetRelay.ChangeRealmTicketResponseOpcode, ForeverBattlenetRelay.ChangeRealmTicketResponse(token));
                Emit("world_change_realm_ticket", new { connection, token, secretBytes = secret.Length, allowed = true });
                return true;
            default: return false;
        }
    }

    private static bool HandleGlueData(NetworkStream stream, ForeverPacketCrypt crypt, int connection, ForeverWorldFrame frame)
    {
        var hotfixes = ForeverHotfixStore.Current;
        switch (frame.Opcode)
        {
            case ForeverHotfixStore.HotfixRequestOpcode:
                var requested = ForeverHotfixStore.ReadHotfixRequest(frame.Payload);
                var bytes = 0;
                foreach (var connect in hotfixes.HotfixConnectPackets(requested)) { Send(stream, crypt, ForeverHotfixStore.HotfixConnectOpcode, connect); bytes += connect.Length; }
                Emit("world_hotfix_request", new { connection, requested = requested.Length, responseBytes = bytes });
                return true;
            case ForeverHotfixStore.DbQueryBulkOpcode:
                var (table, ids) = ForeverHotfixStore.ReadDbQueryBulk(frame.Payload);
                var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var id in ids) Send(stream, crypt, ForeverHotfixStore.DbReplyOpcode, hotfixes.DbReply(table, id, now));
                Emit("world_db_query", new { connection, table = $"0x{table:X8}", records = ids.Length });
                return true;
            case ForeverAccountData.RequestOpcode:
                Send(stream, crypt, ForeverAccountData.UpdateAccountDataOpcode, ForeverAccountData.Current.Request(frame.Payload));
                return true;
            case ForeverAccountData.UpdateOpcode:
                Send(stream, crypt, ForeverAccountData.UpdateCompleteOpcode, ForeverAccountData.Current.Update(frame.Payload));
                return true;
            default: return false;
        }
    }

    // Sandbox dot commands typed in chat (.fly, .speed, .help); other chat is ignored. Only the command name length is logged.
    private static bool HandleChat(NetworkStream stream, ForeverPacketCrypt crypt, int connection, ForeverWorldFrame frame)
    {
        if (frame.Opcode < ForeverChat.FirstChatMessageOpcode || frame.Opcode > ForeverChat.LastChatMessageOpcode || !ForeverChat.TryReadMessage(frame.Payload, out var text)) return false;
        var player = ForeverWorldPlayer.Current;
        var reply = ForeverChat.HandleCommand(text, player);
        if (reply == null) return false;
        player?.Save();
        Send(stream, crypt, ForeverChat.ChatOpcode, ForeverChat.SystemMessage(reply));
        Emit("world_chat_command", new { connection, opcode = $"0x{frame.Opcode:X8}", commandLength = text.Split(' ')[0].Length, inWorld = ForeverWorldPlayer.Current != null });
        return true;
    }

    private static (long waited, bool? signal) WaitForRealmKey()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (RealmKeySignalPath == null) { Thread.Sleep(EncryptDelayMilliseconds); return (clock.ElapsedMilliseconds, null); }
        while (clock.ElapsedMilliseconds < EncryptDelayMilliseconds)
        {
            if (System.IO.File.Exists(RealmKeySignalPath)) { Thread.Sleep(250); return (clock.ElapsedMilliseconds, true); }   // margin for the client's resume
            Thread.Sleep(50);
        }
        return (clock.ElapsedMilliseconds, false);
    }

    // The instance socket also sends realm-connection login packets here: the crypt (per connection) serializes counters and writes.
    private static void Send(NetworkStream stream, ForeverPacketCrypt crypt, uint opcode, byte[] payload) { lock (crypt) stream.Write(ForeverWorldWire.Seal(crypt, opcode, payload)); }
    private static void Emit(string kind, object metadata) => global::AuthServer.LocalAuthDiagnostics.Emit(kind, metadata);
}
