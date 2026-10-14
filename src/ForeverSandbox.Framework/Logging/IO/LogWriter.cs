using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Framework.Logging.IO
{
    public class LogWriter : IDisposable
    {
        public string LogFile { get; set; }
        FileStream logStream;

        public LogWriter(string directory, string file)
        {
            LogFile = string.Format("{0}/{1}_{2}", directory, DateTime.Now.ToString("yyyy-MM-dd_hh-mm-ss"), file);

            logStream = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, true);
        }

        public async Task Write(string logMessage)
        {
            var logBytes = Encoding.Unicode.GetBytes(logMessage + "\r\n");

            await logStream.WriteAsync(logBytes, 0, logBytes.Length);
            await logStream.FlushAsync();
        }

        public void Dispose()
        {
            LogFile = "";
            logStream.Dispose();
        }
    }
}
