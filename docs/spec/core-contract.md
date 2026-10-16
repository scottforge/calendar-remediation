# Core Contract
**Version 1.0.1** · Calendar Remediation · 2026-10-16 · HADS 1.0.0 · Completed-system design

---

## AI READING INSTRUCTION

Read every **[SPEC]** block as normative requirements for the completed Core module, not evidence of implemented behavior.
Read **[NOTE]** blocks for rationale and source context.
Treat **[?]** blocks as unresolved decisions that cannot grant execution permission.
Conceptual names describe responsibilities and values, not compatibility commitments to current C# declarations.

---

## 1. Purpose and authority

**[SPEC]**
Core MUST turn observed meeting evidence and confirmed organizer intent into reproducible assessments, immutable plans, exact approvals, and a durable guarded execution workflow.
- MUST, MUST NOT, SHOULD, and MAY express required, prohibited, recommended, and optional behavior.
- This contract describes the ideal completed system and its acceptance conditions.
- The intended audience is self-service meeting organizers in British Columbia, Alberta, Saskatchewan, and Manitoba.
- The intended target includes single-instance meetings and evidence-qualified straightforward recurrence.
- Every live capability MUST remain denied until its own admission requirements pass.
- A completed implementation MUST expose unsupported and unresolved cases rather than silently widening its capabilities.

**[NOTE]**
The [Graph calendar API contract](ms-graph-calendar-api-contract.md) provides the foundation for the Graph adapter's behavior and the Core module's integration with the Microsoft Graph API.
This contract defines Core's obligations for that target without claiming that provincial rules or Graph mutation behavior have been proven.

**[?]**
Authoritative locality policy, effective dates, and a safe provider strategy remain evidence obligations rather than settled facts.

## 2. Module ownership and seams

**[SPEC]**
Ownership MUST concentrate deterministic decisions and execution authorization in Core while keeping external mechanisms behind injected interfaces.

| Owner | Required responsibility |
|---|---|
| Core deterministic calculations | Evidence validation, eligibility, intent resolution, time arithmetic, assessment, plan construction, canonicalization, and dependency invalidation |
| Core executor | Approval validation, admission checks, dispatch decisions, journal transitions, retry authorization, and interpretation of reconciliation evidence |
| Graph adapter | Graph HTTP, provider read normalization, provider mutation request construction, and preservation of raw provider evidence |
| Host | Authentication lifecycle, model invocation, UI, composition, and durable storage implementation behind Core interfaces |
| Injected rule provider | Versioned policy and time-rule artifacts with provenance and explicit coverage |
| Injected clock | Explicit evaluation and dispatch instants |
| Injected journal | Transactional persistence, uniqueness, concurrency control, and durable evidence retrieval |

**[SPEC]**
- Deterministic calculations MUST NOT perform network calls, model calls, storage writes, or implicit reads of the system clock.
- Identical validated inputs and dependency versions MUST produce identical substantive decisions and canonical plan bytes.
- The executor MAY perform effects only through injected reader, writer, journal, clock, and authorization interfaces.
- Graph MUST NOT decide meeting intent, select mutations, approve retries, or interpret readback as authorization.
- Model proposals MUST enter as untrusted evidence-bearing values, never executable instructions.
- Host MUST NOT bypass Core execution authorization with a direct mutation route.
- Interfaces MUST define failure modes, ordering, cancellation, and evidence obligations alongside their value shapes.

## 3. Values, identity, and unknown information

**[SPEC]**
Core MUST preserve the distinction between observed facts, inferred hypotheses, confirmed intent, and unknown values.

| Conceptual value | Minimum content and invariant |
|---|---|
| Provider identity | Provider, deployment, tenant, mailbox, calendar context, stable resource identity, and identifier semantics |
| Source snapshot | Identity, opaque source validator, raw and normalized time values, organizer evidence, attendees, resources, event category, and read provenance |
| Read evidence | Observation instant, projection/version, requested scope, response representation, source references, completion status, and failures |
| Confirmed intent | Organizer confirmation, evidence references, intended locality and time, preservation mode, and explicit ambiguity answers |
| Rule dependency | Artifact identity, version, digest, provenance, effective interval, locality coverage, and approval status |
| Assessment | Classification, reasons, evidence dependencies, unresolved questions, and proposed effects if computable |
| Execution identity | Plan, approval, selected item, logical operation, durable attempt, and provider correlation identifiers |

