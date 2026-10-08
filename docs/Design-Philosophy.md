# Phinix Rework — Design Philosophy

> **Target audience: code maintainers and AI agents**
>
> **中文版**：[设计哲学.md](./设计哲学.md)
>
> **Compatibility boundaries and recovery contracts (Chinese)**: [Compatibility-Boundaries.md](./Compatibility-Boundaries.md)
>
> **Last updated**: 2026-09-13, updated based on latest codebase audit (responsive UI, extension manager with dependency graph, Item pipeline P0 completion, physical assembly separation, and .NET 10 / RimWorld 1.6 CI alignment).

---

## 1. Core Principles

### 1.1 Plugin Equality

Chat and Trade have the same status as third-party submods. There are no "official super-citizens":

- Same discovery path (reflection scan `IPhinixExtensionModule`)
- Same registration path (`Register(builder)` → `RegisterApi<T>()` / `AddClientMessageHandler()`)
- Same activation path (`Activate(hostContext)` → `Shutdown(hostContext)`)

**Anti-pattern**: The host reserves dedicated interface resolution, dedicated startup branches, or dedicated UI entry points for a specific plugin.

### 1.2 Host Does Not Depend on Plugins

The host only references `ClientExtensionAbstractions` (the general contract layer), and does not reference any plugin's Contracts project:

- `Client.csproj`'s ProjectReference **must not include** `ChatExtension`, `TradeExtension`, or any plugin project
- The host dynamically collects plugin capabilities through `ResolveExtensionApis<T>()`, without strong type binding
- Plugins can depend on the host, but not the other way around

**Anti-pattern**: `Client.csproj` explicitly references plugin projects; host code contains type references to concrete business interfaces such as `IClientChatService`, `IClientTradeService`.

### 1.3 Host Only Provides General Services

The basic services provided by the host are business-agnostic:

```
Network layer (NetClient)
Extension discovery & lifecycle (PhinixExtensionRegistry, IExtensionActivationPolicy)
General services (IClientSessionContext, IClientSettingsContext, IClientUserDirectory,
           IClientUserEventStream, IClientMainThreadDispatcher, IClientWindowService,
           IClientSoundService, IClientLinkService, IClientEnvironmentService, IClientLocalizationService, IClientExtensionManagementWindowService, IUiTheme, IDisplayMessageSink)
ServerTab (general shell, collects IMainTabProvider / IServerSidebarProvider / INoticeBannerProvider / IUiAcceptKeyHandler for dynamic rendering)
Basic UI (SettingsWindow, CredentialsWindow, ExtensionManagerWindow/ExtensionManagerTab, ExtensionControlSettingsPanelProvider)
```

Business logic (chat, trade, red packets, etc.) is entirely in plugins. The host does not care what businesses currently exist.

---

## 2. Software Engineering Principles

### 2.1 Loose Coupling

Modules interact through interface contracts, not directly depending on concrete implementations:

- Plugins and host are coupled through general interfaces defined in `ClientExtensionAbstractions`, not through concrete types
- Inter-plugin interaction is defined by plugins themselves through interfaces and called directly; the framework neither acts as an intermediary nor prevents it
- Adding a new plugin does not affect host compilation or the operation of existing plugins

**Judgment criterion**: Can a complete business plugin (including Tab, sidebar, badge) be added without modifying the host code?

### 2.2 Layering

The system is divided into clear layers, with upper layers depending on lower layers; reverse dependencies are forbidden:

```
┌─────────────────────────────────────────┐
│  Plugins (Chat, Trade, Third-party)      │  ← Business layer
├─────────────────────────────────────────┤
│  ClientExtensionAbstractions             │  ← Shared contract layer
├─────────────────────────────────────────┤
│  Host (Client)                           │  ← Host layer
│  ├─ Network / Auth / User Management     │
│  ├─ Extension Discovery / Lifecycle      │
│  └─ General UI Shell                     │
├─────────────────────────────────────────┤
│  Common (Utils, Connections, etc.)       │  ← Infrastructure layer
└─────────────────────────────────────────┘
```

- Upper layers can call lower layers; lower layers **must never** reverse-depend on upper layers
- Modules within the same layer should stay as independent as possible, minimizing horizontal coupling
- Each layer only exposes interfaces within its responsibility scope, without leaking internal implementation details

**Anti-pattern**: `Common` compiles `../../Server/*.cs` source files; host code directly accesses plugin-internal types.

### 2.3 Minimize Hardcoding

Anything that may change with business requirements should be abstracted through contracts, not hardcoded in the host:

- Tab content → `IMainTabProvider`, do not hardcode "Chat Tab / Trade Tab"
- Sidebar content → `IServerSidebarProvider`, do not hardcode "User List Panel"
- Badges → `IBadgeProvider`, do not hardcode "Unread Message Count"
- Message handling → `module → Kind → MessageType → handler` dynamic routing, do not hardcode dispatch for specific message types
- Event notification → `IClientUserEventStream`, do not provide dedicated event bridges for specific plugins

**Judgment criterion**: When adding a new business capability (such as mail, announcements, quests), is the host code change count zero?

### 2.4 Defensive Programming and Boundary Checking

All external inputs (network packets, UI inputs, external configs, third-party mod data) are treated as untrusted sources:

- **Centralized boundary validation**: Perform format, length, and range validation at system boundaries (UI controls, deserialization entry points, config loaders)
- **Numerical boundaries and overflow prevention**: Parse numbers using defensive parsing (e.g., `int.TryParse` with `Mathf.Clamp`), preventing integer overflow (such as extremely large values or negative values) from corrupting calculations
- **Internal invariants**: Domain logic should defensively guard against null references, collection index out-of-bounds, and division by zero, without assuming callers or remote peers are well-behaved
- **Fast handling of invalid inputs**: Reject, clamp, or fallback to safe defaults at boundaries; unhandled exceptions must never crash the main game loop or pipeline

**Anti-pattern**: Directly using unchecked casts or `Parse`; assuming remote peers send valid values; letting input exceptions crash the UI or pipeline.

**Judgment criterion**: When receiving extreme values (e.g., `int.MaxValue`) or malformed strings, does the system silently clamp or report errors gracefully without throwing unhandled exceptions?

### 2.5 Lifecycle Symmetry and Re-entrancy Safety

