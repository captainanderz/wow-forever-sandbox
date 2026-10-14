using Framework.Network.Packets;
using AuthServer.Network;
using AuthServer.WorldServer.Managers;
using Framework.Constants.Net;
using Framework.Logging;

namespace AuthServer.Game.Packets.PacketHandler
{
    public class TimeHandler : Manager
    {
        public static void HandleReadyForAccountDataTimes(ref PacketReader packet, WorldClass session)
        {
            Log.Message(Framework.Constants.Misc.LogType.Error, "ReadyForAccountDataTimes!!!");
            //WorldMgr.WriteAccountDataTimes(AccountDataMasks.GlobalCacheMask, ref session);
        }
        //[Opcode2(ClientMessage.ReadyForAccountDataTimes, "18156")]
        public static void HandleReadyForAccountDataTimes(ref PacketReader packet, WorldClass2 session)
        {
            Log.Message(Framework.Constants.Misc.LogType.Error, "ReadyForAccountDataTimes!!!");
            //WorldMgr.WriteAccountDataTimes(AccountDataMasks.GlobalCacheMask, ref session);
        }
        [Opcode(ClientMessage.UITimeRequest, "18156")]
        public static void HandleUITimeRequest(ref PacketReader packet, WorldClass session)
        {
            PacketWriter uiTime = new PacketWriter(ServerMessage.UITime);

            uiTime.WriteUnixTime();

            session.Send(ref uiTime);
        }

        public static void HandleLoginSetTimeSpeed(ref WorldClass session)
        {
            PacketWriter loginSetTimeSpeed = new PacketWriter(ServerMessage.LoginSetTimeSpeed);

            loginSetTimeSpeed.WritePackedTime();
            loginSetTimeSpeed.WritePackedTime();
            loginSetTimeSpeed.WriteFloat(0.01666667f);
            loginSetTimeSpeed.WriteInt32(0);
            loginSetTimeSpeed.WriteInt32(0);

            session.Send(ref loginSetTimeSpeed);
        }

        public static void HandleSetTimezoneInformation(ref WorldClass session)
        {
            PacketWriter setTimezoneInformation = new PacketWriter(ServerMessage.SetTimeZoneInformation);
            BitPack BitPack = new BitPack(setTimezoneInformation);

            var timeZone = "Etc/UTC";

            BitPack.Write(timeZone.Length, 7);
            BitPack.Write(timeZone.Length, 7);

            BitPack.Flush();

            setTimezoneInformation.WriteString(timeZone);
            setTimezoneInformation.WriteString(timeZone);

            session.Send(ref setTimezoneInformation);
        }
    }
}