**[SPEC]**
- Unknown, absent, malformed, unsupported, and not requested MUST be distinguishable where they affect decisions.
- Missing attendees MUST NOT become an empty attendee list, and missing organizer evidence MUST NOT become organizer authorization.
- UTC storage, response time zone, device settings, and mailbox defaults MUST NOT establish meeting intent.
- Validators MUST remain opaque and bound to the exact resource and provider operation for which they were obtained.
- Read identifiers MUST NOT be substituted across mailboxes, tenants, identifier modes, or occurrence/master categories.
- Duplicate observations MUST be reconciled by identity and version evidence, not subject text or approximate start time.
- Contradictory observations or an unavailable stable identity MUST hold the affected item.
- All-day values MUST retain start date and exclusive end date, without conversion to fabricated midnight instants.
- Recurrence evidence MUST retain master identity, occurrence identity, original occurrence start, pattern, range, zone, exceptions, and known cancellations.

## 4. Discovery scope, completeness, and budgets

**[SPEC]**
Discovery MUST produce an evidence-bearing bounded set that accounts for every organizer-owned meeting in the requested scope.
- The default user-facing horizon MUST be the next 90 days, with an optional explicit date range within the provider contract's maximum 366-day request range.
- Scope MUST include explicit offset-bearing or instant boundaries, the captured evaluation instant, organizer identity, and future-only execution cutoff.
- The adapter MUST state its interval and overlap semantics so boundary occurrences are not silently lost.
- Core MUST retain all discovered items in the accounting set, including no-change, display-only, unsupported, unresolved, and unassessed items.
- Core MUST require completion of all continuation pages and all details needed for each assessment.
- An incomplete page sequence, exhausted budget, item omission, detail-read failure, cancellation, or time limit MUST mark the scope incomplete and block approval for that scope.
- A smaller scope MAY be discovered and reviewed as a new complete scope, but MUST NOT silently replace an incomplete approved scope.
- Discovery completion MUST NOT imply that a recurring mutation is bounded to the discovery window.

**[SPEC]**
Budgets MUST be explicit inputs with observed consumption and a terminal reason when exceeded.
- Required budgets include elapsed discovery time, page count, item and instance count, detail-read count, selected-write count, and total run duration.
- Configured limits MUST respect the Graph adapter's stricter request, response-size, timeout, and date-range constraints.
- Operational budgets SHOULD be based on measured meeting density, throttling, detail-read cost, and realistic organizer review time.
- The initial executor MUST permit one active run and one mutation request in flight.
- A timeout or limit MUST NOT be reported as an empty successful discovery.
- Completed discovery MUST record the exact scope and evidence inventory used to construct the plan.

**[NOTE]**
The clarified review establishes a 90-day default product experience.
Earlier proposals for 100 instances, 10 writes, or a 15-minute run are not universal acceptance thresholds.

## 5. Intent, policy, and rule provenance

**[SPEC]**
Intent evidence, applicable civil-time policy, and provider capability evidence MUST be independently validated.
- Locality MUST be the organizer-confirmed city or community, including intra-province exceptions.
- Detected or suggested time zones MAY seed a question but MUST NOT override confirmation or change calendar settings.
- A mismatch between detected zone and confirmed locality MUST produce a reviewable clarification.
- Policy evidence MUST identify authoritative sources, publication or retrieval context, effective dates, geographic applicability, and review status.
- The selected time-rule artifact MUST implement the approved policy for every affected date and locality.
- A supported provider zone identifier MUST NOT be treated as proof that installed provider rules implement that policy.
- When a decision relies on baseline reconstruction, it MUST identify the historical rule artifact and evidence supporting the original interpretation.
- Missing or contradictory evidence required by the selected decision path MUST prevent a corrective mutation.
- Independently confirmed intended local values MAY establish a target without reconstructing the original interpretation, provided applicable target rules, current source evidence, and all other admission gates are validated.
- Core MUST NOT invent a historical baseline or imply that direct confirmation proves the original scheduling intent.
- Core MUST NOT infer a province-wide fixed offset or a legislative effective date from a product label.

