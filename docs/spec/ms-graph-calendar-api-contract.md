# Microsoft Graph Calendar API Contract
**Version 1.0.1** · 2026-10-15 · Microsoft Graph v1.0 delegated calendar profile

---

## AI READING INSTRUCTION

Read every **[SPEC]** block as authoritative requirements and facts, including application-profile requirements.
Read **[NOTE]** blocks only when context is needed.
Treat **[?]** blocks as unverified claims, not established Graph behavior.

---

## 1. Normative language and evidence

**[SPEC]**
- **MUST**, **SHOULD**, and **MAY** express requirements for conforming implementations.
- **Microsoft documented behavior** means behavior documented by Microsoft for Graph v1.0.
- **Application profile constraint** narrows behavior accepted or emitted by this application.
- **Simulator extension** is test-only behavior and is not a Microsoft Graph guarantee.
- **CURRENT** describes main-workspace implementation behavior, not conformance or a Microsoft guarantee.
- An application constraint MUST NOT be presented as a Graph-wide restriction.

## 2. Scope and endpoint origin

**[SPEC]**
- This contract defines the delegated Microsoft Graph v1.0 calendar subset consumed by this application, not the complete Graph specification.
- The production API base is `https://graph.microsoft.com/v1.0`.
- Production requests MUST be limited to that origin and `/v1.0/me` routes in this contract.
- The simulator MAY use a configured origin, but MUST enforce the same route, identity, and projection constraints.
- Clients MUST NOT follow a redirect to another origin.
- Production URI validation MUST reject user information, fragments, non-HTTPS schemes, and non-default ports.
- Event IDs MUST be escaped as a single path segment, never interpreted as routes or URLs.
- The application MUST NOT treat attendees as proof of tenant membership or account identity.
- The route matrix covers the discovery reader and time-correction writer, not all feasibility-probe helpers.
- CURRENT: `GraphApi.ValidateUri` confines destinations to the production origin and `/v1.0/me` path boundary, but does not itself enforce the route matrix or HTTP methods.
- Source verification uses exact main-workspace files and graph generation `2026-10-16T00:05:54Z`.

## 3. Authentication contract

**[SPEC]**
- Production uses delegated Entra OAuth access tokens for Microsoft Graph.
- Requests MUST send `Authorization: Bearer {access-token}`.
- An ID token is not an access token and MUST NOT be sent as Graph authorization.
- Synthetic sign-in and synthetic access tokens MAY be used only against the simulator.
- Synthetic credentials MUST NOT authorize production Graph requests.
- Token acquisition MUST remain bound to the configured tenant and selected account, and fail closed if acquisition or identity validation fails.
- Token renewal MUST use the identity library, not reuse expired tokens or silently change accounts or permission profiles.
- Interactive sign-in and consent MUST be explicit user actions, not hidden request retries.
- CURRENT: `TenantSignIn` uses MSAL browser sign-in, silent token acquisition per transport request, and Windows CurrentUser DPAPI cache protection without an unprotected fallback.

| Operation | Microsoft documented delegated minimum | Notes |
|---|---|---|
| Read `/me` | `User.Read` | Microsoft documented least-privileged permission for user profile read. |
| Read default calendar, calendar view, and event | `Calendars.ReadBasic` | Endpoint minimum, not a guarantee that every selected property is available. |
| Update event | `Calendars.ReadWrite` | Required for event update. |
| Read supported time zones | `User.Read` | Microsoft documents this delegated least-privileged permission for work/school and personal accounts. |

**[SPEC]**
- The application MUST use the least-privileged permission profile that meets its explicitly documented data requirements.
- `Calendars.ReadBasic` excludes properties such as body, attachments, and extensions.
- The application MUST validate required returned data under the chosen scopes and fail closed on missing safety inputs, rather than assume `$select` guarantees availability.
- Broader read access requires an explicit application-profile choice and consent, never fallback or automatic escalation after a read failure.
- CURRENT: `TenantSignIn.Scopes(false)` requests `Calendars.Read` and `User.Read`, while write sign-in requests `Calendars.ReadWrite` and `User.Read`.
- CURRENT gap: the read sign-in profile is broader than the endpoint minimum. This contract does not establish that the listed projection requires it.