Component lifecycles must correctly respond to game host sessions (loading save, switching save, returning to main menu, reconnecting):

- **Paired cleanup**: Any event subscriptions (`+=`), timers, background threads, or file handles acquired during a session must be symmetrically cleaned up (`-=`, `Dispose`) on session end or module shutdown
- **Stateless across sessions**: Returning to the main menu must thoroughly release static references and caches of game world entities, preventing old world objects from leaking memory
- **Idempotent re-initialization**: Components must support repeated "load game → main menu → reload game" re-entry; singleton setup and event listeners must prevent duplicate bindings

**Anti-pattern**: Retaining game world entities in static dictionaries; failing to unsubscribe from events when exiting to the main menu, causing duplicated event handlers on subsequent loads; relying on game process restart to reset state.

**Judgment criterion**: After 5 consecutive rounds of "load save → return to main menu → load new save", is memory usage stable and are event handlers triggered only once?

The client framework constructor only establishes dependencies; the host explicitly calls `Start()` after preparing its services on the game main thread. Repeated Start is harmless while running, and Stop/Shutdown/Dispose are terminal and idempotent. Registry cleanup also calls module `Shutdown` after partial Register/Activate failure, so cleanup must tolerate incomplete initialization. Consumers stop before providers, module registrations are revoked, and borrowed host services or cross-plugin APIs are never disposed by the consumer. Normal game shutdown runs through Unity’s main-thread quitting event; ProcessExit only releases game-independent host resources.

### 2.6 Principle of Least Intrusion and Ecosystem Isolation

As a plugin running in a complex environment alongside hundreds of other mods, actively minimize disruption to the host environment:

- **Prefer mount points**: Use framework-provided extension points (Tabs, sidebars, handlers, settings) instead of invasive patches (Harmony)
- **Minimal patching**: When patching is unavoidable, adhere to minimal slicing (prefer Prefix/Postfix over IL Transpiler); only hook low-level atomic methods, never high-frequency top-level loops
- **Namespace isolation**: Public DefNames, config keys, and network tags must have unique namespace prefixes (e.g., `Phinix_`) to avoid collisions
- **Do not pollute global state**: Avoid mutating or consuming shared host global state (such as the global RNG stream `Verse.Rand` or global context)

**Anti-pattern**: Carelessly using Transpilers on core game loops; using generic DefNames without prefixes that collide with other mods.

**Judgment criterion**: When loaded in an environment with hundreds of other mods, are there no naming collisions or unexpected mutations of native game behaviors?

---

## 3. Key Design Decisions

### 3.1 Dynamic Tab Mechanism

The communication layer and UI layer use the same dynamic dispatch pattern: plugins register handlers/providers → host discovers and collects through interfaces → routes by sort key (Priority/TabOrder).

- `ServerTab` is a pure container, containing no business UI
- `ServerTabButtonWorker` aggregates all `IBadgeProvider`s, only displaying the first badge with content
- Adding a new Tab/sidebar only requires implementing the corresponding interface and registering it in `Register()`

The host exposes `IClientExtensionManagementWindowService` to every extension. It opens the host-owned management window, reusing `ExtensionManagerTab` as its content. The host does not register a dedicated Extensions main tab; a store extension contributes its own `IMainTabProvider` and invokes the management service. Host mod settings retain a recovery entry even if that extension is absent or disabled. Enable/disable changes retain the existing restart-required policy for all discovered extensions, including built-ins.

### 3.2 Three Communication Pipeline Categories

Framework communication is divided into three main pipelines, each with its own responsibility:

| Pipeline | Responsibility | Example | Current Status |
|------|------|------|----------|
| Message | User-visible messages | Chat messages, system notifications | ✅ Fully available |
| Command | Background control instructions | Trade creation/update, snapshot sync | ✅ Fully available |
| Item | Item data | Trade item encoding/decoding | ✅ P0 fully available |

> **About the current state of the Item pipeline (2026-06-21 update)**: P0 has been completed. The server-side three-phase chain (`IServerItemInterceptor` → `IServerDefaultItemHandler` → `IServerItemObserver`) is in place; the Client side has independent `KindItem` routing with `IClientIncomingItemHandler` / `IClientOutgoingItemHandler` / `TryHandleOutgoingItem` all ready; registered codecs are now consumed by the pipeline. The current Command-nesting path used by Trade remains compatible. See the "Submod Developer Guide" §6.3 for details.

When adding a new message type, first determine which pipeline it belongs to, then choose the corresponding handler interface to implement.

### 3.3 Inter-Plugin Interaction

Collaboration between plugins is the responsibility of the plugins themselves; the framework does not act as an intermediary:

- Chat needs to initiate a trade → Chat directly references `TradeExtension`'s Contracts, calling `ITradeRequestApi.CreateTrade()`
- Two plugins need to share data → each defines its own interface, resolving each other through the API registry
- The framework's role is only to provide the discovery mechanism (API registry), not to undertake business coordination

### 3.4 General Event Stream Replaces Dedicated Bridges

Plugins do not obtain host events through host-customized bridge interfaces tailored for them, but instead consume general event streams (`IClientUserEventStream`, `IClientMainThreadDispatcher`, `IClientSettingsContext`, `IClientSessionContext`, etc.). See the "Submod Developer Guide" §8 for the specific interface list and usage.

**Anti-pattern**: The host defines plugin-specific interfaces such as `IChatUiEventSink`, `ITradeUiHostContext`, creating a new set each time a plugin is added.

### 3.5 Error Isolation and Retry Mechanism

A single failure in network processing, message parsing, or extension execution **must not** interrupt the overall pipeline or cause service crashes. The framework employs a layered isolation strategy for various error types:

**In-pipeline isolation:**

- Exceptions thrown by individual interceptors/handlers are caught and logged by PipelineRunner, and the pipeline continues processing the next candidate
- A single message parsing failure (e.g., Protobuf `ParseFrom` exception) only skips that message, without affecting other messages in the same frame
- Extension `Activate()` / `Shutdown()` failures do not affect the lifecycle of other extensions

**Network layer resilience:**

- Critical handshake packet (Hello / Auth / Login / ExtendSession) send failures must actively disconnect the corresponding connection to prevent the client from waiting indefinitely
- Non-critical message send failures log errors and notify the caller (via return value or callback), without silently dropping
- On connection disconnect, immediately clean up associated session, user state, and event subscriptions, without relying on timeout timers as a fallback