**[SPEC]**
Preservation intent MUST be explicit and independent of the response representation.

| Mode | Required confirmed meaning |
|---|---|
| Preserve local start | Keep the intended wall-clock start in the confirmed locality under the applicable target rules |
| Preserve another locality's anchor | Keep the confirmed wall-clock start in that other city or community, resolving its date-specific rules |
| Preserve instant | Keep the absolute start instant, with any local display difference explained |
| Preserve local end, if supported | Independently resolve the confirmed end and explicitly approve any elapsed-duration change |

**[SPEC]**
- Core MUST retain who confirmed intent, when, against which snapshot, and which question or evidence was confirmed.
- Model confidence MUST NOT replace confirmation.
- Changed confirmation MUST create a new dependency version and invalidate affected planned effects.

## 6. Time calculations and assessment

**[SPEC]**
Time calculations MUST use explicit local dates and times, offsets, instants, and pinned rule artifacts.
- Preserve elapsed duration by default, compute end from the resolved start, and show the resulting local end.
- Reject non-positive durations, invalid intervals, overflow, and values outside the supported provider range.
- For a gap, return the missing local interval and require a valid corrected time confirmed by the organizer.
- For an overlap, return candidate instants and offsets and require an explicit supported selection.
- Never select a gap shift or overlap offset through an implicit platform default.
- If local-end preservation is supported, resolve both ends independently and expose the resulting duration change.
- Compare semantic time values and approved writable fields, not formatted display strings alone.
- A changed response representation with unchanged intended instants MUST NOT manufacture a correction.

**[SPEC]**
Every discovered item MUST receive an explicit assessment status, with eligibility and admission recorded separately.

| Status | Meaning |
|---|---|
| Correction proposed | Confirmed intent and validated rules produce a concrete difference in supported writable values |
| No change recommended | Sufficient evidence establishes that supported values already satisfy confirmed intent |
| Display-only difference | Representation changes while scheduling semantics already satisfy intent |
| Needs clarification | Missing or conflicting evidence can be resolved through a supported question |
| Unsupported | Event category or requested effect exceeds the defined capability |
| Unassessed | Assessment was not performed or could not complete |

**[SPEC]**
- Only correction-proposed items with all eligibility and admission gates satisfied MAY enter an executable selection.
- A resource-bearing, non-organizer, all-day, private, recurring, or otherwise special item MUST pass its explicit category rules rather than inheriting single-instance support.
- Unknown policy MUST NOT be labeled no change recommended.
- Each assessment MUST carry machine-readable reason codes and human-readable evidence references.
- Manual, Copilot-assisted, and express selection MUST use the same assessments, exact preview, final approval, and executor.
- User-requested LLM review MUST provide reviewable rationales and uncertainty for positive and negative classifications across the candidate list.
- Core MUST validate model references and retain model hypotheses separately from facts and confirmation.
- Subject or description-based review MUST use separately admitted content evidence from the Graph content-read profile, bound to the same account, item, and source version.
- Core MUST distinguish content not requested, unavailable, redacted, truncated, and successfully read rather than treating each as an empty description.
- Missing required content MUST hold the dependent model classification or produce an explicitly limited review, while preserving manual review where its evidence is sufficient.
- Unavailable or malformed model output MUST leave a usable manual review path and MUST NOT become an implicit recommendation.

## 7. Recurrence and effect scope

