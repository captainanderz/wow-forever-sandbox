using Framework.Constants.Net;
using Framework.Network.Packets;
using AuthServer.Network;

namespace AuthServer.Game.Packets.PacketHandler
{
    public class TutorialHandler
    {
        public static void HandleTutorialFlags(ref WorldClass session)
        {
            PacketWriter tutorialFlags = new PacketWriter(ServerMessage.TutorialFlags);
            for (int i = 0; i < 32; i++)
                tutorialFlags.WriteUInt8(0);

            session.Send(ref tutorialFlags);
        }
    }
}
