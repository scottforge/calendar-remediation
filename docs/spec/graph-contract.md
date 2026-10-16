# Graph Adapter Contract
**Version 1.0.1** · 2026-10-16 · Completed-system design · Microsoft Graph v1.0 delegated calendar profile

---

## AI READING INSTRUCTION

Read every **[SPEC]** block as requirements for the completed Graph adapter, not a claim of implementation or live-provider proof.
Read **[NOTE]** blocks for rationale and **[?]** blocks for unresolved decisions.
The [researched Graph API contract](ms-graph-calendar-api-contract.md) distinguishes Microsoft documentation, application constraints, simulator extensions, and current behavior.
Never promote an application constraint, simulator result, or proposed capability into a Microsoft guarantee.

---

## 1. Authority and completion boundary

**[SPEC]**
- MUST, SHOULD, and MAY express requirements for this adapter.
- This document specifies the intended completed system, including evidence gates that remain mandatory after implementation.
- The researched API contract owns documented provider semantics, routes, projections, and wire constraints.
- This document owns the Graph module boundary, provider evidence, failure behavior, and adapter acceptance criteria.
- The companion [Core contract](core-contract.md) owns provider-independent decisions, approved plans, executor transitions, and reconciliation policy.
- Conceptual contract names below describe responsibilities and information, not frozen C# signatures or compatibility promises.
- Completion requires validated behavior and retained evidence, not merely classes, passing fake tests, or populated admission flags.

**[NOTE]**
- Specification targets self-service organizers in BC, AB, SK, and MB and require straightforward recurrence for the first release.
- Manual, Copilot-assisted, and express journeys share the same provider boundary and Core authorization.
- A restricted single-instance experiment does not satisfy the completed release scope, and every unproven category remains disabled.

## 2. Ownership and dependency direction

**[SPEC]**
| Owner | Responsibility | Boundary |
|---|---|---|
| Core deterministic domain | Scope, evidence requirements, confirmed intent, policy, calculations, assessment, plans, approval validity | No HTTP, Graph SDK, authentication, or model dependency |
| Core executor | Dispatch authority, durable-attempt workflow through injected ports, retries, invalidation, reconciliation decisions | Sole authority to initiate or repeat a product mutation |
| Graph | HTTP requests, provider parsing and normalization, provider identity and scope binding, narrow mutation construction, exchange evidence | Implements Core-owned provider ports |
| Host | Authentication, model adapter, UI, composition, selected deployment, durable-store implementation | Supplies bound credentials and persistence ports |

**[SPEC]**
- Graph MUST NOT infer organizer intent, calculate authoritative jurisdiction corrections, approve plans, select batches, or own a second executor.
- Graph MUST NOT infer account identity or tenant membership from attendee addresses.
- Graph MAY reject dispatch when supplied admission, binding, guard, or request evidence is invalid.
- Adapter checks are defense in depth, not approval authority or a replacement for durable executor checks.
- Graph returns observations and transport evidence to Core without deciding that a run is verified-applied.
- Host composition MUST prevent browser or model access to Graph credentials and product mutation capability.

## 3. Provider-facing ports and evidence flow

**[SPEC]**
| Conceptual operation | Inputs | Required result |
|---|---|---|
| Bind calendar | Account and tenant binding, permission profile, cancellation | `/me` and default-calendar observations, binding outcome, exchange references |
| Discover | Bound calendar, UTC scope and cutoff, budgets, content profile | Observations, page and detail evidence, unresolved fields, completeness record |
| Read current event | Bound mailbox and default calendar, exact event ID | Current provider observation and version evidence or explicit read failure |
| Read supported zones | Bound identity, Windows or IANA representation | Alias observations with source, representation, time, and exchange evidence |
| Dispatch time correction | Executor attempt reference, exact approved target and UTC patch, opaque guard, validated admission context | One exchange outcome and complete sanitized evidence |
| Run feasibility experiment | Separately authorized disposable-test scope and protocol | Reviewed-evidence candidates, never product admission by itself |

