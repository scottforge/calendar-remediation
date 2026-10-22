# Calendar Remediation Product Contract
**Version 1.0.1** · Calendar Remediation · 2026-10-17 · HADS 1.0.0 · Product target and acceptance contract

---

## AI READING INSTRUCTION

Read every **[SPEC]** block as a product requirement, not proof of implementation, provincial policy, or provider behavior.
Read **[NOTE]** blocks for context and delivery distinctions.
Treat **[?]** blocks as unresolved decisions that cannot grant permission to write.
Read the acceptance matrix alongside the scope definitions before describing a pilot, experiment, or release as complete.

---

## 1. Purpose and authority

**[SPEC]**
This contract is authoritative for product audience, scope, workflows, user-visible safety, and release acceptance.
- MUST, MUST NOT, SHOULD, and MAY mean required, prohibited, recommended, and optional behavior.
- The product helps organizers review their meetings and explicitly approve evidence-backed corrections to confirmed scheduling intent.
- Requirements describe the target and its admission gates, not an inventory of completed features.
- The [Core contract](core-contract.md) is authoritative for deterministic decisions, algorithms, plans, approval, executor authorization, and durable recovery.
- The [Graph adapter contract](graph-contract.md) is authoritative for provider ports, evidence, adapter behavior, and category admission.
- The [Microsoft Graph Calendar API contract](ms-graph-calendar-api-contract.md) is authoritative for the researched API profile, HTTP details, and distinctions between documented semantics and application constraints.
- Conflicts between contracts MUST be recorded and reconciled explicitly before enabling the affected capability, rather than silently overriding another contract.
- No historical document or local design reference is required to interpret this product contract.

## 2. Audience and problem

**[SPEC]**
- Users are self-service meeting organizers in British Columbia, Alberta, Saskatchewan, and Manitoba, abbreviated BC, AB, SK, and MB.
- Access is delegated to the signed-in user's own default calendar in one configured Entra tenant.
- Every organizer-owned meeting within the declared date window is a review candidate, including unaffected and previously unassessed meetings.
- Attendee copies do not authorize changes on an organizer's behalf.
- Organizers outside these four provinces and support-agent operation are outside product scope.
- Province identifies the audience but may not identify the applicable civil-time rules.
- The organizer MUST confirm city or community where locality-specific exceptions matter.
- Another locality may be the meeting's scheduling anchor even when the organizer belongs to the four-province audience.

**[NOTE]**
The expected population is a few hundred employees who may use the tool once.
First-use effort, support, and decision quality therefore matter as much as implementation throughput.

## 3. Product invariants

**[SPEC]**
- UTC storage does not establish UTC intent, and a zone label, device zone, mailbox default, current display, or model confidence does not establish organizer intent.
- An affected display alone does not justify changing stored scheduling values.
- Facts, model hypotheses, organizer confirmations, policy evidence, and provider observations MUST remain distinguishable.
- Deterministic calculations MUST turn confirmed intent and validated rules into exact effects without network calls, model calls, storage writes, or implicit system-clock reads.
- The model proposes interpretations and questions, while the organizer confirms intent and approves exact effects.
- Core's executor alone authorizes product mutation attempts and any later admitted retries.
- Read-before-write MUST NOT substitute for a proven atomic server-enforced write guard.
- No category is executable until its own compatible evidence and all other gates pass.
- No natural-language instruction, selection, standing authority, or restored UI session constitutes approval.
- Fixture success, simulator behavior, HTTP acceptance, and matching readback MUST NOT be promoted into proof of live guard enforcement or attendee delivery.
- Unsupported, unresolved, unassessed, and unknown cases MUST remain visible rather than acquire permissive defaults.

## 4. Delivery scopes and admission boundaries

**[SPEC]**
The read-only pilot, restricted write experiment, and required first release are distinct acceptance boundaries.

| Boundary | Required capability | Exit condition |
|---|---|---|
| Read-only utility pilot | Complete bounded discovery, manual review, user-requested Copilot-assisted selection, express selection, confirmed intent, deterministic assessments, and exact non-executing previews | Employee utility, policy, content privacy, identity, and first-use evidence accepted for the pilot, with zero provider mutations |
| Restricted first write experiment | Selected organizer-owned, timed single-instance meetings with internal attendees, through the shared exact approval and guarded executor | Product, policy, identity, atomic guard, durability, recovery, and internal-attendee side-effect gates pass for that exact category |
| Required first release | All three journeys, single meetings, and independently admitted straightforward recurrence | Release acceptance passes, including the defined recurrence strategy and its category evidence |

