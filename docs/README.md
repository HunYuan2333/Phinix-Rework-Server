# Server documentation index

This repository is the dedicated server host and server extensions. Its current checkout is `release/legacy-defaults` at `360e716c78b1f39143291b2753f6300a2740e04a`; read this branch snapshot rather than inferring its contents from `main` or `dev`. The local `main` and `origin/main` are merge commit `207f17d`, which contains this exact release commit; the server worktree is pinned to Shared commit `67f243d9edced77dc3fe669f8a435b1f14b59199`. `dev` points to a separate later docs commit / origin state, so do not assume this checkout equals dev.

## Current operator and maintainer references

- [Repository overview and build](../README.md) — source acquisition, build, test, and container workflow.
- [Deployment and release status](../README.md) — UDP defaults, Docker image, publishing workflow.
- [Trade configuration](Server-Trade-Configuration.md) — defaults, generated config, original protocol adapters.
- [Trade persistence and legacy delivery](Server-Trade-Persistence-and-Legacy-Delivery.md) — recovery and delivery boundaries.
- [Protocol and identity TODO](Server-Modern-Identity-TODO.md) — current UDP/ClientKey path and deferred migration.
- [Inventory](Inventory.md), [design philosophy](Design-Philosophy.md), [compatibility boundaries](Compatibility-Boundaries.md).
- [Upgrade roadmap](Server-Upgrade-Roadmap.md) — status labels distinguish implementation, automation, manual acceptance, deployment, and release.

## Evidence and historical records

Dated `Server-Upgrade-*`, `Server-P*`, assessment, audit, and hotfix files are evidence snapshots, not standalone current instructions. They are retained because they record migration inputs, test scope, and acceptance gaps. Use the roadmap and the current configuration/runtime docs above for present behavior. A claimed test/deployment result applies only to the commit and environment named in that record.