**[SPEC]**
- Inputs MUST bind tenant, account, mailbox, calendar, profile revision, operation, and relevant evidence references explicitly.
- A product dispatch MUST identify the durable attempt already committed by the executor.
- Graph MUST return enough evidence for the executor to persist dispatch and readback separately.
- Graph MUST NOT silently perform a follow-up write, repair a failed mutation, or invoke an arbitrary mutation callback.
- Provider observations MUST retain observation time and provenance rather than claim a transactional mailbox snapshot.

## 4. Authentication and permission profiles

**[SPEC]**
- Acquire an access token per HTTP request through the injected identity-library path.
- Bind acquisition to the configured tenant, selected account, delegated Graph audience, and explicit permission profile.
- Send `Authorization: Bearer {access-token}`, never an ID token.
- Fail closed on acquisition failure, unexpected identity, expired credentials, or changed session binding.
- Interactive sign-in and consent are explicit Host user actions, not invisible transport retries.
- Renew tokens through the identity library without silently changing accounts or scopes.
- Synthetic credentials MUST be restricted to simulator composition and MUST never authorize production requests.
- Account, tenant, or session replacement MUST invalidate reusable bindings, continuations, snapshots, identifiers, and dispatch context from the previous identity.

**[SPEC]**
| Capability | Documented delegated minimum | Application requirement |
|---|---|---|
| `/me` identity | `User.Read` | Validate selected account binding |
| Default calendar, calendar view, event reads | `Calendars.ReadBasic` | Validate actual safety-field availability under chosen profile |
| Supported time zones | `User.Read` | Validate representation and returned aliases |
| Event update | `Calendars.ReadWrite` | Explicit write consent plus all admission gates |

**[SPEC]**
- Select the least-privileged profile that meets documented data requirements.
- Missing fields under a narrower scope MUST produce unresolved evidence, not automatic permission escalation.
- A broader read profile requires an explicit data requirement and consent decision.
- Subject, description, body, and model-content requirements MUST NOT silently expand the base safety projection.
- Basic-read availability of all required safety inputs remains an evidence requirement.

## 5. Destination and route confinement

**[SPEC]**
- Production requests MUST use `https://graph.microsoft.com/v1.0` and only the method and route combinations admitted by the active profile.
- Validate method, origin, exact resource route, and allowed query shape before acquiring or attaching credentials.
- Reject user information, fragments, non-HTTPS schemes, non-default ports, unexpected authorities, and routes outside `/v1.0/me`.
- Disable automatic redirects on the credential-bearing transport.
- Never forward authorization to another origin or treat a redirect as permission to change route or identity scope.
- Preserve event IDs as case-sensitive opaque strings and escape them as a single path segment.
- Reject control characters, dot-segment routing, and encoded path interpretations that escape the admitted event route.
- URI validation MUST cover the final emitted URI, not merely an unescaped input string.
- Simulator origins require explicit isolated test composition, matching route restrictions, and synthetic credentials.
- A configurable simulator base MUST NOT turn production transport into an arbitrary-origin credential proxy.

## 6. Wire routes and projections

**[SPEC]**
All paths below are relative to `/v1.0`.
The projection requests fields without promising their presence or non-null values.

| Method and route | Purpose | Accepted read representation |
|---|---|---|
| `GET /me?$select=id,mail,userPrincipalName` | Bound account | `200` user object |
| `GET /me/calendar?$select=id,isDefaultCalendar,owner` | Default calendar | `200` calendar object |
| `GET /me/outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'Windows')` | Windows aliases | `200` collection of zone objects |
| `GET /me/outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'Iana')` | IANA aliases | `200` collection of zone objects |
| `GET /me/calendarView?startDateTime={start}&endDateTime={end}&$top=100&$select={projection}` | Bounded discovery | `200` event collection |
| `GET /me/calendar/events/{id}?$select={projection}` | Default-calendar event detail and readback | `200` event object |
| `PATCH /me/events/{id}` | Admitted time correction | Documented normal success is `200` with updated event object |

