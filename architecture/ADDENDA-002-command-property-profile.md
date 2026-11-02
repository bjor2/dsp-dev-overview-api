# ADDENDA-002 — Command property profile (Architect decision, Nov 2, 2026)

## Decision
**Default / contract:** `Overview:CommandPropertyProfile=kit`

Overview command inject for `SyncProjectDates` / `CreateMissingProjects` must be **byte-equal** to `/workspace/dsp/local-dev/customerproject/seed-messages` using **Dahl.Core.Messaging 2.0.1**.

## Why
Product lock: sample messages = existing test kit. Developer verified kit sends tenant in the **body**, not `Tenant*` application properties; `Dsp_OriginalEnqueuedTime` appears only from **2.0.2**. ADDENDA-001’s first command-prop list came from a newer `_src-fee` copy and is superseded for the contract.

## Profiles
| Profile | Use |
|---------|-----|
| `kit` (default) | Tester, UI sample picker, slice 1 — match seed-messages 2.0.1 |
| `addenda` | Optional experiment against newer messaging prop set; not default |

## Doc updates
- ADDENDA-001 §1 commands corrected
- ADR-005 + HANDOFF-ui-ux examples corrected to kit shape