**[SPEC]**
Straightforward recurrence MUST be a defined, evidence-qualified capability rather than a blanket series exemption.
- Each admitted recurrence category MUST name a specific provider strategy, supported pattern and range, eligible exceptions, and exact mutation target.
- The strategy MUST define its affected occurrence set, including effects beyond the discovery range.
- An unbounded series MUST be held unless a proven strategy bounds mutation effects and establishes the required future behavior.
- Future-only eligibility MUST cover all actual effects, not only the visible occurrence used to select the series.
- The plan MUST describe historical occurrences, future occurrences, exceptions, cancellations, and attendee notification effects.
- History, exceptions, and cancellations MUST be preserved according to an explicit approved strategy and verified provider evidence.
- Source validators MUST protect the relevant recurrence resource and operation, with separate proof for masters and occurrences where applicable.
- Splitting, replacing, truncating, or rewriting a series MUST NOT be inferred safe from single-event PATCH behavior.
- If effect scope cannot be enumerated or conservatively bounded and reviewed, the item MUST remain held.
- Complex exceptions and broader series rewrites MUST remain unsupported until their own strategy and evidence pass admission.

**[?]**
No specific Graph strategy for safe future-only straightforward recurrence is established by this contract.
The implementation must resolve and prove that strategy before enabling recurring writes, without removing recurrence from the intended completed target.

## 8. Provider admission evidence

**[SPEC]**
Live-write admission MUST validate reviewed provider evidence and every other product gate before dispatch.

| Evidence state | Admission consequence |
|---|---|
| Planned | Denied |
| Inconclusive | Denied |
| ObservedFail | Blocked for the affected capability |
| ObservedPass | Necessary evidence, subject to artifact validation and all remaining gates |

**[SPEC]**
- Evidence MUST bind deployment, endpoint, identity mode, operation, event category, guard mechanism, adapter/configuration version, and observation context.
- The record MUST include reviewer identity, review decision, artifact references and digests, experiment inputs, controls, responses, and resource readback.
- Core MUST validate required artifacts and scope compatibility, not accept a boolean such as `safeToWrite` as a substitute.
- Experiments MUST establish atomic stale-write rejection and include omitted-guard and altered-guard controls.
- An unguarded control succeeding MUST NOT alone be classified as failure of guarded requests.
- Simulator success MUST NOT supply provider ObservedPass evidence.
- A `412` response alone MUST NOT establish that an unvalidated guard was enforced correctly.
- Changed deployment, identity semantics, endpoint, category, middleware, or guard assumptions MUST invalidate incompatible evidence.
- Notification and recurrence gates MUST have category-specific evidence, including internal and external attendees where supported.
- Read-before-write MUST NOT replace the atomic provider guard.
- Product live writes MUST remain disabled until compatible reviewed evidence passes.

## 9. Immutable plan and canonical digest

**[SPEC]**
A plan MUST be a durable immutable description of exact effects and all dependencies required to authorize them.

| Plan field group | Required content |
|---|---|
| Identity and schema | Plan ID, schema version, canonicalization version, digest algorithm, creator principal, and creation instant |
| Scope and completeness | Discovery interval, cutoff, organizer/mailbox context, completion evidence, budgets, and inventory references |
| Per-item source | Stable identity, source snapshot digest, opaque validator, event category, and recurrence relationships |
| Decision dependencies | Confirmed intent and version, baseline when relied upon, policy and rule artifact versions/digests, assessment version, capability evidence, provider projection, and any relied-upon content evidence |
| Exact effects | Before and after local values, offsets, instants, duration, semantic writable changes, provider mapping version, and expected postconditions |
| Review consequences | Mutation target, full recurrence effect scope, notification expectations and uncertainty, warnings, and unresolved exclusions |
| Execution constraints | Validity window, future-only rule, selected operation category, and allowed continuation policy |

**[SPEC]**
- Plans MUST contain or content-address every authorization-relevant dependency and validate referenced content before use.
- Plan construction MUST reject missing dependencies, duplicate item identities, ambiguous changes, and unsupported effects.
- Canonicalization MUST define field order, collection order, string encoding, date/time precision, null/unknown representation, and numeric representation.
- Canonicalization MUST exclude the digest field itself and MUST NOT depend on locale or presentation formatting.
- The digest MUST use a versioned collision-resistant algorithm and bind all authorization-relevant content.
- Canonical bytes and digest MUST be persisted together and verified after loading or restart.
- Editing a plan, changing a dependency, or changing a provider mapping MUST produce a new plan and digest.
- Preview MUST be derived from the exact stored plan, not independently recalculated UI values.
- Dry-run MUST use the same assessment, construction, canonicalization, and preview path without mutation.