**[SPEC]**
- The exact base event projection is `id,changeKey,start,end,originalStartTimeZone,originalEndTimeZone,isOrganizer,organizer,attendees,isAllDay,isCancelled,type,seriesMasterId`.
- Do not select `createdDateTime` or `lastModifiedDateTime` in `calendarView`.
- Preserve a returned `@odata.etag` annotation without attempting to select it as an event property.
- Ignore additional properties unless explicitly adopted by a versioned extension profile.
- Require a usable object or collection body for read success.
- Do not admit a `/me` `202` response as identity evidence.
- The main spec uses this profile's default-calendar detail route and does not widen its route permissions.
- Feasibility helpers and future recurrence routes need separately bounded profiles rather than implicit expansion of this matrix.

### 6.1. Agent content-read profile

**[SPEC]**
Subject and description-based agent review requires a separately versioned, explicitly admitted read-only content profile.
- The completed agent capability MUST define the exact additional routes, requested properties, response representations, permission requirements, and content limits before it is enabled.
- The profile MUST document subject access and the selected description source, including whether it reads the event body or a deliberately limited representation.
- The profile MUST establish required field availability under its selected permissions through adapter tests and intended-environment validation.
- Broader permissions MUST be an explicit profile and consent decision, never a fallback after a missing field or denied request.
- Content reads MUST retain the base profile's origin confinement, default-calendar binding, account isolation, byte limits, request deadlines, and enclosing operation budgets.
- Content evidence MUST bind tenant, mailbox, calendar, case-sensitive event ID, returned source validator, content-profile version, and observation time.
- A changed source validator between safety and content reads MUST invalidate their combined assessment and require refreshed evidence rather than silently combining versions.
- Missing version-binding evidence MUST hold any assessment that requires a consistent joined snapshot.
- Normalize HTML or other rich descriptions using an explicitly defined safe text representation and preserve evidence of redaction or truncation.
- Distinguish absent, empty, unavailable, redacted, and truncated content, and never present a partial description as complete.
- Treat calendar content as untrusted data rather than instructions granting model tools, credentials, new reads, or mutation authority.
- Only content permitted by the selected privacy, sensitivity, retention, and model-transmission policies MAY leave the adapter boundary for model review.
- This profile MUST NOT retrieve related email, attachments, extensions, or Teams data unless a further explicit profile admits those capabilities.
- Content retrieval MUST NOT authorize body updates or expand the time-only PATCH payload.
- Until this profile is defined and validated, subject or description-dependent model review MUST remain held or be explicitly limited to available evidence without claiming those clues were assessed.

**[?]**
- Exact content properties, description representation, permission profile, sensitivity handling, and redaction limits require a documented profile decision.
- This contract does not claim that the base projection provides subject or description, or that `Calendars.ReadBasic` permits event-body access.

## 7. Headers and transport bounds

**[SPEC]**
| Header | Requirement |
|---|---|
| `Authorization` | Bound delegated access token |
| `Prefer: IdType="ImmutableId"` | SHOULD accompany event-identifier requests |
| `Prefer: outlook.timezone="UTC"` | SHOULD accompany event reads and calendar views |
| `client-request-id` | Unique exchange identifier, retained with attempt evidence |
| `return-client-request-id: true` | SHOULD request diagnostic echo |
| `Content-Type: application/json` | Required for PATCH |
| `If-Match` | Exact usable previously read opaque ETag, including original entity-tag syntax |