**[?]**
- Availability of every safety-relevant projected field under `Calendars.ReadBasic` has not been validated for the target deployment.

## 4. Request headers

**[SPEC]**
| Header | Contract |
|---|---|
| `Authorization` | Required bearer access token. |
| `Prefer: IdType="ImmutableId"` | Application SHOULD send on every request that handles event identifiers. |
| `Prefer: outlook.timezone="UTC"` | Application SHOULD send for event reads and calendar views. |
| `client-request-id` | Application SHOULD send a unique request identifier for diagnostics. |
| `return-client-request-id: true` | Application SHOULD request echoing its client request identifier. |
| `Content-Type: application/json` | Required for PATCH JSON bodies. |
| `If-Match` | An admitted write MUST send the exact previously read opaque event ETag, including entity-tag syntax such as `W/"opaque-validator"`. No write is eligible without a usable exact guard. This is not a documented Graph conditional-write guarantee. |

**[SPEC]**
- The application MUST NOT derive an ETag from `changeKey`.
- Header presence MUST NOT be interpreted as proof the server enforces a write guard.

## 5. Common data and representation rules

**[SPEC]**
- Property selection MUST use the endpoint-specific `$select` listed below.
- The application event projection is `id`, `changeKey`, `start`, `end`, `originalStartTimeZone`, `originalEndTimeZone`, `isOrganizer`, `organizer`, `attendees`, `isAllDay`, `isCancelled`, `type`, and `seriesMasterId`.
- `@odata.etag` is an opaque response annotation, not a selectable event property.
- Its absence MUST hold mutation eligibility, not be reported as proof of an invalid Graph response.
- Missing, null, malformed, and empty values MUST NOT be conflated with known values such as `false` or an empty attendee array.
- Unknown safety inputs MUST remain unresolved and block mutation, not acquire invented defaults.
- `seriesMasterId` can be absent or null for a single instance, but occurrences and exceptions require a usable master ID in this profile.
- A missing or null `mail` MUST NOT be invented from `userPrincipalName`, which is a sign-in name and is not guaranteed to equal an SMTP address.
- The client MUST preserve event IDs as case-sensitive strings.
- With the immutable ID preference, an event ID is stable within the same mailbox, but MUST NOT be assumed stable across archive mailboxes.
- `iCalUId` is not an event resource locator and MUST NOT be substituted for `id`.
- Recurring occurrences can have different `iCalUId` values.
- `dateTime` and `timeZone` in a `DateTimeTimeZone` are distinct values.
- UTC-normalized output MUST NOT be treated as proof of the user's original time intent.
- When updating time, the application MUST retain the original time-zone labels as context and MUST NOT infer intent solely from UTC timestamps.
- For an all-day event, start and end MUST be midnight in the same time zone.
- `calendarView` includes occurrences, exceptions, and single-instance events in its range.
- `calendarView` MUST NOT select `createdDateTime` or `lastModifiedDateTime`.

## 6. Route matrix and resource examples

**[SPEC]**
The `$select` fields below define the requested application projection, not a non-null response schema.
Extra response properties MAY be present and MUST be ignored unless explicitly adopted by a later profile revision.

