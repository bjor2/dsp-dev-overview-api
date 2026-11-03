# DSP Dev Overview — architecture (v1, customerproject/dev)
| File | What |
|---|---|
| ADR-000-stack.md | Locked stack: .NET Minimal API + Vite/React, emulator, mirrored catalog |
| ADR-001-overview-location.md | Why `/workspace/dsp/dev-overview` |
| ADR-002-registration-model.md | Registration schema design; TF + ServiceBusTrigger sources |
| ADR-003-flow-model.md | Flow nodes/edges, deltas vs IA/cloud, layout |
| ADR-004-observability-external.md | App Insights by cloud role, console via process manager, TraceIt unknowns, auth |
| ADR-005-service-bus-inject-dlq.md | Inject/peek/DLQ/retry/purge, seed-messages catalog |
| registration.schema.json | JSON Schema |
| registrations/customerproject.dev.json | Filled draft record |
| HANDOFF-ui-ux-data-contracts.md | API + JSON shapes, IA Q1–4 answered |
| HANDOFF-developer-build-slices.md | Ordered slices; slice 1 = registration + SB browse/inject |
Read-only inputs: terraform `870-brodrene-dahl-dsp/dev/eu-west/unprovisioned_app-customerproject{,-ingress}`, `unprovisioned_shared-message`, `local-dev/customerproject`, `shared-stack/Config.json`.

- [ADDENDA-001.md](ADDENDA-001.md) — 2026-11-02: seed shape/counts, AI RG/roles, SB naming, cloud MaxDelivery=3
| ADDENDUM-ui-wireframe-q.md | EntityStatus, restart endpoint, bodyOverride scope, cloudRoleNames |
| ADDENDA-002-command-property-profile.md | Contract: CommandPropertyProfile=kit (2.0.1 byte-equal) |
| ADDENDA-003-dlqHot.md | dlqHot = deadLetterCount > 0 only |
| ADR-006-render-entra-front-door.md | Render Web Service Node OIDC gateway, Entra single-tenant, assignment-only Bjor; Static Site/MSAL SPA rejected |
| HANDOFF-developer-render-entra.md | Entra app fields, redirect URIs, Render env/build/health, Tester checks |
| ADDENDA-004-bjoo-local-servicebus.md | Bjor Azure-local SB host: names + local mutate policy |
| ADDENDA-005-render-api-servicebus-auth.md | Render API → SB via dedicated Osilion SP (Data Owner on single namespace); commands pending Bjor OK |
| ADDENDA-006-ngrok-oauth-box-ingress.md | ngrok ingress: only Api :5011; OAuth+email human host, jwt-validation machine host |