**[SPEC]**
- GET responses MUST be limited to 4 MiB, including streamed-body enforcement when length is absent or misleading.
- Each request MUST have a 45-second total budget covering token acquisition, network exchange, and response-body consumption.
- PATCH response and error parsing MUST also use an explicit bounded body limit, no greater than 4 MiB in this profile.
- Read retries MUST fit both request and enclosing discovery budgets rather than resetting unlimited time.
- Oversized, truncated, malformed, or timed-out bodies MUST return explicit evidence of the failure phase.
- A received PATCH status MUST survive body failure and MUST NOT be replaced by an invented network-only outcome.

## 8. Identity, mailbox, and category evidence

**[SPEC]**
- Verify `/me.id` against the selected account before using calendar observations for mutation.
- Verify the default calendar ID, `isDefaultCalendar=true`, and owner address against the bound account using retained identity evidence.
- Keep `mail` and `userPrincipalName` distinct, even when a documented binding rule compares either with a nonblank owner address.
- A missing `mail` MUST NOT be filled from `userPrincipalName`.
- A failed binding MUST hold discovery or eligibility, never select another calendar silently.
- Bind every mutable target through default-calendar reads even though PATCH uses a mailbox-wide route.
- Scope event identity to tenant, mailbox, and calendar context, with exact case-sensitive provider ID.
- Immutable-ID preference does not establish stability across archive mailboxes.
- Never substitute `iCalUId` for an event locator or assume it identifies an entire recurring series.
- Organizer, attendee, and resource observations MUST preserve unknown and malformed states.
- Internal-attendee classification requires independently accepted tenant or directory evidence with provenance and freshness.
- Address suffixes, presence in an attendee list, and an organizer flag alone MUST NOT invent tenant membership or resource classification.
- Unknown ownership, attendee category, resource status, or delegate status blocks that category's mutation admission.

## 9. Parsing and time representation

**[SPEC]**
- Preserve missing, explicit null, malformed, empty, and known values as distinct evidence states.
- Never turn missing booleans into `false`, missing attendees into an empty collection, or absent version data into a fabricated guard.
- Single-instance `seriesMasterId` may validly be absent or null.
- Occurrences and exceptions require a usable master ID under this profile.
- Preserve `changeKey` and ETag independently, with their source and retrieval time.
- Never derive an ETag from `changeKey` or interpret header syntax as evidence of guard enforcement.
- Preserve returned `dateTime`, returned `timeZone`, original start and end zone labels, parsed instants, offsets, and resolution errors separately.
- UTC representation is not original organizer intent, legal-policy evidence, or proof of provider rule adoption.
- Supported-zone collections contain `timeZoneInformation` objects with `alias` and `displayName`, not strings.
- Windows and IANA calls select representations, not mailbox settings or guaranteed local-runtime support.
- Record mapping provenance and unresolved aliases without substituting host defaults.
- All-day start and end require midnight in the same zone, but remain excluded from timed corrections.
- Provider normalization MUST NOT resolve a local-time gap or overlap by choosing an unconfirmed default.

## 10. Discovery scope and continuation

**[SPEC]**
- Capture explicit UTC start, end, and execution cutoff with `Z` or `+00:00` offsets.
- Require a non-empty interval no longer than 366 days and request `$top=100`.
- Treat these limits as application constraints, not Graph service maxima.
- Bind traversal to its original identity, calendar, interval, projection, preference headers, and profile revision.
- Parse collections from `value` arrays and fully process a valid page before treating absent continuation as exhaustion.
- Treat `@odata.nextLink` as an opaque full URL, preserving its query without reconstruction.
- Validate each continuation against the configured origin and exact `/v1.0/me/calendarView` path.
- Reject changed resource type, route, authority, identity scope, fragments, and unsafe URI forms before dispatch.
- Continue valid paging until exhaustion or an explicit page, event, detail, duration, or cancellation budget stops traversal.
- Detect repeated continuations within the bounded traversal and return incomplete evidence rather than loop.
- Required detail reads MUST complete before declaring the scope complete.
- Completion MUST bind source pages, detail observations, traversal termination, budgets, failures, scope identity, and collection times.
- A failed page, invalid continuation, missing required detail, or exhausted budget MUST prevent an eligible complete batch.
- A later page without a next link MUST NOT erase an earlier failure.
- Narrowing a failed scope requires a new explicit scope and corresponding plan, not relabeling partial results as complete.
- Deduplicate repeated identities only when versions and relevant evidence agree.
- Conflicting versions, disappeared items, and unresolved identity mappings MUST remain visible for reassessment.
- A complete traversal may contain unresolved observations, but those observations MUST NOT become executable.
- Traversal completion proves bounded collection, not atomic consistency across the mailbox.

