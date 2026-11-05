using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerReferenceModel;

// Specification oracle: no product/adapter/serializer assembly is referenced.
// Only the P0 active-offer subset is modeled. Completion, persistence and ACK are P3 work.
public enum StepKind { Cache, Offer, AcceptFirstParty, Reconnect }
public sealed record Step(StepKind Kind, int Actor, int Reference = 0, int Asset = 0,
                          int[] References = null, bool TargetExists = true);

public sealed class OfferModel
{
    private readonly Dictionary<(int Owner, int Session, int Ref), int> available = new();
    public int[] Sessions { get; } = new int[3];
    public int[][] Offers { get; } = new[] { Array.Empty<int>(), Array.Empty<int>() };
    public bool[] Accepted { get; } = new bool[2];
    public long Version { get; private set; } = 1;

    public bool? Apply(Step step)
    {
        switch (step.Kind)
        {
            case StepKind.Reconnect:
                Sessions[step.Actor]++;
                return null;
            case StepKind.Cache:
                var key = (step.Actor, Sessions[step.Actor], step.Reference);
                if (available.TryGetValue(key, out int prior)) return prior == step.Asset;
                if (available.Count == 200) return false;
                available.Add(key, step.Asset);
                return true;
            case StepKind.AcceptFirstParty:
                if (!step.TargetExists || step.Actor != 0) return false;
                Accepted[0] = true;
                Version++;
                return true;
            case StepKind.Offer:
                if (!step.TargetExists || step.Actor > 1) return false;
                int[] refs = step.References ?? Array.Empty<int>();
                // Validate the whole request against availability before transitioning rights.
                if (refs.Any(id => id < 0) || refs.Distinct().Count() != refs.Length ||
                    refs.Any(id => !available.ContainsKey((step.Actor, Sessions[step.Actor], id)))) return false;
                Offers[step.Actor] = refs.Select(id => available[(step.Actor, Sessions[step.Actor], id)]).ToArray();
                foreach (int id in refs) available.Remove((step.Actor, Sessions[step.Actor], id));
                Array.Fill(Accepted, false);
                Version++;
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }
    }
}
