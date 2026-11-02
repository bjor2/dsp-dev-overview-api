# ADR-001 — Overview lives at `/workspace/dsp/dev-overview`
Status: Accepted.

## Decision
The overview is a standalone folder/repo at `/workspace/dsp/dev-overview` with this layout: `architecture/` (these docs), `src/` (API), `ui/` (IA + SPA), `catalog/` (sample messages), `data/` (runtime store), `tools/` (registration generator).

## Why not inside `dsp-customerproject`
- Hard rule: **zero edits** to `dsp-customerproject` and other DSP service repos. The overview observes and injects from outside.
- One overview serves about 60 `unprovisioned_app-*` stacks. Owning it in one service repo would couple its release to that service.
- It doesn't affect service CI/CD, PR diffs or the .NET 10 upgrade work.
- The read-only inputs are Terraform (`/workspace/claranet/terraform/870-brodrene-dahl-dsp/dev/eu-west/*`), the service source (`ServiceBusTrigger` attributes), and `local-dev/<project>`. They are read by the generator, never written.

## Consequences
Registration can drift from code, so there is a "Refresh registration" step (ADR-002). Not in git yet; turning the folder into a git repo is Bjor's call.
