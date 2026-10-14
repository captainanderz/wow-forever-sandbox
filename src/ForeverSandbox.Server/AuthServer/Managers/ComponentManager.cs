using Framework.Database.Auth.Entities;
using Framework.Misc;
using System.Collections.Generic;

namespace AuthServer.Managers
{
    class ComponentManager : Singleton<ComponentManager>
    {
        public readonly List<Component> ComponentList;

        ComponentManager()
        {
            ComponentList = new List<Component>();

            UpdateComponents();
        }

        void UpdateComponents()
        {
            ComponentList.Add(new Component
            {
                Program = "Bnet",
                Platform = "Win",
                Build = 37165
            });

            ComponentList.Add(new Component
            {
                Program = "Bnet",
                Platform = "Wn64",
                Build = 37165
            });

            ComponentList.Add(new Component
            {
                Program = "Bnet",
                Platform = "Mc64",
                Build = 37165
            });

            // WoW
            ComponentList.Add(new Component
            {
                Program = "WoW",
                Platform = "base",
                Build = 20365
            });

            ComponentList.Add(new Component
            {
                Program = "WoW",
                Platform = "enUS",
                Build = 20740
            });

            ComponentList.Add(new Component
            {
                Program = "WoW",
                Platform = "enGB",
                Build = 20740
            });

            ComponentList.Add(new Component
            {
                Program = "WoW",
                Platform = "ruRU",
                Build = 20740
            });

            ComponentList.Add(new Component
            {
                Program = "WoW",
                Platform = "deDE",
                Build = 20740
            });

            ComponentList.Add(new Component
            {
                Program = "WoW",
                Platform = "Win",
                Build = 20740
            });

            ComponentList.Add(new Component
            {
                Program = "WoW",
                Platform = "Wn64",
                Build = 20740
            });

            ComponentList.Add(new Component
            {
                Program = "WoWB",
                Platform = "Win",
                Build = 20740
            });

            ComponentList.Add(new Component
            {
                Program = "WoWB",
                Platform = "Wn64",
                Build = 20740
            });

            ComponentList.Add(new Component
            {
                Program = "WoWB",
                Platform = "Mc64",
                Build = 20740
            });

        }
    }
}
