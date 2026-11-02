# ADDENDA-003 — `dlqHot` meaning (Architect decision, Nov 2, 2026)

## Decision
**`dlqHot` = `deadLetterCount !== null && deadLetterCount > 0` only.**

It means: there are messages **already in the dead-letter subqueue**. Same meaning on Flow nodes, `EntityStatus`, and the Service Bus tab.

## Not in v1
Do **not** fold “near MaxDeliveryCount” into `dlqHot`. That would require peeking active messages for `deliveryCount`, is expensive for Flow poll, and is a different UX signal (“at risk of DLQ”).

`sb.maxDeliveryCount` (10 local / 3 cloud) stays on nodes for tooltips / legend only.

If UI later needs at-risk highlighting, add a separate optional field (Team Lead name: **`deliveryHot`**, via peek) after an explicit ADR — never overload `dlqHot`.