**Retry strategy:**

- Client connection failures should provide clear error information (distinguishing "network unreachable", "authentication rejected", "protocol mismatch"), rather than a vague "connection failed"
- Messages sent when the client is not connected should trigger observable alerts (Error-level log or callback notification), without silently dropping
- When the server fails to send to a specific connection, distinguish "connection already disconnected" and "send timeout" — the former triggers the cleanup flow, the latter may consider retry

**Anti-pattern**: catch-all silently swallowing exceptions; background thread exceptions not notifying the main thread; send failures only writing a single line of Debug log while the caller is unaware.

### 3.6 Backpressure and Resource Boundaries

All unbounded queues must have capacity limits to prevent unbounded memory growth when producers are faster than consumers:

- Message display queue (`displayMessages`): limit 1000, remove the oldest entry and log a Warning when exceeded
- Main thread dispatch queue (`pendingActions`): limit 500, discard and log an Error when exceeded
- Extension manager log buffer (`extensionLog`): limit 300 (`MaxExtensionLogEntries`), read via read-only snapshot on `ExtensionLogVersion` change to avoid per-frame GC allocations
- Chat history sync: send in batches (50-100 per batch), do not block-send all history within a single poll cycle

Classes holding `IDisposable` resources such as `Timer`, `NetManager`, `Thread`, `FileStream` must implement `IDisposable` and release them in `Shutdown` / `Dispose`. Event subscriptions (`+=`) must be paired with unsubscriptions (`-=`) in the corresponding `Shutdown` or `Dispose`, without relying on process exit as the ultimate cleanup mechanism.

### 3.7 Plugins Must Not Bypass the Communication Pipeline to Directly Access the Underlying Transport

When communicating with the server, plugins **must go through the framework-defined handler pipeline** (`IClientMessageHandler`, `IClientCommandHandler`), and **must not** directly call `IFrameworkClientTransport.SendFrameworkPacket()` or `ILegacyModuleTransport.Send()` to bypass the pipeline.

**Why:**

- The handler pipeline is the mechanism by which the framework implements "plugin equality" (§1.1) — Priority ordering, interception, replacement, and fallback all depend on handlers executing in sequence
- A plugin directly sending a FrameworkPacket bypassing the pipeline = that protocol packet **skips all other plugins' handlers**. If another plugin (such as LegacyAdapter) tries to intercept traffic for protocol translation through Priority ordering, it will be completely ineffective
- A well-positioned plugin in the ecosystem (such as message auditing, content filtering, protocol adaptation) should not be rendered ineffective because other plugins "took a shortcut"

**Correct approach:**

- Outbound messages: return `FrameworkPacket` through `IClientMessageHandler.HandleOutgoingText()`, letting `PhinixFrameworkClient.TryHandleOutgoingMessage()` send uniformly
- Outbound commands (Trade, etc.): through the `IClientCommandHandler` pipeline, or through `IFrameworkClientLifecycle.CompatibilityMode` to determine the routing strategy after checking the current mode
- Inbound messages: distributed by the framework from `NetClient` to the handler pipeline after reception; plugins do not register `NetClient` handlers themselves to intercept inbound traffic (except Legacy protocol adaptation, since Legacy inbound traffic is entirely outside the Framework pipeline)

**Sole exception**: The Legacy protocol adapter can register raw module handlers through `ILegacyModuleTransport.RegisterHandler()`, because Legacy inbound data does not pass through the Framework pipeline at all (old servers send `"Chat"` / `"Trading"` module packets, not `"PhinixFramework"` packets).

**Judgment criterion**: Can any new plugin insert itself into the communication pipeline through Priority ordering, intercepting/modifying/replacing messages without requiring active cooperation from other plugins?

### 3.8 Logging and Observability

The framework provides a unified log event mechanism through the `ILoggable` interface (defined in `Common/Utils/ILoggable.cs`). Host internal components (`NetClient`, `ClientAuthenticator`, `UserManager`, `PhinixFrameworkClient`, etc.) implement this interface to produce log events, and the host uniformly subscribes and aggregates them at startup.

**Current plugin-side convention**: Plugins report logs through the `hostContext.Log` callback (`Action<string, LogLevel>`). This callback is injected by the host when constructing `ExtensionHostContext`, pointing to the same log outlet as the `ILoggable` events. **Current official extensions (Chat/Trade) use `hostContext.Log` and have not yet directly used `ILoggable`. They will subsequently be unified and migrated to plugin `ILoggable` support.**

No code may bypass the framework logging mechanism to write directly to the console or files.

**Log level conventions:**

| Level | Meaning | Usage Scenario |
|------|------|----------|
| `DEBUG` | Development diagnostic info | Pipeline message routing details, extension loading process, temporary debugging |
| `INFO` | Normal operational info | Connection established/disconnected, extension activation/shutdown, configuration loaded |
| `WARNING` | Recoverable anomaly | Queue overflow, message parsing failure but pipeline continues, retry success |
| `ERROR` | Failure requiring attention | Handshake packet send failure, extension activation failure, send timeout |
| `FATAL` | Service cannot continue | Critical resource exhaustion, unrecoverable state corruption |

**Log content standards:**

- All exception logs must include the `Exception` object (if `LogEventArgs` supports it), and must not log only `ex.Message`
- Network message logs **must not** record sensitive fields such as plaintext tokens, passwords, or keys; protocol packet content recording must be sanitized
- Message-level logs (such as pipeline observer `IServerMessageObserver`) should include message type identifiers for easy filtering by module

**Production minimum observability requirements:**

- Current connection count (`connectedPeers.Count`) should be periodically visible in logs or queryable via command
- Message throughput (inbound/outbound QPS) should be summarizable and loggable through `IServerMessageObserver`
- Error rate: at a minimum, distinguish "in-pipeline business errors" and "network layer transmission errors", counting them separately

**Client-side special conventions:**

