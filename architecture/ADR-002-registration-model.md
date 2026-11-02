# ADR-002 — Registration model
Status: Accepted. Schema: `registration.schema.json`. Draft: `registrations/customerproject.dev.json`.

## Identity
`id = "<project>.<env>"` → `customerproject.dev`, with `project = customerproject` and `env = dev` (the only env in v1).

## Sources (read-only), merged by the generator `tools/Generate-Registration`
| Field | Source |
|---|---|
| apps[] (web, func, func-ingress), Azure names | TF `unprovisioned_app-customerproject` (`locals-web.tf`, `locals-function.tf`) + `unprovisioned_app-customerproject-ingress`; naming `<type>-870-dev-euw-dahl-<service>` |
| app setting → entity name | TF `app_settings` `ServiceBus*Queue/Topic` (`sbq-870-dev-euw-dahl-shared-<name>`, topics `sbt-870-dev-euw-dahl-shared-<name>`; formula in ADDENDA-001) |
| triggers | `[ServiceBusTrigger("%Setting%", Connection=...)]` in `src/**/Functions/*.cs`, then resolved through app settings |
| queues/topics/subscriptions in cloud | TF `unprovisioned_shared-message/locals-sb-queue-dsp.tf`, `locals-sb-subscription-dsp.tf` (`from`/`to`/`forward_to_queue`/`sql_filter`) |
| local entities | `local-dev/customerproject/Config.json` (emulator) |
| local ports/run scripts | `local-dev/customerproject/run-*.ps1` |
| samples | `catalog/customerproject.samples.json` (ADR-005) |

Triggers found (read-only scan of `dsp-customerproject-localrun`):
- Functions `CustomerProjectQueueHandler` ← `%ServiceBusCustomerProjectQueue%` (conn `ServiceBusConnectionString`)
- Functions `CustomerProjectDomainQueueHandler` ← `%ServiceBusCustomerProjectDomainQueue%`
- Ingress `IngressQueueHandler` ← `%ServiceBusCustomerProjectIngressQueue%` (conn `ServiceBusNamespace`)
- Ingress `SnapshotQueueHandler` ← `%ServiceBusCustomerProjectSnapshotQueue%`

Publishers come from app settings without a trigger: Api → `customerproject` queue, `customerproject-ingress`, topic. Functions → topic, `communication`. Ingress → `customerproject-snapshot`, `customerproject-domain`. These are marked `"confidence": "inferred-from-settings"`.

## Two "views" of each entity
Every entity has `local` (emulator name) and `cloud` (dev Azure name/namespace) blocks, so one record serves both local-dev and the dev cloud. v1 runtime targets **local**. Cloud is read-only metadata until auth (ADR-004) exists.

## Manual overrides
`overrides` in the record is hand-maintained (e.g. the local-only `local-inspect` subscription). The generator never overwrites it.