## 11. Narrow time-correction dispatch

**[SPEC]**
- Accept product writes only from the Core executor with exact approved identity, patch, source guard, and durable attempt reference.
- Verify supplied binding and admission evidence before dispatch without taking over executor approval policy.
- Send at most one PATCH per adapter dispatch call, and send none when local validation, admission, token acquisition, or cancellation prevents dispatch.
- Use the exact case-sensitive ID from the bound default-calendar source observation.
- Send only approved `start` and `end`, each containing UTC `dateTime` and `timeZone: "UTC"`.
- Retain original-zone labels as source context rather than overwrite them into inferred intent.
- Do not add subject, body, attendees, recurrence, `isAllDay`, online-meeting fields, or notification flags.
- Do not round-trip an event object as a PATCH body.
- Do not assume omitted properties are wholly unaffected, since Graph can recalculate related values.
- Do not convert all-day events into timed events.
- Body and Teams changes require a distinct profile with a body read and preservation of the existing meeting blob.
- `onlineMeeting` is read-only and MUST NOT be replaced during a time correction.
- Do not claim arbitrary flags suppress attendee notifications.
- Validate serial dispatch and single active mutation through executor integration and configured transport handlers.

## 12. Live admission and evidence validation

**[SPEC]**
- Production writes MUST remain disabled until reviewed `ObservedPass` evidence admits the exact operation in the intended environment.
- `Planned`, `Inconclusive`, and `ObservedFail` MUST NOT grant admission.
- A boolean atomic-guard flag, reviewer name, or hash string alone is insufficient.
- Resolve evidence references to retained artifacts and validate their content, integrity, review decision, and applicability.
- Bind admission to deployment, endpoint and API version, tenant and account identity, permission profile, meeting category, operation strategy, and transport configuration.
- Require source guard, competing edit, stale narrow PATCH, stale rejection, and resource readback demonstrating that competing values remain intact.
- Include omitted-guard and altered-guard controls with resource reads and observed effects.
- An unguarded control succeeding does not by itself disprove enforcement on guarded requests.
- Evidence MUST distinguish actual provider observations from scripted simulator behavior.
- Validate notification and scheduling-representation effects separately for attendee-bearing categories.
- An attendee-free technical appointment proves neither internal-attendee admission nor notification behavior.
- External attendees, resources, delegates, all-day operations, and recurrence require independent category evidence.
- Invalidate or re-review admission when any bound deployment, identity, endpoint, category, strategy, or relevant transport dependency changes.
- Reject missing, expired, mismatched, inaccessible, or unreviewed evidence before dispatch.
- If no supported atomic path passes, keep writes disabled and surface the product go/no-go decision.

## 13. Mutation outcomes and exchange evidence

**[SPEC]**
| Conceptual outcome | Evidence rule | Executor-facing consequence |
|---|---|---|
| `TransportAccepted` | Any received PATCH `2xx` | Readback required, no final success claim |
| `Rejected` | Received `4xx` other than `408`, `412`, `429` | Preserve rejection, no automatic replay |
| `GuardedConflict` | `412` plus validated exact-environment stale-guard semantics | Preserve conflict evidence for replanning |
| `Unknown` | Unvalidated `412`, `408`, `429`, `5xx`, lost response, or ambiguous dispatch | Reconcile and hold without replay |

