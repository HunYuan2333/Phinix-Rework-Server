using System;
using System.Collections.Generic;
using System.Linq;
using ServerRuntime;

internal static class ServerSessionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        bool live = true; int events = 0; string logs = "";
        var registry = new ServerSessionRegistry((_, _, _) => live, (text, _) => logs += text);
        registry.SessionReady += (_, _) => throw new Exception("synthetic-private-marker");
        registry.SessionReady += (_, _) => events++;
        var offered = new List<string> { "b", "a", "unsupported" };
        Check(!registry.TryBind("c", "p", "s", 999, offered, offered, out _, out var reason) && reason == SessionBindFailure.UnsupportedVersion,
            "Unsupported version accepted");
        Check(registry.TryBind("c", "p", "s", 2, offered, new[] { "a", "b" }, out var first, out _), "Authenticated bind failed");
        offered.Add("mutated-after-bind");
        Check(first.Capabilities.SequenceEqual(new[] { "a", "b" }) && !registry.TryGetReady("c", out _), "Bind leaked mutable caps or prematurely became Ready");
        Check(registry.TryBind("c", "p", "s", 2, new[] { "UNSUPPORTED", "B", "a", "a" }, new[] { "a", "b" }, out var replay, out _) && ReferenceEquals(first, replay),
            "Equivalent repeated Hello changed epoch");
        Check(!registry.TryBind("c", "other", "s", 2, new[] { "a", "b", "unsupported" }, new[] { "a", "b" }, out _, out _), "Principal rebind accepted");
        Check(!registry.TryBind("c", "p", "changed-session", 2, new[] { "a", "b", "unsupported" }, new[] { "a", "b" }, out _, out _), "Session rebind accepted");
        Check(!registry.TryBind("c", "p", "s", 2, new[] { "a" }, new[] { "a", "b" }, out _, out _), "Capabilities overwritten");
        Check(registry.MarkReady(first) && registry.MarkReady(first) && events == 1, "Duplicate Ready or subscriber exception interrupted later subscribers");
        Check(!logs.Contains("synthetic-private-marker") && registry.MatchesReady("c", "p", "s"), "Secret log or missing bound authorization");
        live = false;
        Check(!registry.TryGetReady("c", out _) && !registry.MatchesReady("c", "p", "s"), "Expired subject retained inbound/outbound rights");
        live = true;
        Check(!registry.TryGetReady("c", out _) && !registry.MarkReady(first), "Rejected epoch silently revived");
        registry.Remove("c");
        Check(registry.TryBind("c", "p", "new-session", 2, new[] { "a" }, new[] { "a" }, out var next, out _) && next.Epoch != first.Epoch,
            "Reconnect did not create new epoch");
        Check(!registry.MarkReady(first) && registry.MarkReady(next) && events == 2, "Old epoch published Ready after reconnect");
        Check(!registry.TryBind("huge", "p", "s", 2, Enumerable.Range(0, 513).Select(i => "c" + i), new[] { "a" }, out _, out _), "Unbounded capabilities accepted");
        Check(!registry.TryBind("invalid", "p", "s", 2, new[] { "" }, new[] { "a" }, out _, out _), "Blank capability accepted");
        Check(registry.TryBind("waiting", "p", "s", 2, new[] { "a" }, new[] { "a" }, out var waiting, out _), "Pending bind failed");
        live = false;
        Check(!registry.MarkReady(waiting), "Expired negotiation became Ready");
        live = true;
        Check(!registry.MarkReady(waiting), "Expired negotiation silently revived");
        Console.WriteLine("SESSION RESULT passed; binding/epoch/immutable-caps/Ready-once/revocation/reconnect/subscriber-isolation");
    }
}
