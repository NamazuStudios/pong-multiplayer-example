
namespace Elements.Crossfire
{
    public class Logger
    {
        public static LoggerLevel LogLevel;

        private string className;

        public Logger(string className)
        {
            this.className = className;
        }

        public void Log(string message)
        {
            if (LogLevel == LoggerLevel.Debug)
                UnityEngine.Debug.Log($"[{className}] {message}");
        }

        public void LogWarning(string message)
        {
            if (LogLevel <= LoggerLevel.Warning)
                UnityEngine.Debug.LogWarning($"[{className}] {message}");
        }

        public void LogError(string message)
        {
            if (LogLevel <= LoggerLevel.Error)
                UnityEngine.Debug.LogError($"[{className}] {message}");
        }
    }
    
    public enum LoggerLevel
    {
        Debug = 0,
        Warning = 1,
        Error = 2
    }
}