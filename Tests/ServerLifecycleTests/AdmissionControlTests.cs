using System;
using System.IO;
using ServerRuntime;
using Utils.Framework;

internal static class AdmissionControlTests
{
    public static void Run()
    {
        string fixture = "Tests/Fixtures/FrameworkControl/normalized.json";
        var packet = FrameworkSerialization.DeserializePacket(File.ReadAllBytes(fixture));
        Verify(packet, "unsupported_version", "Negotiation", "fixture-hello");
        using var provenance = System.Text.Json.JsonDocument.Parse(File.ReadAllText("Tests/Fixtures/FrameworkControl/provenance.json"));
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fixture))).ToLowerInvariant();
        if (hash != provenance.RootElement.GetProperty("fixtureSha256").GetString()) throw new Exception("Control fixture hash mismatch");
        Console.WriteLine("CONTROL FIXTURE RESULT passed; synthetic admission control is not a business result");
    }
    public static void WriteFixture(string path)
    {
        var packet = new FrameworkPacket { MessageId = "fixture-control-response", Kind = "control", MessageType = "framework.admission.rejected", SessionId = "fixture-session",
            SenderUuid = FrameworkProtocol.SystemSenderUuid, PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkAdmissionRejection
            { Code = "unsupported_version", RequestMessageId = "fixture-hello", CorrelationId = "fixture-hello", Stage = "Negotiation" }) };
        packet.SetCorrelationId("fixture-hello");
        File.WriteAllBytes(path, FrameworkSerialization.SerializePacket(packet));
    }
    public static void Verify(FrameworkPacket packet, string code, string stage, string request)
    {
        var rejection = FrameworkSerialization.DeserializePayload<FrameworkAdmissionRejection>(packet.PayloadJson);
        if (packet.Kind != "control" || packet.MessageType != "framework.admission.rejected" || rejection.Code != code || rejection.Stage != stage ||
            rejection.RequestMessageId != request || rejection.CorrelationId != packet.GetCorrelationId() || rejection.OperationOutcome != "Unknown" || rejection.Retryable || rejection.RequestInvoked)
            throw new Exception("Invalid correlated admission refusal or forged operation outcome");
    }
}
