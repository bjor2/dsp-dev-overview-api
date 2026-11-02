# ADDENDA-001 — 2026-11-02 follow-ups (seed shape, AI location/roles, SB naming)
Status: Accepted. Patches ADR-002/003/004/005, HANDOFF-ui-ux, registrations/customerproject.dev.json (validates against registration.schema.json, jsonschema).

## 1. Seed kit (`local-dev/customerproject/seed-messages/Program.cs`, fully read)
| Mode | Target | Count | Notes |
|---|---|---|---|
| `ingress [N]` | customerproject-ingress | 21 | 6 events × 3 projects (base DataVersion N, default 1) + project 1: renamed (N+1), stale (N), duplicate (N+1) |
| `commands` | customerproject | 2 | SyncProjectDates, CreateMissingProjects, Tenant=Optimera |
| `all` | both | 23 | ingress + commands |
| `one <Event> [DV] [1-3]` | customerproject-ingress | 1 | Name/ProjectName get ` (v{DV})` suffix so domain sees a real change |
| `purge` | 6 queues + DLQs | – | ingress, snapshot, domain, customerproject, communication, topic-inspect; local only |

Ingress message: JSON body, `Subject` = contract type name, `ContentType=application/json`, app property `DataSource=M3Cloud` (`MessageConstants.PropertyNameDataSource`). Envelope: `Id` (e.g. `100|<cust>`, `100|<cust>|01`, `100|<cust>|1|<addrNo>`, `100|<cust>|<order>`, `100|Q<order>`, `100|H<order>`), `DataVersion`, `DataSource=M3Cloud`, `PlatformIngressDataSource=M3Cloud`, `LastModifiedUtc=UtcNow+DataVersion s`, `DataSourceTenants=["Optimera"]`, `PlatformIngressTenants=[Optimera]`. Project 3 (10060077) has agreement Status `90` (ends → project deleted); others `20`. Dates are `yyyyMMdd` ints relative to now (−6/+18 months).

Commands (via kit / **Dahl.Core.Messaging 2.0.1** — Architect decision Nov 2: overview inject contract = **`kit` profile**, byte-equal to seed-messages): `Subject`=type name, no ContentType, **tenant in JSON body** (`Tenant: Optimera`); **no `Tenant*` application properties** on SyncProjectDates/CreateMissingProjects. Props that 2.0.1 does set (confirm on peek): `Dsp_IsM3Cloud`, `DataSource=M3Cloud`, `Dsp_ShardingBucketNumber`, `Dsp_CorrelationContextId`. **`Dsp_OriginalEnqueuedTime` is 2.0.2+** — not part of the kit contract. Requires M3 context (kit calls `M3CloudContextResolver.SetIsM3Cloud(true)`). Prefer run kit as child process, or reproduce **kit** props only.

> Correction: earlier draft of this section used `upgrades/_src-fee/dsp-core-messaging` (newer) and incorrectly listed `Tenant*` bools + `Dsp_OriginalEnqueuedTime`. Optional overview config `Overview:CommandPropertyProfile=addenda` may inject that newer set for experiments; **default and Tester contract = `kit`**.

## 2. App Insights
- `appin-870-dev-euw-dahl-shared` = `format("appin-%s-%s", name_base, "shared")`, `unprovisioned_shared-misc/r-app-insights.tf`. Workspace-based, sampling 12.5 %, cap 10 GB/day.
- RG: `rg-870-dev-euw-dahl-shared` (module.naming.resource_group, suffix incl. `shared`) — derived, confirm.
- Subscription: `870Z-DAHL-DigitalServicePlatform-West-Europe-DEV` — inferred from PPR/PRD naming; not in TF.
- Cloud roles: `web-870-dev-euw-dahl-customerproject`, `func-870-dev-euw-dahl-customerproject` (seen in DSP docs), `func-870-dev-euw-dahl-customerproject-ingress` (guess; external `dahl-app` module v2.1.8 builds names).

## 3. Service Bus naming (TF)
- Queue `sbq-870-dev-euw-dahl-shared-<name>`; topic `sbt-870-dev-euw-dahl-shared-<name>` (Azure/naming slugs, suffix `[870, dev, euw, dahl, shared]`, `unprovisioned_shared-message/m-common.tf` + `r-servicebus-entities.tf`; same as app settings `${module.naming.servicebus_queue.name}-shared-…`).
- Subscriptions: v1 `{from}-to-{to}-subscription`, v2 `{from}-to-{to}`, v3 `{to}`; forward_to = `sbq-…-shared-<queue>`.
- **MaxDeliveryCount cloud = 3** (queues default, subs fixed) vs local 10.

## Still unknown
Ingress function app real name; subscription GUID/RG confirmation (needs `az`); command body enum serialisation on first kit peek; TraceIt auth/API/correlation. Command app-prop contract locked to Dahl.Core.Messaging **2.0.1** / seed-messages (`kit` profile).