| Method and route | Purpose | Success |
|---|---|---|
| `GET /me?$select=id,mail,userPrincipalName` | Resolve signed-in identity. | `200 OK`, user object. |
| `GET /me/calendar?$select=id,isDefaultCalendar,owner` | Discover the default calendar and owner. | `200 OK`, calendar object. |
| `GET /me/outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'Windows')` | Read mailbox-supported Windows time zones. | `200 OK`, collection response. |
| `GET /me/outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'Iana')` | Read mailbox-supported IANA time zones. | `200 OK`, collection response. |
| `GET /me/calendarView?startDateTime={start}&endDateTime={end}&$top=100&$select={projection}` | Read events in a bounded interval. | `200 OK`, event collection. |
| `GET /me/calendar/events/{id}?$select={projection}` | Read a single event. | `200 OK`, event object. |
| `PATCH /me/events/{id}` | Request an event update. | Microsoft documents `200 OK` with updated event object on success. |

**[SPEC]**
- The calendar-view projection is the common event projection defined above.
- The single-event response MUST retain any returned `@odata.etag` for the guarded-write attempt.
- This profile accepts reads only with `200 OK` and a usable object or collection body.
- The get-user documentation also describes `202 Accepted` for background processing, which this reader does not admit as identity evidence.
- The default calendar ID, `isDefaultCalendar=true`, and owner address MUST be verified against the bound `/me` account before mutation eligibility.
- CURRENT: the reader requires the expected `/me.id` and calendar ID, and compares the nonblank owner address case-insensitively with `mail` or `userPrincipalName`.
- The application MUST NOT silently switch to a different calendar if that binding fails.
- This profile reads event details only from the default calendar, even though the PATCH route is mailbox-wide.
- A mailbox-wide PATCH locator MUST NOT authorize writes to an event not bound through default-calendar reads and admission evidence.
- Supported-time-zone responses contain `value` arrays of `timeZoneInformation` objects with `alias` and `displayName`, not arrays of strings.
- The two requests select Windows or IANA representations, not different mailbox settings or a guarantee that every returned alias is supported by the local runtime.
- CURRENT: the reader consumes nonblank `alias` values from both collections and rejects empty collections or time-zone continuation links as an application constraint.
- Example values below are illustrative only.

**[SPEC]**
Identity response:
```json
{
  "id": "user-id",
  "mail": "person@example.test",
  "userPrincipalName": "person@example.test"
}
```

Calendar response:
```json
{
  "id": "calendar-id",
  "isDefaultCalendar": true,
  "owner": { "name": "Person", "address": "person@example.test" }
}
```

Event response:
```json
{
  "@odata.etag": "W/\"opaque-validator\"",
  "id": "event-id",
  "changeKey": "opaque-version",
  "start": { "dateTime": "2026-10-15T17:00:00Z", "timeZone": "UTC" },
  "end": { "dateTime": "2026-10-15T18:00:00Z", "timeZone": "UTC" },
  "originalStartTimeZone": "Pacific Standard Time",
  "originalEndTimeZone": "Pacific Standard Time",
  "isOrganizer": true,
  "organizer": { "emailAddress": { "name": "Person", "address": "person@example.test" } },
  "attendees": [],
  "isAllDay": false,
  "isCancelled": false,
  "type": "singleInstance",
  "seriesMasterId": null
}
```

## 7. Collection and paging

**[SPEC]**
- Collection responses MUST be parsed from a `value` array.
- The application MUST treat `@odata.nextLink` as an opaque full URL and MUST NOT reconstruct its query string.
- For this application profile, each next link MUST resolve to the configured origin and exact `/me/calendarView` path.
- That exact-path rule is an application profile constraint, not a general Graph paging restriction.
- The client MUST reject next links that change origin, route, identity scope, or resource type.
- The client MUST follow valid next links until exhausted or an event, page, detail, duration, or cancellation budget stops traversal.
- Budget exhaustion, failed details, malformed pages, and invalid continuations MUST produce incomplete or held discovery, not a complete eligible batch.
- Absence of a next link establishes paging exhaustion only after a valid page is fully processed, not completeness after an earlier failure or budget stop.

## 8. Query and range constraints

