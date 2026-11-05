using System;
using System.Collections.Generic;
using Utils.Framework;

namespace Phinix.TradeExtension.Server;

public interface ITradeBasicItemProjection
{
    FrameworkItemPayload Project(FrameworkItemPayload item);
}

// Additive API: existing plugins cannot accidentally opt in through the wire API.
public interface ITradeNegotiationApi
{
    void HandleCreateWithTerms(FrameworkPacket command, ServerFrameworkContext context, TradeNegotiationTerms terms);
}

public sealed partial class PhinixFrameworkTradeServerService : ITradeNegotiationApi
{
    private readonly ITradeBasicItemProjection basicItemProjection;

    public PhinixFrameworkTradeServerService(UserManagement.IServerUserManager users,
        ServerRuntime.IServerSessionReadiness readiness, ServerRuntime.IServerWorkQueue queue,
        TradeServerConfig config, ITradeBasicItemProjection projection) : this(users, readiness, queue, config)
    {
        basicItemProjection = projection ?? throw new ArgumentNullException(nameof(projection));
    }

    private static string ItemFingerprint(FrameworkItemPayload item) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(FrameworkSerialization.SerializePayload(item))));

    private static bool MatchesReplayedItem(TradeOperationResult result, int index, FrameworkItemPayload item)
    {
        if (result.SourceItemFingerprints != null)
            return index < result.SourceItemFingerprints.Count && ItemFingerprint(item) == result.SourceItemFingerprints[index];
        return result.Items != null && index < result.Items.Count &&
            FrameworkSerialization.SerializePayload(item) == FrameworkSerialization.SerializePayload(result.Items[index]);
    }

    private List<FrameworkItemPayload> ProjectOffer(string tradeId, List<FrameworkItemPayload> items)
    {
        if (store.GetTerms(tradeId) != TradeNegotiationTerms.BasicItemsCurrentQuote) return items;
        var projection = System.Threading.Volatile.Read(ref endpointBinding)?.Projection ?? basicItemProjection;
        if (projection == null) throw new System.IO.InvalidDataException("BasicItemProjectionUnavailable");
        var projected = new List<FrameworkItemPayload>(items.Count);
        foreach (var item in items)
        {
            var result = projection.Project(item);
            if (!IsValidItem(result) || !FitsRawItemBudget(result))
                throw new System.IO.InvalidDataException("BasicItemProjectionInvalid");
            projected.Add(result);
        }
        return projected;
    }
}