**[SPEC]**
- Normal documented PATCH success is `200 OK` with an updated event object.
- Boundedly parse that body and retain useful returned version and field evidence without replacing independent readback.
- Other `2xx` remain transport acceptance, not proof of documented response shape or guard enforcement.
- A malformed, oversized, or interrupted body after `2xx` MUST preserve acceptance and status while recording body failure.
- Keep safety classification, parsing status, and received HTTP status separate.
- Preserve HTTP status, attempted method and sanitized route, client and server request IDs, echoed IDs, `Retry-After`, provider error code, and sanitized error details when available.
- Preserve attempt reference, identity binding, endpoint/profile revision, timing, transport phase, and dispatch certainty.
- Distinguish definitely not dispatched, possibly dispatched, response headers received, and body consumption completed.
- An exception without an HTTP response MUST NOT invent a status or definitive rejection.
- A final status outside the outcome classes defined in the table, including an unfollowed redirect, MUST retain its raw status and be treated as unknown unless the admitted profile establishes stronger outcome semantics.
- The defined `4xx` rejection class follows the researched application profile and MUST NOT be widened to `408`, unvalidated `412`, or `429` based on an error message or undocumented provider code.
- A locally prevented request MUST retain definite non-dispatch evidence and its validation, authentication, admission, or cancellation reason rather than invent a provider rejection or HTTP status.
- Parse documented Graph error envelopes boundedly without depending on undocumented error codes.
- Error-body parse failure MUST NOT discard available status or correlation metadata.
- Diagnostics MUST exclude tokens, authorization headers, raw calendar bodies, and unnecessary meeting text.

## 14. Retry, cancellation, and reconciliation boundary

**[SPEC]**
- Separate read and write transports so read resilience cannot replay mutations.
- Bounded GET retries MAY honor `Retry-After` for throttling and transient failures within explicit attempt and duration budgets.
- `401` and `403` MUST surface authentication or authorization failure rather than retry unchanged authority indefinitely.
- `404` MUST NOT establish deletion across another account or mailbox without binding verification.
- Disable HTTP, SDK, resilience-handler, authentication-handler, and redirect-based mutation replay.
- Disable write hedging and parallel duplicate dispatch, including default resilience behavior when handlers are added later.
- A mutation `429` is unknown for safety and MUST NOT trigger transport replay, regardless of `Retry-After`.
- Expose `Retry-After` as evidence for later executor-controlled work, not permission to repeat the call.
- Cancellation before dispatch MUST send nothing and record that phase.
- Cancellation or timeout after possible dispatch MUST preserve ambiguity and any received status.
- Cancellation stops new dispatch but MUST NOT erase the executor's reconciliation obligation.
- Readback reports current identity, version, and fields separately from the original mutation exchange.
- Desired current fields do not prove this attempt caused them, that a guard was enforced, or that attendee notifications arrived.
- An immediate unchanged read does not prove an ambiguous request was never processed or cannot still complete.
- Core alone combines exchange and readback evidence into verified, failed, conflicting, or unknown workflow states.
- The thin product permits one attempt per approved item, followed by manual review or replanning.
- Any later retry capability requires executor-owned renewed approval, guard and dependency revalidation, and a durable record for every attempt.

## 15. Recurrence and extension profiles

**[SPEC]**
- The required first-release target includes evidence-qualified straightforward recurring series, independently admitted from single instances.
- The base single-instance wire profile MUST NOT be represented as a completed recurrence implementation.
- Distinguish single instance, occurrence, exception, and series master in observations and mutation scopes.
- A finite calendar-view expansion MUST NOT establish the full future effects of a series mutation.
- Each recurrence profile MUST define additional routes, projections, guards, master relationships, exceptions, cancellation identities, and strategy evidence.
- Select master update, occurrence update, splitting, or another strategy only after exact live effects are proven.
- Prove future-only effects, history preservation, exception preservation, cancellation preservation, notification behavior, and recovery for each admitted strategy.
- Never revive canceled instances or reset deliberately moved exceptions incidentally.
- Expose effects beyond the discovery horizon and require scope-specific preview and approval for them.
- Hold transition-spanning, unbounded, complex, or otherwise unproven patterns with explicit reasons.
- Recurrence feasibility MUST NOT inherit single-instance concurrency or notification guarantees.
- Deferred revert is a new approved guarded operation against current state, not Graph-owned rollback.