**[SPEC]**
- The read-only pilot MUST precede a controlled operational writer and SHOULD test utility before broad execution-workflow development.
- The atomic-write feasibility experiment SHOULD run first or in parallel with read-only utility validation.
- A production execution journal is not a prerequisite for a strictly read-only utility test.
- Durable exact approval and attempt evidence are prerequisites for every operational write.
- A single-instance experiment MUST NOT be called the complete MVP or required first release.
- Recurrence prevalence informs which patterns to prove first, not whether recurrence belongs in the required release.
- If no supported write path passes, automatic-write development MUST stop for an explicit product go/no-go decision.
- Read-only validation may continue, but adopting a read-only end product requires an explicit new scope decision.

### 4.1. Initial write exclusions

**[SPEC]**
- The restricted experiment excludes non-organizer copies, shared calendars, delegates, application-permission access, resources, external-attendee categories, all-day events, and all recurring items.
- Unknown ownership, attendee classification, resource or delegate status, rule evidence, or intent blocks mutation.
- Internal-attendee classification requires accepted tenant or directory evidence, not an address suffix alone.
- Operator-marked sensitive meetings remain excluded until an accepted handling policy admits them.
- Sensitivity is operator-supplied rather than inferred from names or meeting text.
- Excluded items remain in the review accounting with explicit reasons and in-app dispositions.
- External attendees and all-day writes require separate category evidence before any later admission.
- Resources and delegates require separate product scope decisions as well as evidence.

## 5. Onboarding and locality confirmation

**[SPEC]**
- Reuse a valid cached delegated session when available, otherwise request interactive system-browser sign-in and required consent.
- Verify account, configured tenant, mailbox, default-calendar ownership, and delegated access before discovery.
- Outlook's local sign-in is not a separate authentication mechanism for this product.
- Suggest the detected time zone, then require confirmation or selection of BC, AB, SK, or MB before opening the review dashboard.
- Ask for city or community whenever province alone leaves applicable rules or exceptions unresolved.
- Explain a mismatch between detected zone and confirmed locality and request clarification.
- Do not silently override the locality choice or change calendar settings.
- Explain the selected date scope, content permission, preservation choices, and current read-only or write-admitted status.
- Make reauthentication, sign-out, cache clearing, and cleanup behavior understandable to a first-time user.

## 6. Complete bounded discovery and shared list

**[SPEC]**
- Default to the next 90 days, with an optional explicit date range no longer than the Graph profile's 366-day application limit.
- Capture explicit discovery boundaries, organizer/calendar binding, evaluation instant, and future-only execution cutoff.
- Show the complete declared organizer-owned meeting list and support scrolling through every entry.
- Retain unaffected, display-only, unsupported, unresolved, and unassessed entries alongside proposed corrections.
- Follow all valid continuation pages and complete required detail reads before claiming scope completeness.
- Page failure, required-detail failure, cancellation, or exhausted item, page, read, or time budget makes the declared scope incomplete and blocks its approval.
- Offer increased budgets or newly declared narrower windows rather than silently truncating or relabeling an incomplete scope.
- Deduplicate observations only when identity, version, and relevant evidence agree.
- Differing versions, disappeared items, and unresolved identity mappings remain visible for reassessment.
- A complete traversal is bounded observation, not an atomic mailbox snapshot.
- A completed traversal may contain unresolved items, but those items cannot become executable.

**[SPEC]**
Every entry MUST have a reviewable classification with reasons and evidence limits.

| Classification | Meaning |
|---|---|
| Correction proposed | Confirmed intent and validated rules produce supported writable differences, with admission checked separately |
| No change recommended | Sufficient evidence establishes that the supported scheduling values already satisfy confirmed intent |
| Display-only difference | Representation differs without a required stored scheduling correction |
| Needs clarification | Required evidence or an answer is missing or contradictory |
| Unsupported | The category or requested effect exceeds admitted capability |
| Unassessed | Assessment has not run or could not complete |

## 7. Three review journeys

**[SPEC]**
Manual, Copilot-assisted, and express review are paths through shared pages, not separate page sets.

| Path | Page flow |
|---|---|
| Manual | Shared meeting list → shared meeting detail → shared change preview and final approval |
| AI-assisted | Shared meeting list, with user-requested AI selection → same shared meeting detail → same shared change preview and final approval |
| Express button | Shared meeting list → shared change preview and final approval, skipping meeting detail |

- The three paths use the same assessments, exact preview, final approval, and executor.
- Express review MAY open shared meeting detail on demand, and MUST route cases requiring clarification to shared meeting detail; its normal button path skips detail.
- Selection and approval MUST be separate actions.
- Users may deselect items before approval and choose no change, skip, or not sure without pressure to resolve a case.
- Structured review MUST remain usable without a model request and when the model is unavailable or unusable.

