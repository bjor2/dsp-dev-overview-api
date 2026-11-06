# Confluence: CustomerProject Analysis (id 22779013)
Source: https://dahl-it.atlassian.net/wiki/spaces/DSPDOC/pages/22779013/CustomerProject+Analysis
Fetched: 2026-11-06. ADF/M3 complexity analysis — **upstream data provenance**, not in-process architecture.

## Domain entities (ADF)
- Address ← CustomerProject_Optimera_Address_delta (OCUSAD, CEMAIL, OCUSMA) — complex union
- HasProjectPrice ← Fee_Optimera_ServiceCharges / CustomerProject_Optimera_HasProjectPrice (OAGRHE/OARGHE, OOQUOH, OOHEAD) — aggregate

## Relevance to architecture layers
Mark as **external / boundary**: M3 Optimera tables and ADF jobs feed ingress snapshots. Do not invent how ADF maps into current IngressQueueHandler — cite this page as historical/upstream with confidence inferred/uncertain vs current repo handlers.