**[SPEC]**
- `startDateTime` and `endDateTime` MUST be explicit ISO 8601 date-time values with offsets.
- Microsoft documents that the two range parameters are interpreted independently and that an omitted offset is interpreted as UTC.
- The application MUST always provide offsets to avoid reliance on that default.
- This application profile further requires UTC (`+00:00` or `Z`) query bounds and execution cutoff, as enforced by the current reader.
- The range MUST be non-empty and no longer than 366 days.
- The 366-day bound is an application profile constraint, not a Microsoft Graph limit.
- The application MUST request `$top=100` for calendar view.
- Microsoft documents a supported `$top` range of 1 through 1000 for this operation.
- The application MUST enforce a 4 MiB maximum response size for GET and a 45-second per-request budget, including token acquisition.
- Those byte and timeout limits are application profile constraints.
- If Graph returns a next link, the original query bounds remain in force for the overall operation.

## 9. Update contract

**[SPEC]**
- Updates target `PATCH /me/events/{id}` and MUST use the exact case-sensitive event ID read from the bound mailbox.
- Microsoft documents that omitted properties retain their previous values or are recalculated based on changes to other properties.
- For example, updating `location` replaces prior entries in `locations`, even if `locations` is omitted.
- The application MUST send only fields that the approved operation intends to change.
- For a time correction, the body MUST contain only the intended `start` and `end` values, with UTC `dateTime` values and no extra event fields.
- The client MUST NOT silently add subject, body, attendees, online meeting, or notification flags to a time correction.
- `isAllDay` events MUST NOT be converted to timed events by this profile.
- `onlineMeeting` is read-only.
- The application MUST NOT round-trip or replace `onlineMeeting` as part of a time correction.
- After an online meeting provider and status are enabled, Graph does not permit changing them again.
- If an extension edits the body of an online meeting, it MUST first read the body and preserve the existing meeting blob while applying changes.
- The current event projection does not include body or Teams fields.
- Body and Teams handling are future optional profile extensions and MUST NOT be claimed as part of the current projection.
- The application MUST NOT assume it can suppress attendee notifications with an arbitrary writable flag.
- The simulator's notification journal is not evidence of real Graph notification delivery.

**[SPEC]**
Time-correction request:
```http
PATCH /v1.0/me/events/event-id
If-Match: W/"opaque-validator"
Content-Type: application/json
```

```json
{
  "start": { "dateTime": "2026-10-15T17:00:00Z", "timeZone": "UTC" },
  "end": { "dateTime": "2026-10-15T18:00:00Z", "timeZone": "UTC" }
}
```
The read event's original zone labels in this example are `Pacific Standard Time`.

## 10. Conditional writes and mutation safety

**[SPEC]**
- Microsoft documents the event update endpoint as accepting PATCH and returning the updated event on success.
- The update documentation lists `Authorization` in its request-header table.
- It does not document a guaranteed `If-Match` conditional-write contract or a guaranteed `412 Precondition Failed` response for stale event versions.
- An event `changeKey` is a version identifier and `@odata.etag` may be returned, but neither fact proves that the endpoint enforces a conditional write.

### Live-write admission and evidence

**[SPEC]**
- **PENDING live-write admission:** the application MUST keep live product writes disabled until the exact endpoint and deployment behavior of its guard have been experimentally validated.
- Evidence states `Planned` and `Inconclusive` MUST NOT grant admission, and `ObservedFail` MUST block it. Reviewed `ObservedPass` provider evidence is necessary, not a simulator pass or a substitute for other admission gates.
- Validation MUST establish atomic stale-write rejection and record control results for requests with omitted or altered guards, including resource readback.
- Guard evidence MUST identify the deployment, endpoint, identity, observations, and reviewed artifacts, separately from simulator results.
- An unguarded control can succeed without disproving enforcement of guarded requests.
- Ignored-guard and transit-loss simulations test uncertainty handling, not a client's ability to detect server behavior before dispatch.
- The application MUST NOT interpret a `2xx` response alone as proof that its intended guard was enforced.
- The simulator MAY implement stale-guard enforcement, ignored-guard behavior, and dropped-response behavior as explicit test modes.
- Those modes are simulator extensions and MUST NOT be represented as Microsoft Graph guarantees.
- The executor alone owns mutation retries.
- The HTTP client, SDK, and resilience handlers MUST disable mutation replay and write hedging.
- A mutation MUST have a durable record per attempt and MUST retain its exact approved intent and guard.
- After an ambiguous or lost response, the result is unknown until reconciliation establishes the resource state.
- Readback can establish current resource state but MUST NOT be treated as retrospective proof that the guard was enforced or that this attempt caused the state.
- An unknown outcome MUST NOT be automatically retried.
- Any retry requires renewed approval and guard revalidation under executor policy.

