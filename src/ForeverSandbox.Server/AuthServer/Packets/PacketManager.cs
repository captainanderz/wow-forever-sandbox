using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AuthServer.AuthServer.Attributes;
using AuthServer.Network;
using Bgs.Protocol;
using Bgs.Protocol.Connection.V1;
using Framework.Constants.Misc;
using Framework.Constants.Net;
using Framework.Logging;
using Google.Protobuf;
using static AuthServer.AuthServer.Attributes.BnetServiceBase;

namespace AuthServer.Packets
{
    class PacketManager
    {
        static Dictionary<BnetServiceHash, Dictionary<uint, Tuple<MethodInfo, Type>>> BnetHandlers = new Dictionary<BnetServiceHash, Dictionary<uint, Tuple<MethodInfo, Type>>>();

        public static void Initialize()
        {
            IMessage msg = new ConnectRequest();

            var assembly = Assembly.GetExecutingAssembly();

            foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttribute<BnetServiceAttribute>() != null))
            {
                var methods = new Dictionary<uint, Tuple<MethodInfo, Type>>();

                foreach (var method in type.GetMethods().Where(m => m.GetCustomAttribute<BnetMethodAttribute>() != null))
                {
                    var methodAttributeInfo = method.GetCustomAttribute<BnetMethodAttribute>();

                    methods.Add(methodAttributeInfo.MethodId, Tuple.Create(method, method.GetParameters()[1].ParameterType));
                }

                var serviceAttributeInfo = type.GetCustomAttribute<BnetServiceAttribute>();

                BnetHandlers.Add(serviceAttributeInfo.Hash, methods);
            }

            //InvokeHandler
        }

        public static void InvokeHandler(AuthSession session, Header header, byte[] mesageData)
        {
            session.RequestToken = header.Token;
            var v2 = header.ServiceHash == BgsAuthenticationV2.ServiceHash;
            var accountV2 = header.ServiceHash == BgsAccountV2.ServiceHash;
            var realmV2 = header.ServiceHash == BgsRealmV2.ServiceHash || header.ServiceHash == BgsRealmV2.NameHash;
            var known = v2 ? global::AuthServer.AuthServer.Packets.BnetHandlers.AuthenticationV2Service.Handles(header.MethodId)
                : accountV2 ? BgsAccountV2.Handles(header.MethodId)
                : realmV2 ? BgsRealmV2.Handles(header.MethodId)
                : BnetHandlers.TryGetValue((BnetServiceHash)header.ServiceHash, out var registered) && registered.ContainsKey(header.MethodId);
            global::AuthServer.LocalAuthDiagnostics.Emit("bgs_request", new { connection = session.DiagnosticConnectionId, serviceHash = $"0x{header.ServiceHash:X8}", methodId = header.MethodId, requestToken = header.Token, payloadBytes = mesageData.Length, knownHandler = known });
            if (v2) { global::AuthServer.AuthServer.Packets.BnetHandlers.AuthenticationV2Service.Handle(session, header.MethodId, mesageData); return; }
            if (accountV2) { global::AuthServer.AuthServer.Packets.BnetHandlers.AccountV2Service.Handle(session, header.MethodId, mesageData); return; }
            if (realmV2) { global::AuthServer.AuthServer.Packets.BnetHandlers.RealmV2Service.Handle(session, header.MethodId, mesageData); return; }
            Log.Message(LogType.Debug, $"Got  method id for '{header.MethodId}' for '{(BnetServiceHash)header.ServiceHash}'");
            Dictionary<uint, Tuple<MethodInfo, Type>> data;

            if (BnetHandlers.TryGetValue((BnetServiceHash)header.ServiceHash, out data))
            {
                Tuple<MethodInfo, Type> method;

                if (data.TryGetValue(header.MethodId, out method))
                {
                    var handlerObj = Activator.CreateInstance(method.Item2) as IMessage;

                    handlerObj.MergeFrom(mesageData);

                    //Console.WriteLine("{0}: {1}", header.MethodId + "/" + (BnetServiceHash)header.ServiceHash, handlerObj.ToString());
                    //Console.WriteLine();
                    //Console.WriteLine();

                    try
                    {
                        method.Item1.Invoke(null, new object[] { session, handlerObj });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.InnerException.StackTrace);
                    }
                }
                else
                    Log.Message(LogType.Error, $"Got unhandled method id for '{header.MethodId}' for '{(BnetServiceHash)header.ServiceHash}'");
            }
            else
            {

                if (!Enum.IsDefined(typeof(BnetServiceHash), header.ServiceHash))
                    Console.WriteLine($"Got unknown Bnet service 0x{header.ServiceHash:X8}, method {header.MethodId}");
                else
                    Console.WriteLine($"Got unhandled Bnet service 0x{header.ServiceHash:X8}, method {header.MethodId}");
            }
        }
    }
}