### 7.1. Manual and guided review

**[SPEC]**
1. Select meetings from the complete list and open the shared meeting detail page to review each through structured controls.
2. Show relevant meeting evidence beside one contextual question at a time.
3. Label observed facts, proposed interpretations, contradictions, and organizer confirmations separately.
4. Confirm intended time, locality assignment, preservation choice, and any per-meeting override.
5. Assess the confirmed answer against pinned rules, current evidence, eligibility, and admission.
6. Compare no update with the proposed correction using actual dates, named localities, local times, offsets, instants, elapsed duration, and notification consequences.
7. Keep unresolved answers in the application with explicit missing fields, including a missing rule input instead of an invented dated preview.
8. Review the complete proposed batch and explicitly approve its exact effects only when execution is admitted.

### 7.2. Copilot-assisted selection

**[SPEC]**
- The organizer explicitly requests assistance to find meetings to update to confirmed local time.
- Before that request, the UI MUST NOT imply that Copilot has assessed the list.
- Requested list-wide review accounts for every candidate with a short evidence-backed rationale and uncertainty, including why unselected meetings were not recommended.
- No change recommended MUST remain distinct from could not assess, unsupported, and not yet assessed.
- Candidate selection and likely intent are proposals that the organizer can change or reject.
- For each selected meeting, the organizer reviews and confirms the proposed intent on the same shared meeting detail page used by manual review.
- The organizer confirms a concrete interpretation before deterministic computation and reviews every selected effect on the shared change preview page before exact final approval.
- A request such as “fix all meetings” opens review and MUST NOT dispatch a mutation.

### 7.3. Express batch review

**[SPEC]**
- Provide checkbox selection, scope-wide select all, deselection, complete final review, and optional per-meeting details.
- Select all covers the complete declared discovery scope, not the current page, viewport, or model-selected subset.
- Confirm any common intent, source-rule assumptions, and evidence supporting the shared baseline.
- Contradictory clues, weak baseline evidence, and possible prior corrections return to guided per-item review.
- Final review lists every selected identity, exact change, override, and known side effect or uncertainty.
- Explain excluded selections explicitly, since selecting an unsupported, unresolved, unassessed, or non-admitted item cannot make it executable.
- Express review reduces navigation, not evidence, confirmation, or approval requirements.

## 8. Preservation choices and consequences

**[SPEC]**
- Show a visible preservation default and require organizer confirmation before planning rather than hardcoding one global choice.
- Record the chosen value and provenance per plan item and display overrides beside the default in final review.
- Preserve elapsed duration by default and show the resulting local end.
- Local-end preservation is an advanced capability only if separately implemented and explicitly selected, with any duration change shown.
- For a nonexistent local time, show the gap and require a valid corrected time.
- For an ambiguous local time, show candidate instants and offsets and require an explicit supported selection.
- Never resolve gaps or overlaps through implicit platform defaults.

**[SPEC]**
| Choice | User-confirmed invariant | Required consequence display |
|---|---|---|
| Keep my confirmed local time | Intended wall-clock start in the confirmed city or community | Date-specific target instant, offset, local end, and difference from no update |
| Keep another locality's anchor | Intended wall-clock start in the confirmed anchor city or community | That locality's applicable rules and the resulting organizer-local time |
| Keep the absolute instant | Absolute start instant | Unchanged instant and any changed local display |

## 9. Intent, policy, rules, and provider evidence

**[SPEC]**
These evidence responsibilities are independent and MUST NOT substitute for one another.

| Evidence | Establishes | Does not establish |
|---|---|---|
| Organizer confirmation | Desired local time, other-city anchor, or absolute instant, with scope and provenance | Provincial law, provider adoption, or mutation safety |
| Jurisdiction policy | Approved authoritative sources, geographic applicability, effective boundaries, and review status | Organizer intent or deployed software behavior |
| Source and target rule artifacts | Reproducible date-specific calculations with retained content or content-addressed artifacts | Legal authority merely from an artifact version label |
| Provider observations and capability evidence | Stored observations, supported mappings, tested guards, and category-specific effects | Intent from UTC representation or policy adoption from a zone identifier alone |

**[SPEC]**
- Establish BC, AB, SK, and MB policy independently, including intra-province exceptions and effective instants.
- Retain authoritative source content or retrievable evidence with publication/retrieval context and review status before enabling a locality policy.
- The Alberta government time guidance identified during planning is a candidate source to validate, not an admitted policy record or evidence for another province.
- Never invent legislation, effective dates, offsets, provider zone names, or transition behavior.
- Pin exact applicable source and target rule content, hashes, and mapping provenance rather than rely on host defaults or a version label alone.
- A reconstructed historical baseline is a candidate interpretation requiring evidence and confirmation.
- Direct confirmation of intended local values may establish a target without reconstructing historical intent when target rules, current source evidence, and all other gates suffice.
- Do not claim that direct confirmation proves original scheduling intent.
- Calendar-level assignments require explicit confirmation, recorded scope, and precedence, with contradictions resolved visibly.
- Missing or contradictory evidence required for the chosen decision path blocks correction rather than becoming no change recommended.

