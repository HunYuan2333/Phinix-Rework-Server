using System;
using System.Collections.Generic;
using Utils.Framework;

namespace Phinix.TradeExtension.Server
{
    // Wire boundary only: historical Rework inline items and packet references become
    // one complete batch. No mutation, consumption, protocol retry or success inference.
    internal static class LegacyTradeOfferAdapter
    {
        internal static bool TryTranslate(FrameworkTradeOfferUpdateRequest request,
            Func<string, FrameworkItemPayload> resolveReference, Func<FrameworkItemPayload, bool> valid,
            out List<FrameworkItemPayload> items)
        {
            items = null;
            if (request == null) return false;
            var translated = new List<FrameworkItemPayload>();
            if (request.ItemPacketRefs?.Count > 0)
            {
                if (request.Items?.Count > 0 || resolveReference == null) return false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string reference in request.ItemPacketRefs)
                {
                    if (string.IsNullOrEmpty(reference) || !seen.Add(reference)) return false;
                    var item = resolveReference(reference);
                    if (!valid(item)) return false;
                    translated.Add(item);
                }
            }
            else
            {
                // Absence is the historical empty-offer representation, not permission
                // to filter malformed entries out of a nonempty inline batch.
                foreach (var item in request.Items ?? new List<FrameworkItemPayload>())
                {
                    if (!valid(item)) return false;
                    translated.Add(item);
                }
            }
            items = translated;
            return true;
        }
    }
}
