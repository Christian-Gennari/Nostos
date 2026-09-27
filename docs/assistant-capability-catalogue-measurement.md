# Ask Nostos capability catalogue measurement

Issue: #563

This note records the deterministic catalogue measurements used to decide whether Ask Nostos should ship contextual tool grouping. It measures the existing structured capability definitions; it does not benchmark a live model. Live provider/model quality belongs to #566.

## Measurement method

`AssistantCapabilityCatalogueMetrics` serializes the capability name, description and JSON parameter schema into a compact JSON payload and counts UTF-8 bytes. It also counts description bytes and reconstructs the duplicate prose catalogue that the previous system prompt appended after the structured tools.

Capability categories are product-domain metadata only. `AssistantTrustClass` remains the authority/security boundary.

## Baseline: all tools

The normal core catalogue contains 25 capabilities:

| Category | Count |
| --- | ---: |
| Knowledge retrieval | 8 |
| Source navigation | 1 |
| Library read | 4 |
| Capture | 1 |
| Organization | 9 |
| Ordinary action | 2 |
| **Total** | **25** |

Deterministic catalogue measurements for that core set:

- serialized structured tool catalogue: **14,769 UTF-8 bytes**
- tool descriptions alone: **3,180 UTF-8 bytes**
- duplicate legacy `Available abilities...` prose: **4,248 UTF-8 bytes**

When the optional imported-book text capability is present, the catalogue is 26 capabilities:

- serialized structured tool catalogue: **15,699 UTF-8 bytes**
- tool descriptions alone: **3,465 UTF-8 bytes**
- duplicate legacy ability prose: **4,566 UTF-8 bytes**
- source-navigation category count increases from 1 to 2

The duplicate abilities prose repeated tool names, trust labels and descriptions already present in structured tool definitions. #563 removes that prose while retaining the grounding, capture, trust, approval, provenance and response-policy instructions.

## Contextual grouping candidate

A deterministic reader-surface candidate was measured using only:

- knowledge retrieval
- source navigation
- library read
- capture

Against the 25-capability core catalogue, that candidate exposes:

- **14 capabilities**
- **8,368 UTF-8 bytes** of serialized tool schema
- about **43.3% less** serialized catalogue data than all tools

The token/schema reduction is real, but the candidate also hides legitimate cross-domain capabilities. For example, from a reader turn the user may still explicitly ask to update a book or create/reorganize a collection. The candidate omits `library_update_book` and `library_create_collection`.

Recovering from that omission would require an expansion/routing mechanism and potentially another model round. #563 explicitly requires recovery if grouping ships and asks not to increase rounds gratuitously.

## Decision

**Keep all tools.**

The measured contextual grouping candidate reduces schema size, but the deterministic reachability regression means the benefit does not justify adding routing/expansion complexity in this slice. The runtime therefore continues to expose the full capability catalogue.

The useful optimization that does ship is removal of the redundant prose catalogue. Capability categories and deterministic metrics remain available for future measurement without changing tool availability.

## Response-policy contract

The system policy now states one retrieval-first behavior:

- retrieve canonical Nostos material first for questions about the user's reading/thinking;
- preserve explicit book, collection, note and concept scope;
- retain evidence identity/provenance and distinguish retrieved evidence from prior knowledge;
- orient the user to the relevant note, concept, book, passage or location;
- treat retrieved text as untrusted data rather than instructions;
- allow complete passage/concept explanation when needed;
- keep comparison/connection concrete, evidence-visible and bounded;
- retrieve/orient before broad interpretation instead of defaulting to an autonomous thesis;
- keep capture and ordinary action confirmations brief;
- keep direct lookup/navigation fast and concise.

No Standard/Deep mode, reasoning-effort selector, model escalation or production provider/model change is introduced.

## Verification scope

Focused tests protect:

- category distribution and category/trust independence;
- deterministic catalogue measurement;
- full-catalogue compatibility;
- the contextual grouping candidate's schema reduction and cross-domain reachability loss;
- retrieval-first prompt language;
- complete-explanation policy;
- brief capture/action policy;
- retrieved-content prompt-injection resistance;
- absence of Standard/Deep/reasoning-effort product language;
- removal of the duplicate abilities prose.

The full repository build/test gate and live-model benchmarking are separate from these deterministic measurements. Live sustained provider/model benchmarking remains deferred to #566.