**[SPEC]**
The following are arithmetic acceptance cases for hypothetical confirmed rules, not claims about provincial law.

| Inputs and choice | Required result |
|---|---|
| Stored 16:00Z, evidenced source UTC-07 baseline, confirmed 09:00 local, target UTC-06, preserve local start | Propose 15:00Z and retain 09:00 target-local start |
| Same source, preserve absolute instant | Keep 16:00Z and show 10:00 target-local start |
| Stored 15:00Z, independently confirmed 09:00 under target UTC-06, all planned fields already satisfy intent | No-op |
| Stored UTC value without independent intent evidence | Ask a guided question rather than infer a correction |

## 10. Agent grounding and privacy boundary

**[SPEC]**
- Use one selected model integration behind a Core-owned proposal contract, with offline recorded responses for evaluation.
- Assemble only the smallest permitted calendar/date/event scope needed for the requested review.
- Inputs may include permitted structured fields, subject, description, rule summaries, and evidence identifiers.
- Subject and description require a separately defined and admitted Graph content-read profile, explicit permission/content limits, and version binding to the safety snapshot.
- Distinguish content not requested, absent or empty, unavailable, redacted, truncated, and successfully read.
- Changed or missing version binding holds any assessment that depends on consistent joined content and safety evidence.
- Disclose content selection, model destination, and retention behavior before transmission.
- Resolve provider logging, training use, residency, authentication, and retention terms before a real-data pilot.
- Do not promise local-only processing when a remote model is selected.
- Never send access tokens, refresh tokens, authentication headers, credential caches, or unrelated calendar data to the model.
- Related email retrieval is outside initial product scope, and the content profile does not implicitly admit attachments or Teams data.
- Treat meeting text as untrusted evidence, never instructions to invoke tools, expand scope, or approve actions.

**[SPEC]**
- Require schema-bound proposals carrying snapshot references, evidence pointers, alternative hypotheses, essential questions, candidate intent, and uncertainty.
- Reject malformed output, invented evidence identifiers, out-of-scope identities, and unsupported action fields.
- Validate proposed values deterministically before presenting an executable correction.
- Ask the organizer to confirm concrete intent with visible scheduling consequences and retain the answer separately from model output.
- Confidence scores MUST NOT determine write eligibility or replace confirmation.
- Model failure leaves a usable manual path and never becomes an inferred answer.
- Evaluate semantic grounding separately from schema validity using versioned synthetic cases and recorded organizer confirmations.
- Synthetic author labels or recorded organizer confirmations establish evaluation ground truth, not another model's interpretation.
- Track false assumptions, unsupported corrections, essential-question quality, and positive and negative rationale quality before a real agent pilot.

## 11. Exact preview, approval, and invalidation

**[SPEC]**
- Build an immutable plan containing complete scope evidence, source identities and versions, confirmed intent, preservation values, rules, policy, admission dependencies, exact changes, and expected postconditions.
- Preview derives from that exact stored plan and shows before/after dates, named localities, local times, offsets, instants, duration, mutation scope, and notification expectations or uncertainty.
- Bind exact executable content and decision evidence to a canonical versioned digest retained with the plan.
- Final approval binds the authenticated organizer and mailbox, original plan ID and digest, exact selected item IDs, confirmation time, expiry, and partial-execution policy.
- An empty selection authorizes nothing, and unselected or later-discovered items never join an existing approval.
- Dry-run follows the same assessment, plan, and preview path without provider mutation.
- Approval validation is server-side, independent of component state, and repeated immediately before each dispatch, including after pauses and durable pre-dispatch work that may cross a validity boundary.
- Revalidate ownership, source guard, confirmed intent, policy/rules, target-zone support, admission, plan/approval expiry, and future-only eligibility.
- Starting before the execution cutoff or already being in progress blocks a future-only write.
- Apply the policy effective boundary separately, with boundary-crossing cases requiring explicit supported treatment.

