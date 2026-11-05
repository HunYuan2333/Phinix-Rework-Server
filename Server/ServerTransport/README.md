# Neutral TLS packet channel (SV-40 pilot)

`TlsPacketChannel` authenticates and owns an explicitly provided stream, then carries opaque packets through a bounded four-byte big-endian length prefix. It has no dependency on authentication, users, Chat, Trade or legacy wire types.

This component is not yet integrated into the production host. It must never be presented as encrypting the current LiteNetLib UDP listener. An eventual modern transport adapter must carry every modern packet through it and preserve the original operation/result semantics on disconnect or cancellation.

Client trust requires a provisioned server name, normal certificate policy and an independently trusted SPKI pin. No TOFU, insecure callback or plaintext fallback is supplied. Default revocation checking is online. The optional private CA is pretrusted configuration; synthetic tests explicitly use NoCheck because their ephemeral CA has no revocation service.

One read and one write may be active concurrently. A second same-direction operation is refused without queuing. Any interruption after acquiring a direction closes the channel; a stream write completion is not an authoritative business receipt. Dispose closes the owned transport; it does not dispose the borrowed server certificate or trust root.

See `docs/Server-Upgrade-P4-Transport-Pilot-2026-10-23.md` for evidence, protocol bounds and unverified game-runtime/deployment scope. Run `Tests/ServerSecureTransportTests` for real loopback TLS checks with in-memory synthetic certificates only.