## 10. Exact approval and invalidation

**[SPEC]**
Approval MUST be a durable explicit authorization by the authenticated organizer for exact selected plan items.
- Approval MUST bind approval ID, principal, mailbox, original plan ID and digest, exact selected item IDs, confirmation instant, expiry, and partial-execution policy.
- Approval expiry MUST be no later than the plan's validity-window end, and approval MUST NOT widen the plan's validity window.
- Server-side validation MUST prove that the principal may approve and execute against the bound mailbox.
- Model selection, UI checkbox state, reconnect, or preview rendering MUST NOT constitute approval.
- Empty selection MUST authorize no dispatch.
- Unselected and newly discovered items MUST NOT join an approval.
- Immediately before each dispatch, Core MUST verify approval integrity, principal/mailbox binding, both plan and approval validity windows, admission, source guard, future-only eligibility, and all dependent versions using the injected clock.
- Validity checks MUST be repeated after required durable pre-dispatch I/O and before invoking the provider whenever that I/O could outlast a validity boundary.
- Pauses, reconnects, and resumed runs MUST repeat these checks.
- An item-specific change MUST invalidate that item and any item sharing the changed dependency.
- A shared scope, policy, rule, baseline, or capability change MUST invalidate all dependent remaining items.
- Unaffected items MAY continue only if the original exact approval explicitly permits partial execution.
- Otherwise, an invalidated selected item MUST pause the remaining batch for review.
- Replanning MUST create new immutable evidence and renewed approval rather than rewriting an approved digest.
- Completed items and original approval records MUST remain immutable historical evidence.

## 11. Durable journal and dispatch ordering

**[SPEC]**
The journal MUST support a recoverable state machine that distinguishes local intent, possible dispatch, provider evidence, and reconciliation.

| State | Meaning and permitted next action |
|---|---|
| Planned | Immutable plan exists, no authorization to dispatch |
| Approved | Exact approval exists, dispatch checks still required |
| Prepared | Attempt intent, exact request description, guard, and checks are durably committed |
| DispatchStarted | Durable marker precedes the provider call, so dispatch may have occurred |
| OutcomeRecorded | Transport outcome and available raw evidence are durable |
| Reconciling | Reads gather state evidence without replaying the mutation |
| Resolved | Final report retains transport, state verification, attribution, and delivery as separate dimensions |
| HeldUnknown | Possible effect is unresolved, mutation replay prohibited |
| Skipped or CancelledBeforeDispatch | No provider call was started for this attempt |

**[SPEC]**
- Journal operations MUST be transactional and idempotent by stable plan, approval, item, operation, attempt, and transition keys.
- Conflicting reuse of an idempotency key MUST fail, not overwrite evidence.
- Preparing an attempt MUST atomically validate journal state, reserve the item, and commit its approved inputs and exact request description.
- Before the provider call, the executor MUST durably commit DispatchStarted using compare-and-set state/version checks.
- A failure to persist required pre-dispatch evidence MUST prevent the provider call.
- No database transaction or lock MAY be assumed to atomically include the external HTTP mutation.
- The provider call MUST use the prepared identity, operation, guard, and changes without widening them.
- Outcome persistence MUST append evidence and transition atomically without destroying the prepared record.
- A journal failure after possible dispatch MUST hold the attempt as unknown, including when a successful response was visible only in memory.
- The journal MUST retain transition ordering, timestamps, reasons, correlation identifiers, and evidence digests.

### 11.1. Concurrency, cancellation, and restart