**[SPEC]**
| Change after planning | Required behavior |
|---|---|
| Item-specific source or answer changes | Invalidate that item and every item sharing the changed dependency |
| Shared scope, policy, rule, baseline, capability, or other shared dependency changes | Invalidate all dependent remaining items |
| Unaffected remaining selected items | Continue only if the original exact approval permits partial execution and fresh checks pass |
| Partial continuation not authorized | Pause the remaining batch for review |
| Replanning needed | Create a new immutable plan, digest, and renewed approval, retaining the original evidence |
| Circuit reconnect, restart, repeated command, or model proposal | Restore or revalidate existing durable work without creating new approval authority |

## 12. Guarded execution and user-visible recovery

**[SPEC]**
- Begin with one active run and one mutation request in flight.
- The initial executor makes at most one mutation dispatch per selected approved item, with manual review or replanning after rejection or conflict.
- Before dispatch, durably retain intent, exact approved plan/selection, patch, guard, attempt identity, and the in-flight transition.
- If required pre-dispatch persistence fails, send nothing.
- Send only approved fields through the proven guarded operation, without incidental body, attendee, recurrence, or notification-field round trips.
- Disable HTTP, SDK, resilience-handler, and authentication-handler mutation replay and write hedging.
- A mutation throttle response is unknown for safety, and `Retry-After` does not authorize replay.
- Timeouts, lost responses, ambiguous server failures, and post-dispatch journal failures enter reconciliation and unknown holds.
- Successful HTTP acceptance requires independent organizer-side readback and tested attribution evidence before verified-applied reporting.
- Matching desired fields does not prove this attempt caused them, and an immediate unchanged read does not prove a request cannot still complete.
- No cross-event transaction or exactly-once execution guarantee is promised.

**[SPEC]**
| User-visible outcome or control | Required meaning and next action |
|---|---|
| Verified organizer-side change | Retain successful-response attribution and approved-postcondition evidence, separately from delivery status |
| Rejected or failed | Retain definitive evidence and offer manual review without automatic redispatch |
| Conflict | Evidence establishes a competing change against approved inputs, requiring a new plan |
| Unknown hold | Effect or attribution remains uncertain, retain evidence and require investigation without replay |
| Skipped, no change, or canceled before dispatch | Record disposition without implying provider mutation |
| Stop or run-budget expiry | Stop new dispatch and preserve reconciliation of any possibly dispatched item |
| Restart or circuit loss | Recover durable plans and progress, reconcile in-flight work, expose holds, and revalidate remaining approvals |
| Item-specific failure | Independent valid items may continue only under the original approved partial-execution policy |
| Journal, authentication, shared-policy, provider, or uncertain-scope failure | Pause the run |
| Run closure | Distinguish successful, partial, canceled, and closed-with-unresolved, preserving unknown item status |

**[SPEC]**
- Display verified organizer-side outcomes separately from invitation delivery, attendee response, and resource processing.
- Do not promise receipt, acceptance, room availability, resource rebooking, or notification suppression from a successful update.
- Withhold category admission when unresolved side effects prevent informed approval.
- Any later admitted retry remains Core-owned and requires renewed exact approval, current admission, a fresh compatible guard, dependency revalidation, and a durable new attempt record.
- Revert or compensation is a new approved guarded change against current state, never an automatic rollback or overwrite of later edits.
- Restoring fields cannot undo notifications or guarantee reversal of attendee effects.

## 13. Required straightforward recurrence

**[SPEC]**
- The required first release includes evidence-qualified straightforward recurring series alongside single meetings.
- Define supported patterns, ranges, eligible exceptions, mutation targets, guards, bounded effects, and notification behavior before claiming support.
- Distinguish single instances, occurrences, exceptions, and series masters.
- Obtain required master, exception, cancellation, and occurrence-identity evidence rather than infer a complete series from finite discovery expansion.
- Select master update, occurrence update, splitting, or another supported strategy only after live proof of its exact effects.
- Preserve historical occurrences, deliberately moved exceptions, and canceled identities, without incidental revival or resetting.
- Future-only eligibility covers every actual effect, including effects beyond the discovery horizon.
- Broader effects require a new declared scope, complete preview, and exact approval rather than finite-instance approval hiding a wider mutation.
- Transition-spanning or unbounded series remain unresolved until the chosen strategy proves their bounded effects and required future behavior.
- Complex or unproven recurrence remains visible with explicit reasons and no automatic correction.
- Single-instance guard or notification evidence MUST NOT admit a recurring operation.
- If no recurrence strategy passes, record the unmet requirement and keep the required first release blocked pending an explicit new product decision.

## 14. Operating bounds

