using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Framework.Logging;
using Framework.Constants.Misc;

namespace AuthServer.Network
{
    public class WorldNetwork
    {
        public volatile bool listenSocket = true;
        internal bool ForeverHandshakeProbe;
        internal bool ForeverWorldSession;
        TcpListener listener;

        public bool Start(string host, int port)
        {
            try
            {
                listener = new TcpListener(IPAddress.Parse(host), port);
                listener.Start();

                return true;
            }
            catch (Exception e)
            {
                Log.Message(LogType.Error, "{0}", e.Message);
                Log.Message();

                return false;
            }
        }

        public void AcceptConnectionThread()
        {
            new Thread(AcceptConnection).Start();
        }
 
        async void AcceptConnection()
        {
            while (listenSocket)
            {
                Thread.Sleep(1);

                if (listener.Pending())
                {
                    if (ForeverHandshakeProbe || ForeverWorldSession)
                    {
                        var ownedSocket = await listener.AcceptSocketAsync();
                        ThreadPool.QueueUserWorkItem(_ => new ForeverWorldHandshake(ownedSocket, ForeverWorldSession).Accept());
                        continue;
                    }

                   // Console.WriteLine("Accept");
                    WorldClass worldClient = new WorldClass();
                    worldClient.clientSocket = await listener.AcceptSocketAsync();
                    worldClient.clientSocket.Blocking = false;
                    worldClient.clientSocket.NoDelay = true;

                    worldClient.OnConnect();
                }
            }
        }

        public void AcceptConnectionThread2()
        {
            new Thread(AcceptConnection2).Start();
        }

        async void AcceptConnection2()
        {
            while (listenSocket)
            {
                Thread.Sleep(1);

                if (listener.Pending())
                {
                    if (ForeverWorldSession)
                    {
                        var ownedSocket = await listener.AcceptSocketAsync();
                        ThreadPool.QueueUserWorkItem(_ => new ForeverInstanceSocket(ownedSocket).Accept());
                        continue;
                    }

                    WorldClass2 worldClient = new WorldClass2();
                    worldClient.clientSocket = await listener.AcceptSocketAsync();

                    worldClient.clientSocket.Blocking = false;
                    worldClient.clientSocket.NoDelay = true;

                    worldClient.OnConnect();
                }
            }
        }
    }
}