**[SPEC]**
Concurrency control MUST prevent two workers from independently dispatching the same approved item.
- Run and item ownership MUST use durable uniqueness or leases with fencing tokens and compare-and-set transitions.
- Lease expiry MUST NOT make a DispatchStarted item available for another mutation.
- A replacement worker MUST reconcile an abandoned possibly dispatched attempt before any further authorization.
- A worker that loses ownership MUST NOT initiate a new provider call.
- Cancellation before DispatchStarted MUST durably stop dispatch and release a prepared reservation through an authorized transition.
- Cancellation after DispatchStarted MUST stop later work but MUST NOT assert that the provider canceled the mutation.
- Restart from Prepared MAY proceed only after fresh ownership and all dispatch checks, with durable proof that DispatchStarted was never committed.
- Restart from DispatchStarted without a durable outcome MUST enter HeldUnknown and reconcile without replay.
- Restart from OutcomeRecorded MUST resume persistence/reporting or reconciliation, not mutation.
- Repeated run commands and circuit reconnect MUST return or resume the existing durable operation rather than create a second dispatch.

## 12. Mutation outcomes, retry, and reconciliation

**[SPEC]**
Transport outcome MUST be stored separately from reconciled state, causal attribution, and notification delivery certainty.

| Transport outcome | Required interpretation |
|---|---|
| TransportAccepted | Provider reported acceptance, not proof of final state, guard enforcement, or attendee delivery |
| Rejected | Definitive rejection without an applied mutation under the validated operation semantics |
| GuardedConflict | Validated atomic guard rejected the stale operation, requiring renewed assessment |
| Unknown | Dispatch may have had effects or available evidence cannot establish a definitive result |

**[SPEC]**
- Ambiguous transport failures, lost responses, post-dispatch timeouts, and crash windows MUST be Unknown unless stronger evidence establishes otherwise.
- The initial executor MUST make at most one mutation dispatch per selected item and MUST NOT automatically retry writes.
- HTTP handlers, SDK middleware, resilience policies, proxies under application control, and hedging MUST NOT replay mutations.
- Read retries MAY be bounded and separately recorded without granting write retry permission.
- A later retry capability MUST be executor-owned and require renewed exact approval, current admission, a fresh compatible guard, and a durable new attempt record.
- Unknown MUST never cause automatic mutation replay.
- Conflict or rejection MUST NOT cause a hidden rebase, force-write, or broadened patch.
- Dependent subsequent dispatches MUST pause on unknown effects.
- Independent remaining items MAY continue only under the approved partial-execution policy and fresh checks.

**[SPEC]**
Reconciliation MUST read the same bound provider identity and compare the approved postconditions with the observed state.
- State verification MUST distinguish expected state observed, different state observed, incomplete observation, and unavailable readback.
- Attribution MUST separately distinguish proven attempt attribution where available, compatible but unproven attribution, and unresolved attribution.
- Attendee delivery MUST separately distinguish evidenced delivery, evidenced failure, and unknown delivery.
- Matching readback MUST NOT prove that this attempt caused the state, that an atomic guard worked, or that notifications were delivered.
- Readback of a different mailbox copy or recurrence target MUST NOT verify the approved resource.
- Reconciliation MUST retain raw evidence references, observation time, validator, comparison version, and discrepancies.
- Human resolution of an unknown hold MUST record identity, evidence, decision, and any newly approved operation without rewriting the original attempt.
- A later correction or revert MUST be a new guarded approved change against current state, not rollback by overwriting subsequent edits.

## 13. Errors, reports, and privacy

**[SPEC]**
Errors MUST be typed decisions with retained evidence, not successful empty results or unstructured exception text.
- Distinguish invalid input, incomplete discovery, unresolved intent, unsupported category, unavailable rules, denied admission, expired approval, stale source, storage failure, cancellation, and unknown effect.
- Expected holds MUST return actionable questions or reasons without claiming that a mutation occurred.
- Reports MUST account for every discovered item and every selected item, including skipped, cancelled, invalidated, conflicted, rejected, accepted, verified, and unknown outcomes.
- Reports MUST retain original plan and approval references and keep state verification separate from transport and delivery dimensions.
- Human-readable rationales MUST distinguish observed facts, model hypotheses, organizer confirmation, and remaining uncertainty.
- Sensitive meeting text and attendee identities MUST NOT appear in default diagnostic logs or exception messages.
- Evidence storage MUST use access controls, integrity checks, and retention rules appropriate to meeting data and the operator's authorization.
- Model inputs MUST use only explicitly permitted fields, with evidence references preferred over unnecessary content duplication.
- Retention deletion MUST not leave an approval appearing verifiable when its required evidence has been removed.