- The RimWorld console swallows part of stdout output; client logs should not rely solely on `Console.Write` — they should be bridged to the host's standard log path through the `hostContext.Log` callback (current) or `ILoggable` events (future migration target)
- **Release build level filtering**: In Release builds, client `ILoggableHandler` and extension log buffers filter `DEBUG` level logs (pipeline routing details, command dispatches, trade snapshot broadcasts are routed at DEBUG), avoiding spamming the RimWorld console with chat messages; `INFO`, `WARNING`, and `ERROR` are preserved. DEBUG logs are only displayed in Debug builds or when Developer Mode (DevMode) is explicitly enabled
- The host extension manager (`ExtensionManagerTab`) contains a bounded 300-entry log viewer, colored by timestamp and log level, facilitating in-game diagnostic inspection

**Anti-pattern**: Plugins bypassing the logging mechanism to directly `Console.WriteLine`; exception logs only writing `ex.Message` and discarding the stack trace; production log level set to DEBUG.

### 3.9 Explicit State Machines and Timeout Self-Healing

Multi-step asynchronous interactions and network communication workflows must be managed using Finite State Machines (FSM):

- **Formal transitions**: The state space and permitted transition paths must be centrally and explicitly defined; do not combine ad-hoc boolean variables into implicit states
- **Timeout self-healing mechanism**: Any intermediate state waiting for remote confirmation or asynchronous callbacks (such as `Pending`) must have a strictly bounded lifetime; deterministic timeout detection must automatically degrade or mark failure, preventing workflows from deadlocking due to dropped network packets
- **State-driven presentation**: State machines maintain domain state while the UI acts only as an observer reading and rendering that state; UI code must never bypass state machines to mutate internal state directly

**Anti-pattern**: Vibe-coding ad-hoc `isWaiting` or `isProcessing` boolean flags; asynchronous requests without timeout handling, causing permanent UI spinners when the remote side does not respond.

**Judgment criterion**: Can the system's workflows be completely described by a clear state transition diagram? When simulating total network packet loss, can the system automatically recover after a specified timeout?

### 3.10 Data Buffering and On-Demand Consumption

Receiving external data and creating expensive in-game objects (UI elements, game entities) must be strictly decoupled:

- **Buffer first**: Unexpected bursts of large-scale or batched data should first be stored in lightweight local buffers or simple data structures; never instantiate massive numbers of game objects in a single frame (e.g., dropping hundreds or thousands of physical items at once)
- **On-demand and batched processing**: Large UI lists must use virtual scrolling (viewport clipping); game entity instantiation should be on-demand or time-sliced across frames to smooth main thread CPU overhead
- **Bounded queues**: All receiving queues and action dispatch queues must have maximum capacity limits and overflow strategies (drop, coalesce, or alert) to prevent unbounded memory growth

**Anti-pattern**: Instantiating all received items onto the game map in a single frame, freezing physics, pathfinding, and rendering; unbounded queues exhausting memory during high-throughput network bursts.

**Judgment criterion**: When receiving large batches of data or resources at once, does the game framerate remain steady without noticeable freezing or collapse?

### 3.11 Savegame Resilience and Forward Fault Tolerance

Mod data persistence (such as `ExposeData` / serialization) must follow the "savegame safety first" principle:

- **Forward compatibility and default fallbacks**: When adding or removing fields, older saves missing those fields must gracefully fall back to safe default values without corrupting save files
- **Local failure isolation**: When deserialization of a single item (e.g., a single trade record or item cache) fails, skip it and log a warning; never allow unhandled exceptions to crash the main game save loading process
- **Clean uninstallation**: When a player disables or removes the mod, persisted data must not leave behind ghost entities that cause the game engine to throw fatal exceptions

**Anti-pattern**: Missing serialization fields throwing unhandled exceptions that abort the entire save loading process (bricking saves); disabled mods causing save load errors that prevent playing.

**Judgment criterion**: When manually deleting or corrupting a field of this mod in a save file, can the game load the save normally with a graceful degradation warning in the log?

---

### 3.12 Client module composition

The client host supplies `IClientCompositionFactory` through `IExtensionBuilder.HostContext`. Every module, including third-party modules, may create one owned `IClientCompositionScope` in Register. Module discovery and parameterless module construction remain unchanged; required dependencies of ordinary services move to constructors. The neutral `IClientCompositionBuilder` offers typed `Register<TService, TImplementation>()` and `Borrow<T>(instance)` only. Resolve service/API/UI instances at this composition boundary and publish them through the existing builder; business services and per-frame UI do not resolve from the container.

Client/Composition owns Autofac 8.4.0 and its locked dependency graph. Common, plugin contracts and plugin implementation projects do not reference Autofac. Each registration is a local single instance; construction stays passive. Activate explicitly starts subscriptions/work. Shutdown first invalidates module callbacks and detaches module-level handlers, then disposes the scope. Owned resources must provide synchronous IDisposable cleanup; async-only owned types are rejected before construction. Owned IDisposable services stop/cancel their own work; guarded releases report each cleanup error and continue releasing dependencies. Borrowed host services and cross-plugin APIs are never disposed. Create, register, resolve and dispose require the game main thread; disposed scopes/factories are terminal. The host releases any remaining scopes after registry shutdown as a fallback.

Chat is the first adopter. Its feed adapter, UI context, user list and notice sidebar subscribe only at explicit Start and unsubscribe at disposal, including partial Start failure. Queued mention/legacy notifications verify module state, connection generation and captured game identity before delivery. Chat shutdown cancels image requests and releases its cached textures on the main thread. Optional Trade resolution remains a borrowed, deferred action at the module composition boundary. Other modules retain their existing assembly paths until their individual migration.

The main package owns one copy of Phinix.ClientComposition and all eight support DLLs in Common/Assemblies. Memory/Unsafe/Vectors compile references and packaged versions must agree. Managed packages must not redistribute these host assets. Deploy or roll back the full matching host/abstractions/Utils/Chat/runtime package; an isolated Chat DLL replacement is insufficient. Existing abstraction assembly version 1.8.0.0 is retained for these additive contracts; modules using them require the corresponding host build. Compile and console regression success do not certify Unity loading or compatibility with other mods.

New client authors derive from `ClientExtensionModule` and override `Compose(IExtensionBuilder)` (ClientExtensionAbstractions 1.9). Its explicit, nonvirtual shared Register bridge checks host composition availability and invokes Compose once through the ordinary registry. Chat now adopts this entry. Constructors stay passive; Activate/Shutdown and scope ownership remain explicit. Old direct Register modules and server modules retain their existing path during migration. Managed metadata recognizes only the trusted client contract/base assembly identity, not arbitrary external inheritance. The 1.9 API/CLR version prevents new authors from binding to older contract assemblies; deploy a matching full package.