**[?]**
- Supported straightforward patterns, exact recurrence mutation strategy, routes, and bounded-effect proof remain unresolved.
- No statement here guarantees that Graph can meet the required future-only recurrence contract.

## 16. Feasibility experiments and acceptance evidence

**[SPEC]**
| Evidence tier | Required coverage | What it does not establish |
|---|---|---|
| Offline contract tests | Parsing distinctions, scope binding, URI confinement, exact headers and payload, byte/time bounds, outcome metadata | Microsoft service semantics |
| Simulator through real adapter | HTTP route and projection conformance, opaque paging, faults, ignored guards, dropped responses, notification journal | Live guard enforcement or actual delivery |
| Controlled-tenant experiments | Exact endpoint guard controls, resource readback, identity, zones, category effects, notifications | Admission for untested deployments or categories |
| Integrated product acceptance | Durable pre-dispatch record, exact approval, serial attempt, restart, reconciliation, unknown holds | Exactly-once execution or cross-event transactions |

**[SPEC]**
- Experiments MUST use separately authorized disposable resources and record environment, identity, protocol, observations, and artifacts.
- Probe infrastructure MUST issue inspectable requests through the actual adapter transport, not accept callback-reported mutation success as provider proof.
- Record valid, stale, omitted, and altered guard results with before and after reads.
- Retain technical-appointment evidence separately from attendee-bearing meeting evidence.
- Normal simulator PATCH success MUST return the documented `200` event body.
- Simulator stale rejection, ignored guards, response loss, and notification journals MUST be labeled test extensions.
- Acceptance MUST include cross-origin and cross-route continuations, unsafe IDs, account replacement, missing safety inputs, absent ETags, malformed and oversized bodies, and incomplete scopes.
- Verify that received `2xx` survives body failure and unvalidated `412` remains unknown.
- Verify zero extra PATCH requests after throttling, rejection, timeout, cancellation, lost response, or handler retries.
- Verify readback cannot establish retrospective guard enforcement, causation, or delivery.
- Validate the content-read profile separately for field availability, exact projection, version binding, rich-text normalization, redaction, truncation, account changes, and prohibited model transmission.
- Publish named results and evidence references by profile and category, with explicit holds for every unproven capability.

## 17. Current gaps and completion criteria

**[NOTE]**
The checklist records offline adapter work, a narrow writer, and a technical-appointment probe.
It still identifies full sanitized mutation evidence, evidence-reference validation, conditional `412` interpretation, permission-profile proof, attendee-bearing live admission, and recurrence as incomplete or unproven.
Those observations explain the remaining work and do not narrow the completed-system requirements above.

**[SPEC]**
- A completed adapter satisfies the wire profile, all bounded-failure behavior, account isolation, and metadata preservation through the real transport.
- Operational writes additionally require reviewed live admission, category evidence, durable executor integration, and accepted deployment support.
- Read-only usefulness MUST remain available without implying that write admission passed.
- Outstanding recurrence-strategy, permission, content-projection, and attribution decisions MUST be recorded explicitly before claiming the corresponding capability complete.

## 18. Changelog

**[NOTE]**
- `1.0.1` · 2026-10-16 · Aligned product references with specification 03 version 3.2 and removed the resolved audience and recurrence-scope conflict.
- `1.0.0` · 2026-10-15 · Defined the completed Graph boundary, bounded wire profile, admission evidence, single-dispatch outcomes, reconciliation seam, recurrence gates, and acceptance tiers.
