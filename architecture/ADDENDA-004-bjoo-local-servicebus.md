# ADDENDA-004 — Bjor local Azure Service Bus host (Nov 2, 2026)

## Host
`sb-bjoo-local-noe-dahl-shared.servicebus.windows.net`

This is a **personal Azure Service Bus** namespace from `local-shared.bicep` (`sb-${developerName}-local-noe-dahl-shared`), **not** the shared-stack emulator (`sbemulatorns` / localhost) and **not** cloud DEV (`sbq-870-dev-euw-dahl-shared-*`).

No connection strings or keys in this note.

## Do registration entity names match?

| Layer | Queue/topic name shape | Match to `sb-bjoo-local-…`? |
|-------|------------------------|----------------------------|
| Emulator / `Config.json` / registration `localName` | short: `customerproject`, `customerproject-ingress`, … | **No** — wrong names on this host |
| Cloud DEV / registration `cloudName` | `sbq-870-dev-euw-dahl-shared-<name>` | **No** — different env prefix (`870-dev-euw` vs `bjoo-local-noe`) |
| Developer Azure local (`local-shared.bicep`) | `sbq-${developerName}-local-noe-dahl-shared-<name>` | **Yes** — for `bjoo`: `sbq-bjoo-local-noe-dahl-shared-customerproject`, etc. |

Same pattern for topic: `sbt-bjoo-local-noe-dahl-shared-customerproject` (cloud DEV uses `sbt-870-dev-euw-dahl-shared-customerproject`).

**Gap:** `customerproject.dev.json` today only carries emulator short names + 870 cloud names. It does **not** yet carry the `bjoo-local-noe` entity names. Overview must resolve names from **host kind**, not always from `localName`/`cloudName`.

### Entity names for this host (derive; do not store secrets)

For developerName `bjoo` (from namespace `sb-bjoo-local-noe-dahl-shared`):

- Queues: `sbq-bjoo-local-noe-dahl-shared-customerproject`, `-customerproject-ingress`, `-customerproject-snapshot`, `-customerproject-domain`, `-communication`
- Topic: `sbt-bjoo-local-noe-dahl-shared-customerproject` (confirm topic exists in that local bicep / deployed NS)
- Emulator-only entities (`customerproject-topic-inspect`, subscription `local-inspect`) may be **absent** on Azure local NS — treat as emulator-scope only unless bicep creates them

## Overview policy: local vs cloud

| Host kind | How to detect (FQDN / config) | Entity name source | Inject / DLQ retry | Purge |
|-----------|-------------------------------|--------------------|--------------------|-------|
| `emulator` | localhost / `UseDevelopmentEmulator` / `sbemulatorns` | registration `localName` | **allowed** | **allowed** (confirm) |
| `developer-local-azure` | `sb-*-local-noe-dahl-shared.servicebus.windows.net` | `sbq-{dev}-local-noe-dahl-shared-{short}` / `sbt-…` | **allowed** (personal sandbox) | **allowed** with confirm (not 870 shared) |
| `cloud-dev` | 870 / `euw-dahl` shared NS (or explicit) | registration `cloudName` | **peek-only** unless Bjor explicitly OK | **forbidden** (`PURGE_CLOUD_FORBIDDEN`) |

**Decision:** Treat `sb-bjoo-local-noe-dahl-shared…` as **`developer-local-azure` = local for mutating ops** (inject / DLQ retry / purge-with-confirm), **not** as cloud-dev peek-only. It is still Azure (auth via RBAC/connection from user secrets — never paste into docs), but it is Bjor’s personal local stack, not shared DEV.

## Registration / generator follow-up

1. Add optional `hosts` (or connection profile) on registration: `emulator` | `developer-local-azure` | `cloud-dev` with name templates — no secrets.
2. Generator: when FQDN matches `sb-(?<dev>.+)-local-noe-dahl-shared`, set `developerName` and fill `developerLocalName` per entity: `sbq-{dev}-local-noe-dahl-shared-{localName}`.
3. Until then, Overview config can map FQDN → name template without editing the draft JSON by hand.

## Related
- ADR-005 (emulator inject; cloud peek-only) — extended by this host kind
- `dsp-customerproject/infrastructure/local-shared.bicep` (read-only source of naming)
- shared-stack `Config.json` (emulator short names only)