F4-H development status: maintained Chat/Inventory/Trade/Store/LegacyAdapter and both samples use Compose. Direct client Register is deprecated; LegacyClientExtensionModule is an Obsolete source compatibility adapter, and registered old client modules receive one migration warning per startup. The shared server registry remains unchanged. Planned first stable deprecation: host 0.9.8; conditional removal: host 1.0 / client abstractions 2.0 after independent plugin migration, distribution/rollback and game gates. This is not a published version bump or removal. The 1.9 composition contract/runtime set is a freeze candidate; open game/boundary gates must remain explicit.

### 3.13 Store operation transitions

The bundled Store owns a pinned Stateless dependency behind its internal operation model. Shared contracts, the host and business services do not expose library types. The controller serializes transitions and snapshot publication under one gate; operation generations and token identity reject stale results/progress. Transitions contain no downloads, installation, UI work or persistence writes.

Cancellation keeps an operation busy until its worker exits. Transient completion after cancellation becomes Canceled; an authoritative successful durable installation/state change retains its true result. Dispose publishes terminal Stopped before invoking cancellation callbacks, and late completions cannot revive it. Existing installation journals, recovery and validation remain authoritative. Installed requires a successful installation-service response and a fresh inventory read; full download progress alone is insufficient. Common/Assemblies contains one matching Stateless DLL, protected from managed package replacement. Deploy the complete matching package; console tests do not certify game loading.

Extension management also exposes saved disabled module IDs that were not discovered, including after package removal. These are UI recovery entries only: they never become registry/discovery results or create module instances. Restoring one explicitly changes that module setting; installation still requires enabled module intent and all existing ownership/assembly checks. Package intent and module disable counts are displayed separately.

## 4. Boundary Rules

### 4.1 Reference Direction

```
Client → Common (shared contracts & abstractions) ← Server

Forbidden: Common → ../../Server/*.cs
Forbidden: Common → ../../Client/*.cs
Forbidden: Client → concrete plugin projects (ChatExtension, TradeExtension, ...)
```

### 4.2 What Can Go in Common

- Protocols (protobuf contracts, packet DTO)
- Abstractions (extension module contracts, handler contracts, API registry abstractions)
- Runtime-neutral utilities (serialization, text processing, basic logging interface)
- Infrastructure needed by both ends and not bound to a specific runtime

### 4.3 What Cannot Go in Common

- Business handlers (chat, trade, or any concrete business logic)
- Server-side state managers
- Client-side UI models
- Any client-only or server-only runtime implementations
- Host assumptions about official plugins

### 4.4 Judgment Criterion

> If moving a piece of code to one side (Client or Server) means the other side **is completely unaffected**, then it should not be in Common.

### 4.5 Common Source File Sharing Pattern (Current Transitional State)

Currently, some end-specific implementations (`ClientAuthenticator.cs`, `NetClient.cs`, `NetServer.cs`, `ClientUserManager.cs`, etc.) physically reside under the `Common/` directory, but through the `Compile Remove` / `Compile Include` linking mechanism in `.csproj`, it is ensured that **the compilation attribution is single-end** — Common's `Connections.csproj` excludes `NetClient.cs` and `NetServer.cs`, which are compiled by `Connections.Client.csproj` and `Connections.Server.csproj` respectively through path linking.

This pattern is a transitional state; physical location ≠ compilation attribution. The criterion for determining whether a file crosses the boundary is **compilation attribution**, not physical path:

- A file compiled by `Common/Authentication/Authentication.csproj` → belongs to Common
- A file only compiled by `Client/Common/Authentication.Client/Authentication.Client.csproj` → belongs to Client, regardless of where it physically resides

**Target state**: End-specific implementations are physically moved to their respective end directories, and the Common directory retains only truly runtime-neutral source files. This adjustment should be carried out after the assembly split (§5.2) is completed.

---

## 5. Directory and Assembly Conventions

### 5.1 Naming and Ordering

DLL filenames use zero-padded numeric prefixes to ensure string order = load order. The current build output directory structure and number assignments are as follows:

```
Client/
  Common/
    Assemblies/                          ← Framework infrastructure and core abstractions only
      01-LiteNetLib.dll                  ← Third-party networking library
      02-Protobuf.dll                    ← Serialization library
      03-Utils.dll                       ← IPhinixExtensionModule, framework basics
      04-Connections.dll                 ← Shared network types
      05-Connections.Client.dll          ← Client network implementation
      06-Authentication.dll              ← Shared authentication contracts
      07-Authentication.Client.dll       ← Client authentication implementation
      08-UserManagement.dll              ← Shared user management contracts
      09-UserManagement.Client.dll       ← Client user management implementation
      10-ClientExtensionAbstractions.dll ← UI abstractions and general host contracts
  1.6/
    Assemblies/
      13-PhinixClient.dll                ← Client host main assembly
  Common/
    Extensions/                          ← Plugin directory (isolated from Assemblies)
      08-ChatExtension.dll               ← Chat domain contracts
      09-TradeExtension.dll              ← Trade domain contracts
      10-InventoryExtension.dll          ← Inventory domain contracts
      10-LegacyAdapter.Client.dll        ← Legacy protocol adapter plugin
      11-ChatExtension.Client.dll        ← Chat plugin
      11-InventoryExtension.Client.dll   ← Inventory client plugin
      12-TradeExtension.Client.dll       ← Trade plugin
      17-PluginStore.Client.dll          ← Bundled plugin store
```

When adding new DLLs, assign numbers according to dependency relationships. RimWorld's `ModAssemblyHandler` will only avoid throwing `ReflectionTypeLoadException` if and only if the string order guarantees that all dependencies are loaded before their dependents.

RedPacket and TalentTrade are independent optional plugins, excluded from the main solution and distribution. Main packaging removes only retired flat DLLs, companions and owned language resources from generated output; it does not remove player settings, saves or managed installations. They use ordinary discovery/registration. Existing Legacy/BuiltIn/builtin identity prefixes do not imply bundled distribution.

### 5.2 Release Boundaries (Implemented State)

