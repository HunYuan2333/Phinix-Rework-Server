using Trading;
using Utils;

namespace PhinixServer.Framework
{
    internal sealed class LegacyServerTradeRuntimeAdapter : ILegacyTradeRuntime
    {
        private readonly ServerTrading trading;

        public LegacyServerTradeRuntimeAdapter(ServerTrading trading)
        {
            this.trading = trading;
        }

        public event System.EventHandler<LogEventArgs> OnLogEntry
        {
            add => trading.OnLogEntry += value;
            remove => trading.OnLogEntry -= value;
        }

        public void RaiseLogEntry(LogEventArgs e)
        {
            trading.RaiseLogEntry(e);
        }

        public void Save(string path)
        {
            trading.Save(path);
        }

        public void Load(string path)
        {
            trading.Load(path);
        }
    }
}
