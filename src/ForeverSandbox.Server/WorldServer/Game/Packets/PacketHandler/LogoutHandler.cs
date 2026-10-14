using Framework.Network.Packets;
using AuthServer.Network;
using AuthServer.WorldServer.Managers;
using Framework.Constants.Net;
using System.Linq;

namespace AuthServer.Game.Packets.PacketHandler
{
    public class LogoutHandler : Manager
    {
        [Opcode2(ClientMessage.CliLogoutRequest, "17658")]
        public static void HandleLogoutRequest(ref PacketReader packet, WorldClass2 session)
        {
            var pChar = session.Character;

            WorldMgr.waitAdd = true;
           // pChar.InRangeObjects.Clear();

            ObjectMgr.SavePositionToDB(pChar);

            PacketWriter logoutComplete = new PacketWriter(ServerMessage.LogoutComplete);

            //logoutComplete.WriteSmartGuid(pChar.Guid);


           
            var sess1 = WorldMgr.GetSession(pChar.Guid);
            //var pkt = ObjectHandler.HandleDestroyObject(ref sess1, pChar.Guid);
            // Destroy object after logout
            // sess1.Send(ref logoutComplete);
            WorldMgr.WriteOutOfRangeObjects(SpawnMgr.GetOutOfRangeCreatures(pChar), WorldMgr.Sessions.ToList()[0].Value);

            WorldMgr.DeleteSession(pChar.Guid);
            //WorldMgr.DeleteSession2(pChar.Guid);

            session.Send(ref logoutComplete);

        }
    }
}