**[SPEC]**
| Bound | Status and required interpretation |
|---|---|
| Next 90 days | Settled default review horizon |
| Optional range up to 366 days | Application-profile request limit, not a Graph-wide maximum |
| Approximately 100 meetings per organizer | Expected design workload, not permission to truncate and not a universal instance cap |
| Discovery time, pages, items, detail reads, expanded recurrence instances, selected writes, total run duration | Explicit measured budgets with consumption and terminal reasons recorded |
| One active run and one mutation in flight | Initial execution constraint |
| Graph request byte limits and deadlines | Technical profile constraints from the Graph contracts, also bounded by enclosing operation budgets |

**[SPEC]**
- Measure representative meeting density, recurrence expansion, pagination, detail cost, throttling, and organizer review time before setting operational budgets.
- Record user-facing meeting counts and expanded-instance counts separately.
- Earlier 10-write and 15-minute suggestions are not universal acceptance thresholds.
- Reaching a limit MUST produce explicit incomplete or stopped status, not an empty success or a hidden omission.

**[?]**
- Discovery, recurrence-instance, selected-write, and run-duration budgets remain provisional until representative measurements are accepted.
- Approval lifetime and quantitative utility/release thresholds require explicit acceptance decisions before the applicable pilot or write admission.
- No unspecified budget or threshold may be invented or treated as evidence that a gate passed.

## 15. Platform and deployment

**[SPEC]**
- The selected .NET application architecture is C# on .NET 10 LTS with Blazor Interactive Server, Razor markup, and CSS.
- Core owns provider-independent decisions and execution authorization, Graph implements provider adapters, and Host composes identity, model access, presentation, and persistence.
- Keep UI event handling, agent workflows, application logic, and tests in C#, without a required custom JavaScript application or Node/npm build toolchain.
- Preserve framework-managed browser scripts, SignalR circuits, and reconnect behavior.
- Keep Graph credentials on the backend and out of the browser and model.
- Rehydrate durable work after circuit loss, since a circuit is not approval authority or the execution record.
- Keep Core platform-neutral without WPF, WinForms, or Outlook COM dependencies.
- Use the .NET build/publish pipeline, serviced dependencies, dependency locks, and reproducible rule artifacts.
- Detailed technical interfaces and provider HTTP belong to the companion contracts rather than incidental demo layout requirements.

**[NOTE]**
The current repository has Core and Graph implementations, a synthetic `/` workspace, and a separate read-only `/connect` page.
The synthetic workspace does not establish a live review workflow or write admission.
Browser-based live connection works cross-platform, using configured tenant/client GUIDs, a public-client `http://localhost` callback, and no client secret.
Windows persists a protected account cache, while non-Windows tokens remain in memory and require sign-in again after restart.
The current local host explicitly binds HTTP loopback, prefers port 5009, and selects an available port if needed, so startup output identifies the actual address.
These current behaviors do not establish employee rollout support, hosted delivery, or completion of this contract.

**[SPEC]**
- Choose delivery early using representative employee first use and operational evidence.
- Compare local Blazor, hosted .NET, and existing Power Platform support, including Power Pages or Power Apps with PowerShell where organizational capabilities already exist.
- Compare consent, model access/authentication, licensing, executor identity, installation, updates, support, isolation, retention, recovery, and cleanup.
- The .NET architecture is settled for this application, while any selected alternative delivery requires explicit technical reconciliation.
- Local delivery requires loopback confinement, public-client browser sign-in with PKCE, account/tenant/session binding, protected per-user persistent caches where used, and no embedded client secret.
- Validate authenticated sessions, origin checks, and antiforgery protection for all execution actions.
- Hosted delivery requires explicit identity, user/session isolation, storage, and operating-ownership decisions before implementation.
- Select least-privileged permissions through the validated Graph data profiles, with explicit broader-read requirements/consent and write consent only for admitted write phases.
- Do not turn missing fields into automatic permission escalation.
- A PowerShell or alternative executor still requires exact durable approval, atomic guards, per-attempt evidence, duplicate-invocation safety, and unknown holds.

**[SPEC]**
- Build and test natively on Linux and Windows, format on Ubuntu, then package and smoke-check self-contained artifacts for each claimed runtime.
- Distribute the complete publish directory with Razor UI and framework assets.
- Shell and asset smoke tests do not establish identity callbacks, protected cache, mappings, or Graph behavior.
- Before a local Windows pilot, prove launch, sign-in, callback, session binding, cache protection, zone mapping, and Graph access on Windows.
- Cross-publishing does not prove native runtime behavior.
- Non-Windows local end-user distribution remains deferred until its browser, cache, packaging, runtime, and support gates pass, regardless of current development/connection capability.
- Plan runtime and dependency upgrades before support ends.

## 16. Reporting and data handling

