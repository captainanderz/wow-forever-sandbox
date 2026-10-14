using Framework.Constants.Misc;
using Framework.Database.Auth.Entities;
using Framework.Logging;
using Framework.Misc;
using System.Collections.Concurrent;

namespace AuthServer.Managers
{
    class RealmManager : Singleton<RealmManager>
    {
        public bool IsInitialized { get; private set; }
        public readonly ConcurrentDictionary<int, Realm> RealmList;

        RealmManager()
        {
            IsInitialized = false;

            RealmList = new ConcurrentDictionary<int, Realm>();

            var realm = new Realm
            {
                Id     = 1,
                Name   = "Forever Sandbox",
                IP     = "127.0.0.1",
                Port   = 8100,
                Type   = 0,
                Status = 1,
                Flags  = 0,
            };

            if (RealmList.TryAdd(realm.Id, realm))
                Log.Message(LogType.Debug, "Added Realm (Id: {0}, Name: {1})", realm.Id, realm.Name);
        }
    }
}