**[?]**
- Whether Microsoft Graph's `If-Match` behavior for this endpoint and deployment provides an atomic stale-write guard remains unverified.

## 11. Errors and retry handling

**[SPEC]**
- Graph error bodies SHOULD be parsed using the documented error envelope when present.
- Parsing SHOULD be bounded and retain only necessary sanitized diagnostics, not raw calendar content.
- CURRENT gap: `GraphTransport` discards GET error bodies and every PATCH body, so it does not currently implement error-envelope parsing.
- Clients MUST NOT invent or depend on undocumented Graph error codes.
- Status and response body are evidence, not a substitute for reconciliation after an ambiguous mutation.

| Response or outcome | Required application handling |
|---|---|
| `2xx` PATCH | Record transport acceptance and correlation evidence, then independently read back for reconciliation. This is not final guarded-write success. The simulator MUST return the documented `200 OK` event body in its normal success mode. Other `2xx` are accepted by the current writer but are not the documented update response. |
| `412` PATCH | Treat as guarded conflict only with validated environment evidence that it represents enforced stale-guard rejection. Otherwise classify mutation safety as unknown/unverified, reconcile, and do not replay automatically. |
| Other `4xx` PATCH except `408`, `412`, and `429` | Classify as rejected. Preserve available status and correlation evidence and do not replay automatically. |
| `408` PATCH | Outcome is unknown. Reconcile and do not automatically retry. |
| `429` PATCH | Outcome is unknown for mutation safety. Respect `Retry-After` if present for later work, but do not automatically replay the mutation. |
| `5xx` PATCH or connection loss | Outcome is unknown. Reconcile and do not automatically retry. |
| `429` GET or transient read failure | Reads MAY use bounded retry policy and SHOULD honor `Retry-After` when supplied. |
| `401` or `403` | Do not retry unchanged credentials or permissions indefinitely. Surface authentication or authorization failure. |
| `404` | Do not treat as proof of deletion across another identity or mailbox. Confirm account and calendar binding. |

**[SPEC]**
- The table defines application safety requirements, not Microsoft guarantees or a claim that the current writer implements every requirement.
- CURRENT: `GraphCalendarWriter` maps all `2xx` to `Accepted`, `412` unconditionally to `Conflict`, other `4xx` except `408` and `429` to `Rejected`, and other statuses or caught transport failures to `Unknown`.
- CURRENT gap: unconditional `412` classification does not establish validated guard semantics and MUST NOT be used as evidence of them.
- CURRENT: `GraphTransport` sends once per call with no built-in read retry. Bounded read retries above are permitted policy, not implemented behavior.
- CURRENT: exchange metadata retains status, client/request IDs, and `Retry-After` when available, but the writer returns only outcome and server request ID. Callers MUST NOT assume the writer result includes the discarded body or full exchange.

## 12. Invariants

**[SPEC]**
- The active account MUST remain bound to the `/me` identity and discovered default calendar owner.
- Event identity MUST be mailbox-scoped and case-sensitive.
- The application MUST keep time-zone labels and UTC instants distinct.
- No read-before-write sequence alone satisfies the atomic guarded-write requirement.
- No participant address establishes account or tenant membership.
- No mutation retry may bypass fresh approval, durable attempt recording, and guard revalidation.
- No production request may be redirected outside the configured Graph origin.
- No simulator success may be treated as evidence of Microsoft service behavior.

