# PENDING — per-item root content migration (ADDENDA-011.1)

**Status: COMPLETE 2026-11-06 (Europe/Oslo).** Both customerproject and price catalogs use handler-bound roots. Strict `verify-addenda-011.py` (no legacy flag) **PASS exit 0**. Obsolete container roots live under each project's `_obsolete/` and are **ignored** for `dsp-arch-pos:v1:{project}:{env}:{layerId}` (not deleted from browser storage, not remapped, ids not recycled).

`--allow-legacy-container-roots` may be removed in a follow-up cleanup; it is no longer needed for CI.

## Status 2026-11-06 16:02 (Europe/Oslo, UTC+1)
- **customerproject: MIGRATED.** Roots: fn.ingress, fn.snapshot, fn.domain, fn.commands (new, split), api (reuse-id + handlerId customerproject.api.http). Child func-app.domain-update keeps id, reparented to layer:customerproject.fn.domain. Retired container roots moved to `layers/customerproject/_obsolete/` (ignored for dsp-arch-pos; not in index; ids not recycled). Generator: `drafts/build-cp-per-item-roots.py` (reads `_obsolete/` inputs; idempotent).
- **price: DONE 2026-11-06T16:04+01:00** via drafts/build-price-per-item-roots.py: 8 handler roots (fn.ingress, fn.snapshot, fn.commands, fn.domain, fn.domain-ingress, fn.blob-optimera, api, api-batch) + child api.get-prices; ingress-app/func-app moved to layers/price/_obsolete/. Strict verifier PASS for both projects (exit 0). Deferred: subscriptions + 5 sub-*; no-layer: 4 timers.
- `drafts/build-layers-addenda-011.py` is superseded for customerproject and would overwrite later price provenance edits; it is guarded and must not be re-run.