**[SPEC]**
- Provide one versioned JSON plan/audit format and a readable in-app preview and results view.
- Account for every discovered and selected item, including review dispositions, intent provenance, proposed effects, exclusions, invalidations, failures, conflicts, and unknown holds.
- Keep transport acceptance, organizer-state verification, attempt attribution, and attendee-delivery certainty separate.
- Retain original plan, approval, attempt, and reconciliation evidence for the agreed retention period.
- Protect stores, exports, diagnostic files, and content evidence as calendar data using appropriate access and integrity controls.
- Define retention, deletion, export access, model transmission, and crash-recovery retention before real-data review.
- Keep raw prompt logging off by default and explicitly define any diagnostic content capture.
- Omit tokens, unnecessary subjects/descriptions, and attendee identities from default operational logs and exception text.
- Retention deletion MUST NOT leave an approval appearing verifiable after required evidence is removed.
- A changed-meeting history screen is deferred, but durable approval/outcome records and in-run results are mandatory before writes.

## 17. Evidence gates and measurable acceptance

**[SPEC]**
Acceptance MUST retain named scenario results, measured outcomes, reviewed artifacts, and explicit holds for failures or missing evidence.
- Offline tests prove deterministic behavior and boundaries, while controlled-tenant tests prove provider-specific semantics.
- Coverage reports with documented exclusions support investigation but do not replace scenario results or provider gates.
- A gate passes only for its evidenced deployment, identity/profile, category, operation, guard, and relevant transport/configuration versions.

**[SPEC]**
| Gate | Required measurement or scenario evidence | Admission consequence |
|---|---|---|
| Four-province locality policy | Reviewed authoritative sources, city/community exceptions, effective boundaries, pinned rules and hashes, mapping and independent provider-adoption evidence for each enabled locality | Missing evidence holds affected calculations and writes |
| Onboarding and first use | Cached and interactive sessions, wrong account/tenant, province/community confirmation, zone mismatch, consent, model access, reauthentication, cleanup, and observed employee support effort | Required before representative real-data pilot and selected deployment rollout |
| Complete list and scale | Approximately 100-meeting review, all-page scrolling, scope-wide select all/deselection, dense recurrence expansion, boundaries/crossing events, duplicates, changed/disappeared IDs, detail failure and budget exhaustion | Incomplete scopes cannot be approved and no viewport truncation is accepted |
| Shared journeys | Manual, requested assisted, and express flows produce the same assessment/approval path, with per-candidate positive/negative reasons and explicit unsupported/unassessed states | Required read-only product behavior and first-release workflow acceptance |
| Deterministic intent and arithmetic | Golden arithmetic cases above, four-province locality coverage, other-city anchors, no-op/display-only cases, prior-correction contradictions, gaps/overlaps, duration, overrides, and missing-evidence holds | No fabricated correction or implicit ambiguity resolution accepted |
| Agent grounding and content | Versioned synthetic intent labels, false-assumption/unsupported-correction counts, essential-question quality, invented IDs, scope escape, prompt injection, version mismatch, truncation, malformed output, and model-outage fallback | Required before real agent pilot, separate from schema conformance |
| Employee utility | Compare assisted review with a simple structured form using recorded organizer intent and verified calculations, measuring correct decisions, completion time, question count, abandonment, and agreement with unsupported corrections | Explicit utility decision before broad writer investment, synthetic results alone insufficient |
| Model privacy and access | Approved provider destination/authentication, field/content profile, logging/training/residency/retention terms, sensitivity handling, safe rich-text representation, and transmission checks | Required before real meeting content is transmitted |
| Atomic write feasibility | Named owner, bounded experiment and stop date, exact endpoint/API/tenant/identity, actual source guard, competing edit, valid/stale/omitted/altered guard controls, and readback proving competing values survive stale rejection | No live product writes without compatible reviewed pass evidence |
| Single-instance category | Disposable organizer-owned timed meetings with internal attendees, accepted internal/resource classification, scheduling representation and actual notification observations with retained artifacts | Admits only the exact restricted category, attendee-free evidence insufficient |
| Exact approval and invalidation | Wrong identity/mailbox/digest/selection, empty selection, per-item/shared changes, expiry before/after persistence, pause/reconnect, partial continuation, and immutable replanning | No widened authority or stale approval accepted |
| Durable execution and transport | Real-store transaction/fault tests around each transition, duplicate commands/workers, cancellation, restart, rejected/throttled/lost responses, journal failure, and configured middleware proving zero hidden extra mutation requests | Required before operational writes |
| Unknown recovery and reporting | Desired fields from another actor, immediate unchanged read, lost attribution, in-flight restart, shared-failure pause, item-specific continuation, manual hold resolution, and closed-with-unresolved results | No unknown converted to success/conflict without evidence and no blind replay |
| Straightforward recurrence | Named patterns/ranges/strategy, exact guards/targets, history, moved exceptions, cancellations, transition boundaries, unbounded series, beyond-horizon scope, notifications, and recovery in controlled tenant | Independent gate required for first release, failure leaves release blocked |
| Platform and deployment | Native Linux/Windows build/test, complete self-contained package and asset smoke checks, intended-platform identity/Graph/session/circuit-loss tests, plus selected deployment privacy/support evidence | Claims limited to actually tested deployment/runtime behavior |