## 13. Conformance acceptance matrix

**[SPEC]**
| Case | Required result |
|---|---|
| Read seeded account and calendar through the real adapter | Identity and default-calendar owner bind to the same `/me` account. |
| Read a seeded event through the real adapter | Returned event preserves the listed projection and opaque event ID. |
| Omit or null a safety-relevant field | Missing evidence is unresolved and mutation is held. Valid optional absence is not declared a Graph protocol violation. |
| Omit `@odata.etag` | Read may remain useful, but no guarded write is admitted and no ETag is synthesized. |
| Read a multi-page calendar view | Valid same-origin `/me/calendarView` next links are followed without query reconstruction. |
| Change active account | Events and cached identifiers from the prior account are not reused for mutation. |
| Return UTC times with original zone labels | Client does not infer original user intent from UTC alone. |
| Approve a time-only correction | Exact PATCH body contains only intended `start` and `end` values. |
| Edit body on an event with Teams data in an extension profile | Existing Teams meeting blob is preserved. |
| Simulate stale guard enforced | Attempt conflicts and no second write is emitted. |
| Receive `412` without validated provider guard evidence | Safety classification remains unknown/unverified, regardless of the current writer's `Conflict` label. |
| Simulate guard ignored or omitted | Test demonstrates that a successful write does not prove atomic protection. |
| Drop mutation response after server acceptance | Outcome remains unknown, is reconciled, and is not automatically replayed. |
| Return `429` for mutation | Mutation is not automatically replayed. |
| Operate in read-only mode | No PATCH is emitted. |

## 14. Microsoft references

**[SPEC]**
- These references describe Microsoft documentation and are not substitutes for production validation.
- [Update event](https://learn.microsoft.com/en-us/graph/api/event-update?view=graph-rest-1.0)
- [Get event](https://learn.microsoft.com/en-us/graph/api/event-get?view=graph-rest-1.0)
- [List calendarView](https://learn.microsoft.com/en-us/graph/api/user-list-calendarview?view=graph-rest-1.0)
- [List calendar calendarView](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0)
- [List calendars](https://learn.microsoft.com/en-us/graph/api/user-list-calendars?view=graph-rest-1.0)
- [Get user](https://learn.microsoft.com/en-us/graph/api/user-get?view=graph-rest-1.0)
- [Get calendar](https://learn.microsoft.com/en-us/graph/api/calendar-get?view=graph-rest-1.0)
- [List supported time zones](https://learn.microsoft.com/en-us/graph/api/outlookuser-supportedtimezones?view=graph-rest-1.0)
- [DateTimeTimeZone resource](https://learn.microsoft.com/en-us/graph/api/resources/datetimetimezone?view=graph-rest-1.0)
- [Event resource](https://learn.microsoft.com/en-us/graph/api/resources/event?view=graph-rest-1.0)
- [User resource](https://learn.microsoft.com/en-us/graph/api/resources/user?view=graph-rest-1.0)
- [Permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference)
- [Error responses](https://learn.microsoft.com/en-us/graph/errors)
- [Immutable identifiers](https://learn.microsoft.com/en-us/graph/outlook-immutable-id)
- [Paging Microsoft Graph data](https://learn.microsoft.com/en-us/graph/paging)
- [Throttling guidance](https://learn.microsoft.com/en-us/graph/throttling)
- [OAuth 2.0 authorization code flow](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow)

## 15. Changelog

**[NOTE]**
- `1.0.1` · 2026-10-15 · Corrected permissions, PATCH semantics, status classification, and current-versus-required behavior against main-workspace source and Microsoft documentation.
- `1.0.0` · 2026-10-14 · Converted the reviewed contract to HADS without removing substantive technical requirements.