## 14. Acceptance matrix

**[SPEC]**
Acceptance MUST test behavior through Core interfaces and distinguish deterministic proof from provider evidence.

| Area | Required meaningful acceptance evidence |
|---|---|
| Time arithmetic | Golden cases for each preservation mode, date boundaries, locality exceptions, effective-rule changes, gaps, overlaps, elapsed duration, and invalid inputs |
| Reproducibility | Same inputs produce identical assessments and canonical bytes across culture settings and restart, with approved canonicalization fixtures |
| Unknown values | Missing organizer, attendees, validator, rules, or baseline cannot become permissive defaults or no-change conclusions |
| Discovery | Multiple pages, overlapping windows, duplicates, detail failure, throttling, cancellation, and budget exhaustion preserve complete accounting and deny incomplete approval |
| Intent | Versioned synthetic cases exercise false assumptions, essential questions, evidence references, and positive and negative rationales separately from malformed model output |
| Admission | Planned, Inconclusive, ObservedFail, missing artifacts, scope mismatch, and tampered evidence all deny dispatch |
| Plan integrity | Every authorization-relevant dependency change alters or invalidates the plan, while presentation-only rendering cannot change effects |
| Approval | Wrong principal, wrong mailbox, changed selection, expired approval, reconnect, and partial invalidation cannot widen authorization |
| Journal | Fault injection before and after every durable transition proves pre-dispatch ordering, idempotency, and unknown holds |
| Concurrency | Competing workers, expired leases, fenced owners, and duplicate run requests cannot produce duplicate dispatch |
| Recovery | Cancellation and crashes before preparation, before dispatch, after possible dispatch, and after response preserve correct restart behavior |
| Transport | Accepted, rejected, validated conflict, lost response, ignored guard, and post-dispatch storage failure retain distinct evidence and never trigger hidden replay |
| Reconciliation | Matching state from an independent actor cannot establish attempt attribution, guard correctness, or notification delivery |
| Recurrence | Each proposed strategy covers history, exceptions, cancellations, future-only boundaries, effects outside discovery, unbounded series, and notification scope |
| Privacy | Diagnostics and reports enforce field permissions, evidence access, and retention without leaking meeting content |

**[SPEC]**
- Unit and property tests MUST establish deterministic invariants with injected clocks and rule artifacts.
- Journal integration tests MUST use the real storage adapter to prove transactions, uniqueness, concurrency, and crash recovery semantics.
- Simulator tests MUST exercise adapter failures and uncertainty without being promoted to live provider evidence.
- Live experiments MUST separately establish exact deployment/endpoint/category guard behavior, identifier semantics, recurrence effects, and notification behavior.
- Passing offline tests MUST NOT enable live writes.
- User evaluation MUST establish that organizers can understand unchanged, unsupported, unresolved, and selected meetings before approving exact effects.

## 15. Delivery interpretation and open decisions

**[NOTE]**
The present repository is not evidence that these completed-system requirements are satisfied.
Existing contract names may inform implementation, but this document does not inventory current code or preserve its interface shapes.
Generalized plan amendments, a changed-meeting history screen, and revert UX are later capabilities rather than prerequisites for the initial executor.

**[?]**
Before enabling each capability, owners must resolve and document the authoritative four-province locality policies, approved rule artifacts, evidence review lifecycle, approval lifetime, operational budgets, and data retention policy.
Straightforward recurrence additionally needs a defined proven mutation strategy with bounded effects, preserved history, explicit exception handling, and validated notifications.
No unresolved item in this section grants a default policy, a safe guard assumption, or permission to write.

## 16. Changelog

**[NOTE]**
- **1.0.1 · 2026-10-16:** Aligned source references with specification 03 version 3.2 and removed the resolved product-scope reconciliation warning.
- **1.0.0 · 2026-10-16:** Defined the completed Core module, deterministic decision interfaces, exact planning and approval, reviewed provider admission, durable one-dispatch execution, recurrence obligations, and acceptance evidence.