Official plugin DLLs and framework base DLLs are physically separated:

```
Client/
  Common/
    Extensions/          ← Plugin DLLs in independent directory (official plugins + submod probe directory)
      ChatExtension.Client.dll
      TradeExtension.Client.dll
      LegacyAdapter.Client.dll
      ...
    Assemblies/          ← Framework base DLLs only (01-10)
  1.6/
    Assemblies/
      13-PhinixClient.dll ← Host assembly

Server/
  Extensions/            ← Server-side plugin DLLs in independent directory
    ChatExtension.Server.dll
    TradeExtension.Server.dll
```

On startup, `ExtensionAssemblyLoader` scans `Common/Extensions/`, and also natively probes the `Assemblies/` directory of all active RimWorld mods, allowing third-party plugins to be distributed as standalone mods or placed in the Extensions directory (see the "Submod Developer Guide" §2.4 for details).

### 5.3 Versioning and API Compatibility

**Unified assembly versioning:**

All assembly version numbers are centrally managed through `Directory.Build.props`, not scattered across individual `AssemblyInfo.cs` files for manual maintenance. Currently, project versions are inconsistent and need to be aligned to a unified version.

**Git tags and releases:**

- Semantic versioning (Semver): `MAJOR.MINOR.PATCH`
  - `MAJOR`: Breaking API changes (e.g., interface removal, method signature change)
  - `MINOR`: Backward-compatible additions (new interfaces, new methods, new plugin mount points)
  - `PATCH`: Pure fixes, no public API changes
- Each release gets a Git tag `v<semver>`, triggering Docker image build and release

**Protobuf message compatibility:**

- Field numbers are **permanent and immutable**. Deleted fields must use `reserved` to reserve the number and name, preventing future reuse
- New fields can only be appended, not inserted between existing fields
- Enum values must not be deleted or renumbered — deprecated values should be marked `[Obsolete]` or commented, but retain their numeric values
- When changing message type semantics, a new message type must be created (e.g., `LoginRequestV2`), with the old type retained as legacy compatibility

**API deprecation lifecycle:**

Aligned with the §6 Host/Core incremental update rules, `[Obsolete]` marking follows this cadence:

```
Mark [Obsolete] → retain for at least 1 MINOR version → remove after confirming no downstream references
```

- Deprecated interfaces must indicate the alternative in XML documentation comments (`<summary>Use IXxx instead.</summary>`)
- Before removal, search the entire repository and known third-party submods to confirm no remaining references
- Expired deprecations can be cleaned up centrally during `MAJOR` version upgrades

---

## 6. Incremental Migration Principles

- **Each phase must remain compilable, runnable, and verifiable**. No "one-shot mass migration".
- **Close boundaries first, then do runtime migration, and finally complete plugin-ization**.
- **Refactoring does not change behavior**. Every change in Phase 5 (type movement, reference adjustment) only changes code attribution, not runtime behavior. If behavior changes, it is a bug.
- **Directory closure precedes assembly splitting**. Physical directories can be cleaned up first; assembly boundaries can be gradually tightened in subsequent phases.
- **New code follows new rules; old code migrates gradually**. New features must meet the boundary requirements of the current phase; existing code migrates out gradually by priority.
- **Host and Core only receive incremental updates**. All changes to Host and Core must be incremental — no deletion or removal of any existing public interfaces. If breaking changes are needed, retain the original interface and mark it `[Obsolete]` (or comment `// outdated`); internal implementation can be rewritten but external behavior must remain consistent. This rule ensures that downstream plugins and third-party submods do not experience compilation failures or runtime breakage due to framework upgrades.

---

## 7. Commit and Review Checklist

When adding or modifying code, check the following:

- [ ] Has the host project added a new reference to a plugin?
- [ ] Has Common added client-only or server-only code? If such addition is necessary, is the compilation attribution only through `.csproj` linking, rather than directly compiled into the Common assembly?
- [ ] Is any concrete business type hardcoded (e.g., directly using `IClientChatService`) instead of through a general interface (e.g., `IMainTabProvider`)?
- [ ] Does the new extension entry connect through existing general mount points (Tab/sidebar/badge/message handling), or was a new dedicated opening created in the host?
- [ ] Is inter-plugin interaction completed directly between plugins, rather than relayed through the host?
- [ ] Is the DLL load order correct under string ordering?
- [ ] Does the new network handling/message parsing have try-catch isolation? Would a single message failure interrupt the entire pipeline?
- [ ] Does the new `IDisposable` resource holder implement `IDisposable`? Are event subscriptions paired with unsubscriptions in `Shutdown`?
- [ ] Does the new queue/buffer have a capacity limit? Is the scenario of producers faster than consumers handled?
- [ ] Are changes to Host/Core incremental? Has any existing public interface been deleted or removed? If breaking changes are needed, has the original interface been retained and marked `[Obsolete]`?
- [ ] Are log calls reported through the `ILoggable` interface, rather than bypassing the framework to write directly to the console/file? Do exception logs include the complete `Exception` object?
- [ ] Has a new public API been added? If so, does the version number need upgrading to `MINOR`? If modified/removed, has it been marked `[Obsolete]` and the alternative indicated in documentation?
- [ ] Are Protobuf field changes compatible — no reuse of deleted field numbers, no modification of enum values, and have breaking changes created new message types?
- [ ] Do external inputs and numerical parsing have range and boundary checks (e.g., `TryParse` + `Clamp`)? Can they prevent extreme integer overflows?
- [ ] Do asynchronous network workflows have explicit state machine definitions? Is there a timeout fallback mechanism to prevent permanent deadlocks if the peer does not reply?
- [ ] When exiting to the main menu or switching saves, are event subscriptions completely unsubscribed? Are static references to map entities cleared?
- [ ] Has an invasive Harmony patch been added? Could it use existing general mount points instead?
- [ ] Do newly added Defs and config keys carry a unified namespace prefix?
- [ ] Does receiving external large payloads have a buffering/staging mechanism, rather than instantiating massive game world entities in a single frame?
- [ ] Does savegame and config serialization provide default fallbacks? Would individual item corruption block main save loading?
- [ ] Are standalone windows (ServerTab, SettingsWindow, CredentialsWindow, TradeWindow, etc.) constrained to the screen safe area using `UiScreenSafeArea.ClampWindow`?
- [ ] Are output Rects from controls and container layouts normalized to non-negative dimensions (preventing negative width/height rendering anomalies in Unity IMGUI)?
- [ ] Do two-pane or multi-pane views declare and execute fallback degradation policies when space is constrained (vertical reflow or SinglePane sub-tab switching)?
- [ ] Do forms and toolbars support dynamic reflow (Inline/Stacked) and overflow menus (FloatMenu), and are long translations and user texts backed by scroll views or truncated tooltips?
- [ ] Do large or open-ended lists (chat messages, items, logs) render only visible rows via `VirtualListLayout`?
- [ ] Have deprecated legacy container classes (`[Obsolete]` Flex/TabsContainer) been avoided in new code?

