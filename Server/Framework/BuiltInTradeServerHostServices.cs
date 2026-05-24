namespace PhinixServer.Framework
{
    public sealed class BuiltInTradeServerHostServices
    {
        public BuiltInTradeServerHostServices(PhinixFrameworkTradeServerService tradeService)
        {
            TradeService = tradeService;
        }

        public PhinixFrameworkTradeServerService TradeService { get; }
    }
}
