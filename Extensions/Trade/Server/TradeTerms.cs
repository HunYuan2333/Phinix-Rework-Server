namespace Phinix.TradeExtension.Server;

// Business terms, selected by a server-side adapter, never by packet metadata.
// The default remains full payloads with consent bound to an observed quote.
public enum TradeNegotiationTerms
{
    ObservedQuote = 0,
    BasicItemsCurrentQuote = 1
}