---

## 8. Pre-Release Performance and Stability Review

Before each milestone release, review item by item against the following categories. Review pass standard: **All CRITICAL items cleared, HIGH items have a clear disposition plan**.

### 8.1 Memory Leaks

**Review points:**

- Are all `IDisposable` resource-holding classes released in `Shutdown` / `Dispose`? Check objects: `Timer`, `NetManager`, `Thread`, `FileStream`, `StreamReader`/`StreamWriter`
- Do event subscriptions have paired unsubscriptions (`+=` ↔ `-=`)? Relying on process exit as the sole cleanup mechanism is considered a failure
- When removing elements from collections such as dictionaries/lists, do the keys match? (e.g., session dictionary uses connectionId as key but Remove uses sessionId)

**Inspection method:** Start server → connect 5 clients → disconnect → repeat 3 rounds → check if memory baseline continuously rises. Same for client: enter server → switch Tabs → exit → repeat.

**Anti-pattern**: Relying on timeout timers as a fallback to clean up resources that should be cleaned up immediately upon event triggering.

### 8.2 Error Handling

**Review points:**

- Network layer: Are reads/writes to `connectedPeers` / `probePeers` lock-protected? Are callbacks triggered by background threads marshalled to the main thread?
- Pipeline layer: Are individual interceptor/handler exceptions caught by PipelineRunner? Does the pipeline continue processing the next candidate after an exception?
- Parsing layer: Do Protobuf `ParseFrom`, `Unpack` and other deserialization operations have try-catch? Are malicious or corrupted messages only skipped without interrupting the pipeline?
- Critical handshake packets: Does a Hello / Auth / Login / SessionExtend response send failure trigger connection disconnect and resource cleanup?
- Client disconnection: Do messages sent when not connected trigger Error-level logging or callback notification, rather than silent discard?
- Exception granularity: Have catch-all (bare `catch` or no exception type filter) been replaced with catching specific exception types? Has `catch` as flow control been eliminated?

**Inspection method:** Send corrupted protobuf packets to the server, confirming the pipeline continues processing subsequent normal messages. Force-disconnect the client network, confirming the server cleans up session and user state after timeout.

### 8.3 UI Rendering Performance

**Review points:**

- Per-frame allocation: Are there `new` objects on the `DoWindowContents` / `Draw` / `DoButton` paths? (Regex, TextWidget, GUIContent, List, HeightContainer, etc.)
- Regex: Are all Regex used for rich text tag stripping `static readonly` precompiled instances? (`RegexOptions.Compiled`)
- Layout caching: Is `CalcHeight` / `CalcWidth` computed once on data change and cached, rather than recomputed every frame on the Draw path?
- LINQ allocations: Are there LINQ calls on the Draw path that allocate enumerators, such as `.Where()` `.Sum()` `.Select()` `.ToList()` `.Count()`?
- Property getters: Are property getters called every frame (e.g., `BadgeText`) and performing computation or allocating new objects each time? Should they be changed to push-style cached updates?
- Scroll lists: Do large lists (chat messages, trade items) traverse all entries every frame to compute layout? Should dirty flags be introduced to skip recomputation on no change?

**Inspection method:** RimWorld developer mode → open Performance Profiler → enter server Tab → send 100 chat messages → scroll up and down → check GC.Alloc and frame time. Per-frame GC allocation should be near zero.

### 8.4 UI Adaptability and Robustness

Full UI adaptability (Phase 9 acceptance standard) requires the system to maintain stable layouts, accessible controls, zero text clipping/overlap, and excellent performance across arbitrary resolutions, UI scale factors, and extreme content lengths.

**Review points:**

- **Safe Area Clamping**: All standalone primary windows and dialogs (`ServerTab`, `SettingsWindow`, `CredentialsWindow`, `TradeWindow`, `DirectTradeWindow`, red packet details, etc.) must be clamped within `(0, 0, UI.screenWidth, UI.screenHeight)` via `UiScreenSafeArea.ClampWindow`. When switching from high to low resolution, changing UI scale factors, or moving across monitors, windows must never extend off-screen, and title bars, drag handles, and confirm/close buttons must remain reachable.
- **Dynamic Navigation & Sidebar Drawer**: The main tab bar must use native RimWorld `TabDrawer.DrawTabsOverflow` and `TabDrawer.GetOverflowTabHeight`, wrapping automatically when 10–20 extensions register tabs, with the content area dynamically relinquishing the occupied height. When available window width cannot satisfy `MAIN_MIN_WIDTH` (480px) + sidebar `MinimumWidth`, sidebars declaring `IResponsiveSidebarProvider.CanCollapse` must automatically collapse into a drawer icon button (`☰`), opening a floating drawer overlay when clicked, never squeezing the main content into an unusable state.
- **Non-negative Geometry & Dynamic Reflow**: All layout Rects must guarantee non-negative width and height (`UiScreenSafeArea.Normalize`). Form rows should use `ResponsiveFormLayout` to switch adaptively between inline and stacked modes based on width, reserving room for error text. Action bars should use `ResponsiveToolbarLayout` to automatically wrap actions and overflow secondary items into a `⋯` FloatMenu when row limits are reached. Two-pane views should use `ResponsiveSplitLayout` to gracefully degrade to vertical stacking or single-pane sub-tabs when width or height is constrained.
- **Text Truncation & Scroll Containment**: Single-line text in fixed-height cards or rows must be truncated cleanly with a `TooltipHandler.TipRegion`. Multi-line text must measure height dynamically based on width and data version caches and be contained in an outer `Widgets.BeginScrollView`. Long player names, item descriptions, long localized translations, or server descriptions must never overlap or displace action buttons.
- **List Virtualization**: Large or unbounded lists (chat messages, trading shelves, red packet logs, talent markets, extension manager logs) must use `VirtualListLayout` (fixed-height `GetFixedRange` or dynamic-height `GetDynamicRange`) to perform visible-row clipping, rendering only items in the visible viewport and never running `Draw` across the entire list.
- **Cache Invalidation Driven**: Text measurement (`Text.CalcHeight` / `CalcSize`), LINQ sorting (`OrderBy`), and complex layout geometry calculations must be driven by language changes, container resize events, or data version invalidations. Re-measuring or re-allocating objects every frame on `Draw` / `DoWindowContents` hot paths is strictly forbidden.
- **Host Neutrality & Obsolete Containers**: All responsive layout primitives must reside in `ClientExtensionAbstractions`, keeping the Host completely agnostic of specific business extensions. Legacy `Displayable` flex container classes (`HorizontalFlexContainer`, `VerticalFlexContainer`, `TabsContainer`, `ConditionalContainer`, `MinimumContainer`, `VerticalPaddedContainer`) are fully marked `[System.Obsolete]`; existing callers must ensure defensive non-negative clamping, and new code must not use them.