**[SPEC]**
- Time-box the atomic guard investigation before broad writer development and retain the explicit pass, fail, or inconclusive decision.
- If Graph fails, investigate supported alternatives only within a separately bounded effort, with new authentication/deployment and guard evidence.
- Do not promise an alternative exists or build one speculatively.
- Internal and external attendees, resources, recurrence, and other extensions need independent side-effect evidence rather than inherited admission.
- Passing tests or a reviewed provider pass is necessary evidence, not a substitute for all remaining product gates.

## 18. Non-goals and deferred capabilities

**[SPEC]**
- No autonomous remediation, standing delegated batch authority, background daemon, or general calendar assistant.
- No attendee-side organizer updates, support-agent operation, shared/delegate calendars, application-permission access, or organizers outside BC/AB/SK/MB.
- No related-email retrieval in the initial product.
- No requirement for optional Copilot hosting integration, multiple model providers, a general agent framework, or additional export formats.
- No automatic mutation retries, generalized amendment lineage, changed-meeting history screen, or one-button revert in the initial executor/release.
- No resource/delegate support without a new scope decision and separate evidence.
- No automatic correction of complex or unproven recurrence.
- No incidental demo navigation, component arrangement, or visual layout is a product requirement.
- Deferral of a history screen or revert UI never defers the durable evidence required for execution and recovery.

## 19. Decision register

**[SPEC]**
The following decisions are settled product or architecture boundaries and MUST NOT be reopened implicitly by a pilot implementation.

| Decision | Accepted boundary |
|---|---|
| Audience and access | Four-province self-service organizers, own default calendar, delegated access in one configured tenant |
| Candidate set | Every organizer-owned meeting in the declared window, not only suspected corrections |
| Journeys | Manual, user-requested Copilot-assisted, and express review over one shared workflow |
| Default horizon and workload | Next 90 days, optional explicit date range, approximately 100 meetings as a design workload |
| Scheduling authority | User-confirmed intent, deterministic Core calculation, exact organizer approval, Core-only execution authorization |
| Release scope | Single meetings and independently admitted straightforward recurrence, with a separate restricted single-instance experiment |
| Initial execution | One active run, one mutation in flight, at most one dispatch per approved item, manual replan and unknown holds |
| .NET application boundary | .NET 10 Interactive Server, Core/Graph/Host responsibilities, backend identity/model/persistence |
| History and revert | Deferred UI, mandatory durable evidence, later compensation requires a new guarded approval |

**[?]**
These choices require evidence or an explicit bounded decision rather than a permissive default.

| Open decision | Required resolution | Blocks |
|---|---|---|
| Four-province policies | Approved sources, effective instants, locality exceptions, rule artifacts, mappings, and provider adoption | Affected locality assessment and write admission |
| Atomic provider path | Exact-environment live guard proof and reviewed artifacts, or explicit no-go | Every operational write |
| Straightforward recurrence | Supported patterns/ranges/exceptions, operation strategy, bounded effects, guards, and notification/recovery proof | Required first release |
| Model integration and content | Provider/model/version/authentication/output mechanism, content-read profile, limits, sensitivity and privacy terms | Real-data agent pilot |
| Employee utility | Accepted comparison results and explicit quantitative success thresholds | Broad assisted-workflow/writer investment decision |
| Delivery and operating owner | Employee first-use comparison of local, hosted, and existing platform options, with identity/storage/support reconciliation | Employee rollout |
| Operational budgets and approval lifetime | Representative measurements, configured budgets, validity windows, and reviewable acceptance thresholds | Applicable pilot/write admission |
| Retention and evidence lifecycle | Access, deletion, export, crash retention, admission review/expiry and re-review rules | Real-data review and durable operational use |
| Later revert | Current-version guarded compensation and evidence preservation, including notifications that cannot be undone | Any future revert capability |

## 20. Changelog

**[NOTE]**
- **1.0.1 · 2026-10-17:** Clarified that the three review journeys are paths through shared pages, with express review normally skipping meeting detail.
- **1.0.0 · 2026-10-15:** Established standalone product authority for the four-province organizer workflow, three review journeys, evidence-qualified execution and recurrence, deployment distinctions, and measurable acceptance gates.
