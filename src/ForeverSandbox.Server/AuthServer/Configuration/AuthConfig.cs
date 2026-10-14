using Framework.Constants.Misc;
using Framework.Logging;

namespace AuthServer.Configuration
{
    class AuthConfig
    {
        #region Config Options
        public static LogType LogLevel;
        public static string BindIP;
        public static int BindPort;
        #endregion

        public static void ReadConfig()
        {
#if !PUBLIC
            LogLevel =  LogType.Init | LogType.Normal | LogType.Error | LogType.Debug | LogType.Info;
            LogLevel =  (LogType)0xFF; //LogType.Init | LogType.Normal | LogType.Error | LogType.Debug | LogType.Info;
#else
            LogLevel = LogType.Init | LogType.Normal | LogType.Error | LogType.Debug | LogType.Info;
#endif
            BindIP = "127.0.0.1";
            BindPort = 8000;

            Log.Initialize(LogLevel, null);
        }
    }
}