**Inspection method:**
1. Test fullscreen and window resizing across 1024×768, 1280×720, 1366×768, 1920×1080, 2560×1440, and various UI scale multipliers.
2. Dynamically register 10–20 tabs and multiple sidebars, verifying tab row wrapping and sidebar drawer collapse interactions.
3. Inject 64/128-character player names, long item names, and long translation strings to confirm no text collision or clipping.
4. Stress-test scrolling with 1,000 items, verifying GC.Alloc stays near zero with no stutter or frame drops.

### 8.5 Thread Safety

**Review points:**

- Are reads/writes to shared collections (Dictionary, List) protected by `lock` or using `ConcurrentDictionary` / `ConcurrentQueue`?
- When network callbacks (`OnPeerConnected`, `OnNetworkReceive`, etc.) fire on the poll thread, is code operating on UI or shared state marshalled to the main thread?
- Is there a TOCTOU race between the `Connected` check and `Send`? Is `TrySend` used instead of `Send`?

**Inspection method:** High-concurrency connection test (10 clients simultaneously connecting + sending messages) → check for no `InvalidOperationException` (collection modified), no crashes.

### 8.6 Review Checklist

| Category | Check Item | Severity |
|------|--------|--------|
| Memory | `IDisposable` resource holders implement `IDisposable` | CRITICAL |
| Memory | Event subscriptions have paired unsubscriptions (`-=`) | HIGH |
| Memory | Session/Peer removal key matches | CRITICAL |
| Error Handling | Shared collections have lock protection | CRITICAL |
| Error Handling | Protobuf parsing has try-catch isolation | HIGH |
| Error Handling | Handshake packet send failure triggers connection disconnect | HIGH |
| Error Handling | Offline send triggers observable alert (not silent discard) | HIGH |
| UI Performance | Regex is `static readonly` precompiled | CRITICAL |
| UI Performance | Draw path has no `new` object allocation | HIGH |
| UI Performance | Layout calculation is cached (dirty flag), not recomputed every frame | HIGH |
| UI Performance | Draw path has no LINQ allocations | HIGH |
| UI Performance | Property getters do not perform real-time queries/allocations | MEDIUM |
| UI Adaptability | Window size and position clamped to `UI.screenWidth/screenHeight` safe area | CRITICAL |
| UI Adaptability | All dynamic tabs/sidebars remain accessible when count increases (TabDrawer wrap / drawer button) | HIGH |
| UI Adaptability | Sizing down triggers reflow/collapse/scroll without producing negative Rects | CRITICAL |
| UI Adaptability | Long translations and user texts do not cover actions; truncated text has tooltips | HIGH |
| UI Adaptability | Two-pane layouts declare and execute vertical/single-pane fallback degradation | HIGH |
| UI Adaptability | Large lists render visible rows only (VirtualListLayout virtualization) | HIGH |
| UI Adaptability | Responsive measurement and sorting driven by cache invalidation, not per-frame allocation | HIGH |
| UI Adaptability | Layout additions maintain host extension neutrality and backward compatibility | CRITICAL |
| UI Adaptability | Deprecated legacy Flex/TabsContainer classes avoided in new code | MEDIUM |
| Threading | Network callbacks marshalled to main thread | HIGH |
| Threading | Unbounded queues have capacity limits | HIGH |
| Threading | `TrySend` / TOCTOU protection | MEDIUM |
| Boundary Defense | External input and numerical parsing have boundary constraints and overflow prevention | CRITICAL |
| State Machine | Asynchronous intermediate states have timeout self-healing mechanisms (deadlock prevention) | HIGH |
| Lifecycle | Exiting to main menu / switching saves unsubscribes events and clears entity caches | CRITICAL |
| Lifecycle | Session re-entry and singleton initialization are idempotent | HIGH |
| Compatibility | Business features prefer mount points; Harmony uses minimal slicing | HIGH |
| Compatibility | DefNames and config keys carry unique namespace prefixes | HIGH |
| Performance | Large payloads use buffering and on-demand / batched instantiation | HIGH |
| Savegame Safety | Serialization has default fallbacks; single item corruption does not block save loading | CRITICAL |


## DLL plugin language resources

The host provides package-scoped localization: plugins bind during Activate, resolve keys while drawing and release during Shutdown. It is independent of store/business plugins and does not inject managed languages into RimWorld global dictionaries. The main thread publishes language changes; resource validation and fallback are general infrastructure.

## Client assembly loading ownership

RimWorld selects and loads ordinary mods and their effective version/conditional folders. The client discovers Phinix modules in assemblies already loaded by the game; it does not proactively scan other mods' root `Assemblies` directories or the game executable directory. Proactive Phinix loading uses its own `ModContentPack.RootDir` runtime/bundle roots.

The owned loader prepares complete assembly identities before loading, handles only owned requesters and declared exact references, and detaches its resolver after plugin shutdown. This is an ownership boundary for our loader, not CLR isolation. Managed plugin payload verification and startup conflict/dependency gates remain separate. The legacy server loader remains available until a separate migration is accepted.
