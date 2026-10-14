namespace AuthServer.Managers
{
    class Manager
    {
        public static ComponentManager Components;
        public static ModuleManager Modules;
        public static RealmManager Realms;

        public static void Initialize()
        {
            Components = ComponentManager.GetInstance();
            Modules = ModuleManager.GetInstance();
            Realms  = RealmManager.GetInstance();
        }
    }
}
