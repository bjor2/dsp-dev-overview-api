# ADDENDA-009: dsp-price (dev) registration + trigger kinds `timer` / `blob`

Status: Proposed (Architect, 2026-11-04). Builds on ADDENDA-008 (registration v2) and ADDENDA-004 (host kinds).
Output: `registrations/price.dev.json` (schemaVersion 2). It validates against `registration.schema.json`, and customerproject.dev.json still validates too.
Source: read-only scan of `/workspace/dsp/dsp-price` (box copy, stated to be master @ 51f4830; **the copy has no `.git`**, so the commit can't be verified on the box). Claude Code opus (effort high) drafted the mapping, and I re-verified it with rg. No secrets.

## 1. Trigger kinds (schema delta, merged minimally)
```json
"triggers.items.properties": {
  "kind": { "enum": ["servicebus","http","timer","blob"] },
  "schedule": { "type": "string" },   // timer: NCRONTAB literal or resolved value of `setting`
  "path": { "type": "string" }        // blob: "<container>/{pattern}"
},
"triggers.items.allOf": [
  { "if": {"properties":{"kind":{"const":"timer"}}}, "then": {"anyOf":[{"required":["schedule"]},{"required":["setting"]}]} },
  { "if": {"properties":{"kind":{"const":"blob"}}},  "then": {"required":["path","connection"]} }
],
"entities.items.properties.kind": { "enum": ["queue","topic","subscription","blobContainer"] },
"hosts": { "<hostKind>": { "namespace", "resourceGroup?", "queueTemplate?", "topicTemplate?", "subscriptionTemplate?", "mutating?", "provisioned?", "note?" } }
```
- **timer:** no inbound edge. The UI renders a clock badge on the node, with the schedule as a tooltip. `setting` holds the app-setting name when the attribute is `%X%`.
- **blob:** `entityId` may point to a `blobContainer` entity (`b:<name>`). It is an optional standalone source node with a `trigger` edge into the handler. **API change:** SB probes (`FlowService`, `ProjectHealth`, `OverviewService.ActiveEntities`) must skip `kind: blobContainer`. It has no depth, DLQ, inject or purge.
- **Timer grouping (amends ADDENDA-008 §11 "one Function per handler"):** several timer functions in **one class with the same outbound target** may form one handler with several `triggers[]`. The case here is `ReloadM3CloudDataTimerTriggers`: 6 functions, 1 node.

## 2. Mapping (paths relative to `src/Dahl.Price.`)
| Handler node | Function(s) | App | Trigger (file:line) | Sends to (file:line) |
|---|---|---|---|---|
| `price.fn.ingress` INGRESS | PriceIngressQueueHandler | Ingress | q:price-ingress `Ingress.Functions/Functions/PriceIngressQueueHandler.cs:47` | q:price-snapshot `Ingress.Application/Features/Import/ChangeEventHandlerBase.cs:55` (from ItemMaster/ItemWarehouse/ItemAssortment/SupplierChangedHandler :31/:31/:38/:31) |
| `price.fn.snapshot` SNAPSHOT | PriceSnapshotQueueHandler | Ingress | q:price-snapshot `…/PriceSnapshotQueueHandler.cs:32` | q:price-domain `Ingress.Application/Features/CreateIngressSnapshot/CreateOptimera*Handler.cs:16,18,23,16` |
| `price.fn.timer-reload-m3cloud` | Run{OptimeraCampaign,…PriceListTablePriceList}TimerTrigger ×6 | Ingress | timer `%ReloadM3Cloud*TimerTrigger%` `Ingress.Functions/Functions/ReloadM3CloudDataTimerTriggers.cs:15,30,45,60,75,90` (local.settings 02:05–02:18 daily) | q:price-snapshot `…ReloadM3CloudDataTimerTriggers.cs:24,39,54,69,84,99` |
| `price.fn.domain` DOMAIN | PriceDomainQueueHandler | Functions | q:price-domain `Functions/Functions/PriceDomainQueueHandler.cs:39` | none |
| `price.fn.domain-ingress` DOMAIN (PICO direct) | PriceDomainIngressQueueHandler | Functions | q:price-domain-ingress `…/PriceDomainIngressQueueHandler.cs:65` | none |
| `price.fn.commands` COMMANDS | PriceQueueHandler | Functions | q:price `…/PriceQueueHandler.cs:72` | t:price `Application/Features/Events/CheckItemCampaignsHandler.cs:52`, `Application/Features/Admin/PublishAllItemCampaignsHandler.cs:48`; **self-requeue** q:price `Application/Features/Import/VisionPoint/ImportVisionPointCampaignItemHandler.cs:45` |
| `price.fn.subscriptions` | PriceSubscriptionsHandler | Functions | q:price-subscriptions `…/PriceSubscriptionsHandler.cs:22` | none |
| `price.fn.sub-{customerproject,customer,warehouse,currency,product}` | *SubscriptionTrigger ×5 | Functions | topic + `price-to-<x>-subscription`: CustomerProject :27, Customer :26, Warehouse :18, Currency :17, Product :18 | none |
| `price.fn.blob-optimera` | OptimeraSnapshotFileBlobTrigger | Functions | blob `pricedumpfromoptimera/{filename}`, conn `PriceStorageAccount`, `…/OptimeraSnapshotFileBlobTrigger.cs:17` | none (DB import) |
| `price.fn.timer-snapshot` | PriceSnapshotTimerTrigger | Functions | timer `0 0 4,12,17 * * *` `…/PriceSnapshotTimerTrigger.cs:16` | q:price `:20` |
| `price.fn.timer-check-campaigns` | CheckItemCampaignsTimerTrigger | Functions | timer `0 0 * * * *` `:21` | q:price `:26` |
| `price.fn.timer-campaign-lowest` | PriceCampaignLowestPricesTimerTrigger | Functions | timer `0 15 4,12,17 * * *` `:21` | q:price `:25` |
| `price.api.http` | Api controllers | Api | HTTP | q:price `Api/V3/Controllers/AdminController.cs:38` (DeleteNexstepPriceList). `IIngressQueueClient` is injected at `:18` but never used, so no edge. |
| `price.api-batch.http` | Api.Batch | Api.Batch | HTTP | none |

Totals: **21 Functions** (15 Functions + 6 Ingress) and 2 web apps, giving **18 handler nodes**.

**PICO direct path:** M3 PICO events (~23 contracts) arrive on `q:price-domain-ingress` and go straight into DOMAIN (PICO), skipping ingress and snapshot. Nothing in this repo sends there. It is modelled as `ext:m3-pico → q:price-domain-ingress` (`inject`, scope cloud, confidence manual). DOMAIN and DOMAIN (PICO) are two nodes in `func-app` because both live in one resource, so no logical container is needed. `q:price-subscriptions` has no sender in this repo either (`ext:external-subscriptions`).

## 3. Containers
| id | members |
|---|---|
| `container:price.ingress-app` (func-870-dev-euw-dahl-price-ingress) | fn.ingress, fn.snapshot, fn.timer-reload-m3cloud |
| `container:price.func-app` (func-870-dev-euw-dahl-price) | fn.commands, fn.domain, fn.domain-ingress, fn.subscriptions, fn.sub-×5, fn.blob-optimera, fn.timer-×3 |
| `container:price.api` (web-870-dev-euw-dahl-price, :52161) | api.http |
| `container:price.api-batch` (web-870-dev-euw-dahl-pricebatch, :52171) | api-batch.http |

`layoutDefaults` seeds container positions only, and inner nodes are auto-laid out.

## 4. Invariant I-1
I scripted the check over the edges in price.dev.json. Edges into `container:*` or `app:*`: 0. Direct pairs:
- q:price-snapshot: fn.ingress and the reload timer send, fn.snapshot consumes. OK.
- q:price-domain: fn.snapshot sends, fn.domain consumes. OK.
- q:price: api.http and the 3 timers send, fn.commands consumes. OK.
- **q:price, COMMANDS sending to itself (VisionPoint):** the only hit. It is traced to code, so it becomes a `selfRequeue` badge and no edge is drawn.
- Every queue handler passes its own trigger client to the Dahl.Core `MessageHandler` base (Dahl.Core.Messaging 1.0.33, no source). Whether retries re-send is unverified, and it is not drawn (same as ADDENDA-008 OQ-4).
- **Result: no unresolved violations.**

## 5. Service Bus on `sb-bjoo-local-noe-dahl-shared` (read-only az, 2026-11-04 ~13:30 Europe/Oslo)
Expected names from `infrastructure/local-shared.bicep`:
- 6 queues: `sbq-bjoo-local-noe-dahl-shared-price`, `-price-ingress`, `-price-snapshot`, `-price-domain`, `-price-domain-ingress`, `-price-subscriptions`.
- Topics: `sbt-…-price`, plus upstream `currency`, `product`, `warehouse` and 8 ION topics (assortment, customeragreementhead, headerfile, linefile, quotationorderhead, salepricelist, supplier, systemtablefile). The ION topics have `price-to-*-subscription` forwarding to price-ingress.
- Upstream subscriptions on customer and customerproject.

Found:
- Queues are communication, customer×4 and customerproject×4. Topics are customer, customeraddress, customerproject and user.
- **Missing: all 6 price queues and their DLQs, sbt-price, sbt-currency, sbt-product, sbt-warehouse, all 8 ION topics, and all price-to-* subscriptions**, including `price-to-customer-subscription` (customer only has customer-to-customer) and `price-to-customerproject-subscription` (customerproject only has local-inspect).
- The price bicep has not been applied to this namespace, so `hosts.developer-local-azure.provisioned = false`.
- The emulator (`shared-stack/Config.json`) has no price entities either.
- The storage account `stlocaldahl{dev}price` (3 blob containers) was not checked.

## 6. Seed / test kit
None found. There is no `local-dev/price` folder, no seed-messages project, and no sample JSON in dsp-price/tests, so `samples.catalog = none` and the counts are not applicable. Follow-up: build a price seed kit modelled on the customerproject one (modes ingress / commands / pico / all).

## 7. Local run + roles
- Ports: Functions 7073 (`local.settings.json` LocalHttpPort), Api 52160 http / 52161 https, Api.Batch 52170/52171 (launchSettings). **Ingress has no LocalHttpPort set** (the default of 7071 would clash with other services). There are no `run-*.linux.ps1` scripts.
- Cloud role names (App Insights `appin-870-dev-euw-dahl-shared`, from the site names in `azure-pipelines.yml:165-169`): `web-870-dev-euw-dahl-price`, `web-870-dev-euw-dahl-pricebatch`, `func-870-dev-euw-dahl-price`, `func-870-dev-euw-dahl-price-ingress`. Not confirmed in AI.
- Cloud entity names are taken from the customerproject Terraform naming formula (`sbq-870-dev-euw-dahl-shared-{short}`). There is no Terraform in dsp-price, so these are inferred.

## 8. Gaps / open questions
1. Provision price on bjoo: run `infrastructure/setup-local-infrastructure.ps1` / local-shared.bicep. This is a write and needs Bjor's OK.
2. Add price to the emulator `Config.json`, and create a `local-dev/price` folder with run scripts. Pick an Ingress port (suggest 7074).
3. Confirm the cloud PICO source (which M3 topics or subscriptions forward to `price-domain-ingress`) and who sends to `price-subscriptions`. Both need the Claranet Terraform for `app-price`.
4. Who subscribes to `t:price` (consumers are outside this repo)? Add them as cloud scope later.
5. The box copy has no `.git`, so HEAD 51f4830 can't be confirmed here.
6. Developer: skip `blobContainer` in SB probes, add the timer/blob badges, and make the generator handle the timer grouping rule.
7. Confirm Dahl.Core retry semantics (shared with ADDENDA-008 OQ-4).
