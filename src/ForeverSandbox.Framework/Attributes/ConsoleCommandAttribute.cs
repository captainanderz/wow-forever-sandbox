using System;

namespace Framework.Attributes
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class ConsoleCommandAttribute : Attribute
    {
        public string Command { get; set; }
        public string Description { get; set; }

        public ConsoleCommandAttribute(string command, string description)
        {
            Command = command.ToLower();
            Description = description;
        }
    }
}
