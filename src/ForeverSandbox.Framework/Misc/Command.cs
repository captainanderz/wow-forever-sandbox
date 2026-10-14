using System;
using System.Globalization;
using Framework.Constants.Misc;
using Framework.Logging;

namespace Framework.Misc
{
    public class Command
    {
        public static T Read<T>(string[] args, int index)
        {
            try
            {
                return (T)Convert.ChangeType(args[index], typeof(T), CultureInfo.GetCultureInfo("en-US").NumberFormat);
            }
            catch
            {
                Log.Message(LogType.Error, "Wrong arguments for the current command.");
            }

            return default(T);
        }
    }
}
