# ADR-005 — Service Bus inject / browse / DLQ / retry from outside
Status: Accepted.

## Connection
v1 target = shared-stack emulator (`sbemulatorns`, `Endpoint=sb://localhost;...;UseDevelopmentEmulator=true`), configured in the overview's `appsettings.Development.json`. Cloud dev SB is **read-only (peek) and behind a flag**. Injecting to cloud needs Bjor's explicit OK, following the DSP rule that every write needs approval.

## Operations (all via the overview API)
| Op | Implementation |
|---|---|
| depth | `ServiceBusAdministrationClient.GetQueueRuntimeProperties` (active, deadLetter, scheduled). Emulator: if admin is unsupported, fall back to peek-count up to 250 |
| peek | `ServiceBusReceiver.PeekMessagesAsync` (non-destructive) |
| DLQ list | receiver on `SubQueue.DeadLetter`, peek |
| retry | receive the DLQ msg by sequence (PeekLock), clone body + ApplicationProperties + Subject/ContentType/CorrelationId into a new message with `x-overview-retry-of=<seq>`, send to the main entity, then Complete the DLQ msg. Retry-all = batched |
| discard | receive by sequence from DLQ → Complete (confirm in UI) |
| inject | send a catalog sample (optionally edited JSON) to the target entity. Adds the property `x-overview-injected=true` |
| purge | receive-and-delete loop on queue + DLQ, **local emulator only**, confirm required |

"Poison" = DLQ in Azure Functions isolated (no separate poison queue for SB). After MaxDeliveryCount=10, messages go to `$DeadLetterQueue`.

## Sample catalog
Source = `seed-messages/Program.cs`, **mirrored** once (by a generator script plus manual review) into `catalog/customerproject.samples.json`. Modes map 1:1 to the kit:
- `all` = ingress + commands
- `ingress` = 6 events × 3 projects + the rename/stale/duplicate sequence for project 1 → `customerproject-ingress`
- `commands` = `SyncProjectDates`, `CreateMissingProjects` (tenant Optimera **in body**) → `customerproject`, **kit profile** (Dahl.Core.Messaging 2.0.1 / seed-messages byte-equal; see ADDENDA-002)
- `one <Event> [DataVersion] [projectIndex]`
- `purge`

The overview reproduces the message shape (body + Subject + application properties) in C# from the catalog. It does **not** execute the kit. Optional dev mode "run kit" = `dotnet run --project seed-messages -- <mode>` as a child process, whose output goes to the console tab.
**Confirmed shape (Program.cs read 2026-11-02, see ADDENDA-001):**
- Ingress: body = JSON of the M3 contract (`BinaryData(obj, type)`), `Subject` = contract type name, `ContentType = "application/json"`, one app property `DataSource = "M3Cloud"`. Envelope: `Id`, `DataVersion`, `DataSource=M3Cloud`, `PlatformIngressDataSource=M3Cloud`, `LastModifiedUtc=now+DataVersion s`, `DataSourceTenants=["Optimera"]`, `PlatformIngressTenants=[Optimera]`. Company = 100.
- Counts: `ingress` = 18 (6 × 3 projects) + 3 (renamed v+1, stale v, duplicate v+1 for project 1) = **21**; `commands` = **2**; `all` = **23**; `one` = 1 (`one` also suffixes `(v{N})` onto Name + ProjectName). `ingress N` sets base DataVersion (only for `all`/`ingress`).
- Commands (**kit contract**, ADDENDA-002): prefer spawn seed-messages kit, or reproduce 2.0.1 props only. `Subject` = type name, **no ContentType**, tenant in body `{ "Tenant": "Optimera" }` (enum serialisation — confirm on first kit peek). **No `Tenant*` application properties.** App props from 2.0.1: `Dsp_IsM3Cloud`, `DataSource="M3Cloud"`, `Dsp_ShardingBucketNumber`, `Dsp_CorrelationContextId`. Do **not** require `Dsp_OriginalEnqueuedTime` (2.0.2+). Optional `Overview:CommandPropertyProfile=addenda` for experiments only.
- Purge list (kit): customerproject-ingress, -snapshot, -domain, customerproject, communication, customerproject-topic-inspect (main + DLQ).
- **Cloud MaxDeliveryCount = 3** (TF default for queues and subs); local emulator = 10. DLQ "hot" threshold must use the per-scope value.
