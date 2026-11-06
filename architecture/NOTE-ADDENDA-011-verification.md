# ADDENDA-011 — first-slice verification
Verified 2026-11-06 (Europe/Oslo). Design artifacts only; no runtime API/UI implementation, deployment, publishing, service-source writes, pull/fetch, infrastructure action or global skill save.

## Reproduce
From `/workspace/dsp/dev-overview`:
```sh
python3 architecture/drafts/verify-addenda-011.py
```
Requires Python jsonschema (already available). This is an executable **fixture checker**, not the production loader; implementing all V-* checks, API behavior and UI tests remains Developer work through Team Lead.

Results:
- Draft 2020-12 meta-schema check passes; worked layer + catalog validate.
- Existing customerproject and price registrations validate against unchanged registration.schema.json.
- Semantic checks pass for the real single-root layer and a synthetic depth-3 navigation tree.
- 18 negative cases reject: missing parent, parent cycle, missing opens target, orphan child, duplicate opener, non-child jump, outside opener, missing edge endpoint, duplicate node ID, cross-project layer, missing Flow container/entity, traversal and absolute catalog paths, duplicate catalog path, observed/live edge kind, empty evidence and inference without rationale.
- 42 source occurrences resolve: real repo/registration files with matching SHA256, plus 3 page IDs actually fetched through Atlassian. Confluence versions: 22779513 v4, 22779013 v6, 22774292 v13.
- No malformed-reference recovery/ghost layer is accepted in the design. Invalid architecture is isolated from existing Flow.

## Read-only verification
Service tree fingerprints are SHA256 over sorted relative file names and SHA256(file bytes), excluding .git. Baseline and final are identical:
- customerproject: 11,708 files, `da9afa2f8c58cd36eb94bed49e719df70ef9107097a1a3e589bf00ff5975efe6`.
- price: 1,095 files, `62a1cc2e6ce674500c02ba4f061c74e1d833cac7f9cb00a647c9bf60ef366a66`.
Final customerproject `git status --short` empty, HEAD `b0864c051065f88e978b6d110483096c915d7edf`. Initial status check could not resolve a git repository, but final metadata was readable; no git write/fetch/pull was run. Price has no .git, so no clean-git claim is made for it.

Generated registration files remain byte-identical:
- customerproject.dev.json `5ccc19c9751934139e195b0c50121ce1b95af6fea5a3d934fc5de6308571f39a`.
- price.dev.json `ec39d09048cdbbc9236c6ffeaa7ec4e3f1fd6536bf9ca9bac8785c3d2daa8046`.

No claim that existing runtime code loads architecture is made: there is no implementation yet. Existing registration JSON/schema compatibility is proven; runtime DTO normalization, failure isolation, catalog packaging on both upstreams, navigation/pan/zoom and position/activity separation require integration tests.

## Design synthesis/tooling
Loaded skill-authoring before writing the research skill draft; it remains a file only. Claude Code fable was attempted as requested. Initial prompt parsing failed because variadic allowedTools consumed the prompt; stdin prompt corrected invocation. The read-only fable synthesis completed successfully (about 3m20s) and informed alternative assessment in ADDENDA-011. It proposed a richer shared-element/rollup catalog; that was not adopted wholesale because static explanations/no dangling links and a prompt first slice are the current contract. No CloudAgent tool was available in executor discovery; no production code changes made.

## Additional evidence snapshot
`drafts/confluence-22774292-event-stream-conversion.md` records the newer CQL-found conversion design. The parent already saved the other two Confluence markdown snapshots. Historical ADF and current snapshot SQL differ; no business-equivalence or rollout assertion was made.

## v1.1 verification (2026-11-06, Europe/Oslo)
`python3 architecture/drafts/verify-addenda-011.py`:
- PASS schema (layer/catalog); customerproject and price registrations validate unchanged.
- PASS real forests: customerproject 4 layers depth 2, price 5 layers depth 2; synthetic depth 3.
- PASS V-008 no bidirectional boundary node in any layer.
- PASS 19 negative fixtures (adds "bidirectional queue").
- PASS 364 citation occurrences: every repo/registration file exists with matching SHA256; only the 3 opened Confluence page IDs are cited.
Registration hashes unchanged: customerproject `5ccc19c9…f39a`, price `ec39d090…8046`.
Read-only: customerproject `git status --short` empty, HEAD `b0864c051065f88e978b6d110483096c915d7edf`; file counts unchanged (customerproject 11,708; price 1,095). This pass's tree fingerprint (sha256 over sorted `relpath\0sha256\n`, excl. .git): customerproject `0ffd9bb2f0625972672ab930974606fc334091c6a22e8da9fa5cc32c0331f306`, price `af34968c19911b046ffbf5477ecc1371bcb1ae18e2d34c9c912f5ad465b5c895`. The v1 fingerprint serialization was not recorded, so these values are a new baseline rather than a byte-for-byte comparison.
Generator: `drafts/build-layers-addenda-011.py` (reads service repos, writes only under dev-overview). No price Confluence pages were opened in this pass.
