# hummingbird.airlines.svc

![Hummingbird Airlines](Hummingbird-Airlines.png)

An **API testing playground** built for [Aethrix](https://hummingbird-alm.com): one ASP.NET Core
web app (net10.0) hosting a REST middleware plus three simulated SOAP 1.1 "legacy enterprise"
backends. Designed so test scenarios can deliberately trigger every failure mode: hard faults,
soft warnings, timeouts, unavailable backends and capacity eviction.

## Architecture

```
Test client (Aethrix)
  | REST/JSON + OpenAPI                    | raw SOAP 1.1 + WSDL
  v                                        v
+--------------------------------------------------------------------------+
| one Azure Web App - ASP.NET Core, .NET 10                                |
|                                                                          |
|  Middleware (REST)   /api/v1/**      protocol translation ONLY:          |
|    |                                     no business validation          |
|    | real SOAP 1.1 HTTP round-trips                                      |
|    +--> /soap/booking   BookingSystemService    (reservations)           |
|    +--> /soap/flights   FlightManagementService (flight control, RO)     |
|    +--> /soap/luggage   LuggageManagementService (check-in + bag drop)   |
+--------------------------------------------------------------------------+
```

* `src/Hummingbird.Airlines.Backend` - service contracts (`[ServiceContract]`), the three
  legacy services, shared in-memory stores and all business rules.
* `src/Hummingbird.Airlines.Middleware` - web host. Mounts the SOAP endpoints with SoapCore
  (WSDL at `?wsdl`), exposes the REST gateway with Swagger UI, and translates backend
  failures into RFC 7807 ProblemDetails.
* The middleware talks to the backends through the official WCF client stack
  (`ChannelFactory<T>` + `BasicHttpBinding`) over loopback HTTP - never in-process - so a
  failing backend is indistinguishable from a real remote outage.

## Run locally

```powershell
dotnet run --project src/Hummingbird.Airlines.Middleware
# public: http://localhost:5000  (Swagger UI at /swagger)
# internal SOAP loopback: http://127.0.0.1:5150 (configured in appsettings.json)
# gRPC:    http://localhost:5007 (cleartext HTTP/2; configured in appsettings.json "Grpc:HttpPort")

./tools/smoke.ps1                 # end-to-end smoke suite against http://localhost:5000
./tools/smoke-streaming.ps1       # phase 1+2 protocols: REST v2 board + SSE + gRPC (incl. bidi scanner) + GraphQL + gate WebSocket + spec endpoints
```

## Deployment

Live instance: **https://hummingbird-airline.azurewebsites.net**

Deploy to your own Azure App Service (Linux, .NET 10) with the included tooling:

```powershell
./tools/deploy-azure.ps1 -ResourceGroup <your-rg> -WebAppName <your-app>
./tools/deploy-azure.ps1 -ResourceGroup <your-rg> -WebAppName <your-app> -VerifyOnly
```

The script publishes (`dotnet publish`), zip-deploys and probes the endpoints.

Notes:
* The Linux front end forwards to container port 8080; the app reads it from the
  `ASPNETCORE_HTTP_PORTS=8080` app setting (locally it falls back to 5000).
* The gRPC listener (port 5007 by default) binds in-process. The public HTTPS front end
  does not proxy it by default - expose gRPC over HTTPS by App Service's HTTP/2 gRPC
  support (or An Azure Front Door) if you need it remotely; the SSE/REST streaming
  surface (port 8080) works out of the box.

## Endpoints

| Endpoint | Kind | Description |
|---|---|---|
| `/` | HTML | default document: service directory + live test data |
| `/api/v1/flights` | REST | flight search (`?from=&to=&date=`), read-only |
| `/api/v1/flights/{nr}` | REST | single flight with live status |
| `/api/v1/bookings` | REST | booking CRUD |
| `/api/v1/bookings/{ref}/check-in` | REST | one-shot check-in with optional polymorphic bag array (`bags: Baggage[]`) |
| `/api/v1/bookings/{ref}/baggage` | REST | polymorphic single-bag drop (also one-per-type) |
| `/api/v1/_protection` | REST | current abuse-protection settings (rendered on the landing page) |
| `/api/v1/admin/reset` | REST | reset all in-memory demo data to fresh state (see below) |
| `/swagger` | REST | Swagger UI for the whole REST surface |
| `/soap/booking?wsdl` | SOAP 1.1 | reservations system |
| `/soap/flights?wsdl` | SOAP 1.1 | airport flight control (read-only) |
| `/soap/luggage?wsdl` | SOAP 1.1 | departure control: check-in + bag drop |
| `/api/v2/baggage-claim` | REST | arrivals board of the CDG T2E baggage claim (`?speed=` 1..60) |
| `/api/v2/baggage-claim/{flight}` | REST | one arrival + its belt (404 `ARRIVAL_NOT_FOUND`) |
| `/api/v2/baggage-claim/stream` | HTTP/SSE | live belt/arrival change feed, resumable via `Last-Event-ID` |
| `localhost:5007` (gRPC) | gRPC | `BaggageClaim/GetBoard`, `GetArrival`, `WatchBoard` (see below) |
| `/api/v2/gates` | REST | gate turnaround board of Terminal 2E departures (`?speed=` 1..60) |
| `/api/v2/gates/ws` | WebSocket | interactive gate feed: `subscribe` / `gateEvent` / ping-pong / error frames (see below) |
| `/graphql` | GraphQL | baggage claim queries + mutations + subscriptions (graphiql IDE on GET, subscriptions over websocket) |
| `/graphql/schema.graphql` | GraphQL SDL | schema for importers / code generators |
| `/specs/asyncapi.yaml` | AsyncAPI 3.0 | SSE baggage claim stream + gate WebSocket message models |
| `/specs/baggageclaim.proto` | Protobuf | served copy of the BaggageClaim gRPC contract |
| `/specs/baggagescanner.proto` | Protobuf | served copy of the BaggageScanner gRPC contract (bidirectional) |

REST is documented through **Swagger** (`/swagger`, machine-readable at
`/swagger/v1/swagger.json`); every SOAP endpoint publishes its **WSDL** at `?wsdl`
(importable by Aethrix / SoapUI / Postman). Both are linked from the default page.

### Default page

`/` serves a static directory page (`wwwroot/index.html`) that lists the four services with
their Swagger/WSDL links, the frozen test data below, business-rule and warning-translation
quick references, chaos-header examples, a reset button, the current protection limits, and
**live state** for the hot flights and demo bookings (refreshed every 20 s via the public
REST API).

### Reset demo data

`POST /api/v1/admin/reset` restores fresh state without restarting the system:

```bash
curl -X POST http://localhost:5000/api/v1/admin/reset
# {"reset":true,"resetAtUtc":"...","demoBookings":["GZT001","QWX452","LMN789","PRS205","TRV310"]}
```

What it does (via a real SOAP round-trip to the booking backend): drops every booking,
replays the five frozen demo seeds and restarts the deterministic counters (booking refs
from `T00001`, bag tags from `HB-00000001`, boarding sequences from 201).

Caveats, by design: it is **global** (no sessions - one call resets the whole server),
**no concurrency guarantees** (a reset racing with in-flight calls may interleave), chaos
drills are bypassed so reset always succeeds, and abuse rate limits still apply.

## Baggage claim board (streaming + gRPC)

A second, protocol-rich domain for Aethrix: the **arrivals board of CDG Terminal 2E** -
five baggage claim belts ("tapis" 1..5) fed by partner arrivals (Hummingbird, Air France,
British Airways, Air China, Lufthansa, Emirates). One arrival lands every six *board*-minutes;
each flight runs the lifecycle

```
inbound (+poll) -> arrived (belt assigned) -> unloading -> lastBag -> beltReleased -> off board
   -12min            0min                      +2min      +12min    +14min             +18min
```

The whole board is a **pure function of its (compressed) clock** - nothing is stored, so the
snapshot is identical on every transport and never needs an admin reset. `speed=60` makes a
6-minute cadence surface a belt transition every ~6 wall seconds, which turns every stream
into an actively moving feed during a test.

### HTTP / SSE (`/api/v2/baggage-claim*`)

| Endpoint | Returns |
|---|---|
| `GET /api/v2/baggage-claim?speed=60` | full snapshot: `generatedAtUtc`, `boardTimeUtc`, `speed`, `belts[1..5]`, `arrivals[]` |
| `GET /api/v2/baggage-claim/{flight}?speed=60` | one arrival (404 problem `ARRIVAL_NOT_FOUND` when unknown/off-board) |
| `GET /api/v2/baggage-claim/stream?speed=60` | infinite `text/event-stream` (see framing below) |

SSE framing:

* `retry: 3000` so event-source reconnects recover quickly;
* initial `event: snapshot` (id = board time in ms) and a fresh snapshot every 20 s;
* change events named `flightInbound`, `flightArrived`, `unloadingStarted`, `lastBag`,
  `beltReleased`, `flightOffBoard` - each body carries `{sequence, occurredAtUtc, kind,
  flightNumber, belt, state}` in the same shapes as the REST endpoints;
* `: keepalive` comments every 15 s (Kestrel's write-silence abort is disabled for exactly
  this reason, see `MinResponseDataRate` in `Program.cs`);
* **resumption**: pass the last event id via the `Last-Event-ID` header or the
  `lastEventId` query parameter - the stream re-emits everything you missed between your id
  and "now" (clamped to the board origin), then continues live.

### gRPC (`localhost:5007`, contract in `src/Hummingbird.Airlines.Middleware/Protos/baggageclaim.proto`)

| RPC | Shape | Notes |
|---|---|---|
| `BaggageClaim/GetBoard` | unary | full snapshot; `speed` compresses the clock |
| `BaggageClaim/GetArrival` | unary | single flight; gRPC 5 `NotFound` with code `ARRIVAL_NOT_FOUND` |
| `BaggageClaim/WatchBoard` | server streaming | first message a snapshot (unless `include_snapshot=false`), then every change event |

* Kestrel uses a **dedicated cleartext HTTP/2 listener** (plain HTTP/1.1 ports cannot
  negotiate h2c); clients connect with prior knowledge: `GrpcChannel.ForAddress("http://localhost:5007")`.
* **Server reflection is enabled**, so grpcurl / gRPC UI / Aethrix can discover the service:
  `grpcurl -plaintext localhost:5007 list`.
* `WatchBoardRequest.last_event_sequence` replays missed events (same semantics as the SSE id);
  sequence numbers live on the board clock, so resume with the same `speed` the events were
  produced with.
* The port and the listener are additive: set `Grpc:HttpPort: 0` to switch gRPC off and the
  REST/SOAP behaviour is untouched.

Event sequence numbers (SSE `id` = gRPC `BoardEvent.sequence` = board time in ms) are shared
across both transports, so a test tool can consume them simultaneously and correlate.

### Probe + smoke for the streaming surface

```powershell
dotnet run --project tools/GrpcProbe -- table 60        # unary board print
dotnet run --project tools/GrpcProbe -- find LH426 60   # unary lookup (belt, state)
dotnet run --project tools/GrpcProbe -- watch 30 60     # 30 s server-stream sample
dotnet run --project tools/GrpcProbe -- scan            # bidi scanner demo (14 scans, all dispositions)
./tools/smoke-streaming.ps1                             # REST-v2 + SSE + gRPC (incl. scanner) + GraphQL (queries/mutations/subscription) + gate-WS smoke (needs pwsh 7)
```

Note: the board uses its own partner-arrival feed - its "AF/BA/CA..." flight numbers are unrelated
to the internal Hummingbird booking schedule ("HB..."), so tests of both domains stay isolated.

## Phase 2 - scanner, GraphQL and the gate turnaround feed

Phase 2 added three protocol surfaces (all additive; nothing about the REST/SOAP
v1 contract changed). They share the deterministic clock described above; the GraphQL
mutation stage (`checkIn`/`dropBag`/`scanBag`) extended `/graphql` afterwards.

### gRPC `BaggageScanner/ScanBags` (bidirectional, port 5007)

Contract: [`/specs/baggagescanner.proto`](http://localhost:5000/specs/baggagescanner.proto)
(source: `src/Hummingbird.Airlines.Middleware/Protos/baggagescanner.proto`). The client streams
`ScanRequest {scan_id, tag_number, zone}` frames; the server answers with one `ScanAck` per bag
and closes the session with a single `ScanSummary` once the client half-closes its stream.
Every session is independent (no cross-session state). Dispositions are evaluated in a fixed
order, so a tag always produces the same result (the rules are shared with the GraphQL
`scanBag` mutation via `Storage/BagScanRules`):

| Disposition | Trigger |
|---|---|
| `LIMIT_EXCEEDED` | accepted count already 8 |
| `MALFORMED` | tag fails `^[A-Z]{2}[0-9]{6}$` (case-insensitive) |
| `UNKNOWN_ZONE` | zone outside MU1 / MU2 / MU3 (reply carries the input; empty zone = MU1) |
| `DUPLICATE` | tag already accepted in this session (case-insensitive) |
| `ACCEPTED` | routed to belt 1..5 by FNV-1a hash of the tag |

Rejections always carry `belt = 0`. The summary reports totals plus a `by_disposition` map.
Try it with the probe (a deterministic demo that exercises every disposition):

```powershell
dotnet run --project tools/GrpcProbe -- scan            # 14-scan demo session
dotnet run --project tools/GrpcProbe -- scan AB123456 CD765432@MU2
```

### GraphQL (`/graphql`, HotChocolate)

```graphql
query  { bagClaimBoard(speed: 60) { boardTimeUtc belts { number idle flightNumber } arrivals { ... } } }
query  { bagClaimArrival(flightNumber: "LH862", speed: 60) { ... } }
query  { bagClaimBelt(number: 3, speed: 60) { ... } }
subscription { bagClaimEvents(speed: 60, sinceSeq: 0) { sequence occurredAtUtc kind flightNumber belt state } }
```

* `bagClaimArrival` for an unknown/off-board flight returns GraphQL error code
  `ARRIVAL_NOT_FOUND` (same identity as the REST 404 and the gRPC `NotFound`).
* `bagClaimEvents` is a custom stream (clock-derived, per-subscriber `speed` and `sinceSeq`
  resume point) - the same sequence ids as SSE and `WatchBoard`.
* The IDE is served on GET of the same `/graphql` URL; subscriptions use the
  `graphql-transport-ws` sub-protocol there.
* Enums are serialized with HotChocolate's default UPPER_SNAKE convention
  (`FLIGHT_ARRIVED`, `LAST_BAG`, airline `AF`) - the SDL at
  `/graphql/schema.graphql` always matches the wire. The REST/SSE JSON contract uses lowercase
  camel values (`flightArrived`), so compare per transport.

#### GraphQL mutations (departure-side: `checkIn`, `dropBag`, `scanBag`)

```graphql
mutation { checkIn(input: { bookingRef: "T00005", bags: [ { kind: CHECKED, weightKg: 28 } ] })
           { bookingRef seat gate boardingSequence bags { tagId kind weightKg } warnings { code } } }
mutation { dropBag(input: { bookingRef: "GZT001", bag: { kind: CARRY_ON, weightKg: 8, hasLaptop: true } })
           { bookingRef bags { tagId kind } warnings { code } } }
mutation { scanBag(input: { sessionId: "cart-42", scanId: "1", tagNumber: "AB123456" })
           { scanId disposition belt acceptedCount rejectedCount } }
mutation { resetScanSession(sessionId: "cart-42") { sessionId removed } }
```

* `checkIn`/`dropBag` travel through the same `SoapGateway` loopback round-trips as
  `POST /api/v1/bookings/*`, so verdicts and codes stay identical:
  `BOOKING_NOT_FOUND`, `CHECKIN_CLOSED`, `CHECKIN_REQUIRED`, `ALREADY_CHECKED_IN`,
  `BAGGAGE_TYPE_LIMIT`, `FLIGHT_DEPARTED`, `INVALID_REQUEST` (weight out of 0..100),
  plus `LEGACY_TIMEOUT` / `LEGACY_UNAVAILABLE` when the `X-HB-Simulate` chaos header
  is set. Errors surface as `errors[].extensions.code` (RFC-style titles are REST only).
* `BagInput` is flat like the REST bag body: `kind` (CHECKED/CARRY_ON) plus the kind-specific
  fields (`lengthCm..fragile` for checked, `hasLaptop`/`fitsUnderSeat` for carry-on).
  In-allowance overweight keeps the bag and is reported inside `warnings`
  (`CHECKED_BAGGAGE_OVERWEIGHT`), exactly like REST 201 responses.
* `scanBag` evaluates the SAME rule engine as the gRPC `ScanBags` stream (shared
  `BagScanRules`): dispositions are data, never errors; `belt` is 0 when rejected and the
  FNV-1a make-up belt 1..5 when accepted. Sessions are explicit (`sessionId`), GraphQL-side
  only, expire after 30 minutes of inactivity and are bounded (oldest evicted);
  `resetScanSession` clears one session idempotently (`removed=false` for unknown ids).

### Gate turnaround feed (`/api/v2/gates` + WebSocket)

Departure-side counterpart of the board: **gates 1..8 of the Terminal 2E pier**, one Hummingbird
turnaround per gate every 16 feed-minutes, staggered by 2 feed-minutes across gates. Flight
numbers are simulated rotations (`HB900..HB907`) that are guaranteed unique among simultaneously
occupied gates. Facts about the virtual clock: everything is a pure function of the clock again.

| Endpoint | Contract |
|---|---|
| `GET /api/v2/gates?speed=60` | `TurnaroundSnapshot` (`feedId` CDG-T2E-GATES, gates[]) |
| `WS /api/v2/gates/ws` | client frames: `{"type":"subscribe","speed":60,"sinceSeq":0}`, `{"type":"unsubscribe"}`, `{"type":"ping"}` |

Server frames: `snapshot` right after subscribe, then `gateEvent` per transition with kinds
`gateOccupied`, `boardingOpened`, `finalCall`, `pushbackStarted`, `gateReleased`,
`turnaroundComplete`; keepalive `ping` after ~15 s of write silence; `pong` in reply to client
pings; `error` envelopes (`MALFORMED_JSON` / `OVERSIZE_FRAME` / `UNRECOGNIZED_TYPE`) that leave
the socket open. Sequence ids are feed-time milliseconds and resume via `sinceSeq` exactly like
the SSE `Last-Event-ID`.

```powershell
# quick look at the frames (any WebSocket client works)
wscat -c ws://localhost:5000/api/v2/gates/ws
> {"type":"subscribe","speed":60}
```

### Specifications for importers

Every contract is served as a document so tools (Aethrix Studio import wizard, SoapUI, protoc,
graphql-codegen, Postman...) can build object models directly from the running service:

| URL | Format | Contents |
|---|---|---|
| `/specs/asyncapi.yaml` | AsyncAPI 3.0.3, self-contained | both live streams: SSE baggage claim + WS gate feed (payload JSON schemas, resumability rules) |
| `/specs/baggageclaim.proto` | Protobuf 3 | `BaggageClaim` service (unary + server streaming) |
| `/specs/baggagescanner.proto` | Protobuf 3 | `BaggageScanner` bidi service |
| `/graphql/schema.graphql` | GraphQL SDL | queries + subscriptions
| `/swagger/v1/swagger.json` | OpenAPI 3 | REST gateway surface |
| `/soap/{booking,flights,luggage}?wsdl` | WSDL 1.1 | legacy SOAP systems |

## Abuse protection

All four endpoints sit behind the same protection pipeline so nobody can use this public
test service for bulk attacks or load testing. Configured under `Protection` in
`appsettings.json`:

| Scope | Default limit | Rejection |
|---|---|---|
| per IP - REST `/api/v1/*` | 120 requests / min | 429 |
| per IP - SOAP `/soap/*` | 240 requests / min | 429 |
| per IP - pages & assets | 120 requests / min | 429 |
| per IP - concurrency | 16 parallel requests | 429 (no queuing) |
| server wide (all callers) | 3000 requests / min | 429 |
| request body size | 256 KB (Kestrel) | 413 |

* Fixed-window quotas; responses carry `Retry-After: 60`.
* The 429 body matches the caller's dialect - REST gets ProblemDetails
  (`code: RATE_LIMITED`), SOAP gets a SOAP 1.1 fault envelope with
  `faultstring RATE_LIMITED`.
* Middleware-to-backend loopback traffic (no `X-Forwarded-For` on a loopback socket) is
  exempt, otherwise the gateway would throttle itself.
* Client address comes from `X-Forwarded-For` because Azure App Service front ends proxy to
  localhost. The proxy chain is trusted unconditionally (unknown cloud networks), so per-IP
  keys can be spoofed - the server-wide ceiling and concurrency caps still bound total load.
  Honest trade-off for a free-tier test target.

## Business rules (validated in the BACKENDS only)

| Rule | Behaviour |
|---|---|
| check-in < 30 min before departure | SOAP fault `CHECKIN_CLOSED` -> middleware **409** |
| checked bag > allowance (First/Business 30 kg, Economy 23 kg, inclusive) | bag accepted, legacy warning string -> middleware **2xx** + structured warning |
| carry-on > allowance (First/Business 10 kg, Economy 8 kg, inclusive) | same soft-warning behaviour |
| more than one bag of the same type per passenger | fault `BAGGAGE_TYPE_LIMIT` -> **409** |
| baggage before check-in | fault `CHECKIN_REQUIRED` -> **409** |
| unknown booking / flight | faults -> **404** |
| cancelled or departed flight | faults -> **409** |
| bookings table capacity | 50 records FIFO; oldest evicted |

### Translation layers (the interesting part)

1. **Faults**: backend `FaultException<ServiceFault>` carries a stable code
   (`BOOKING_NOT_FOUND`, `CHECKIN_CLOSED`, ...). The middleware maps codes to HTTP statuses
   and returns `application/problem+json`:

   ```json
   {
     "type": "https://hummingbird.airlines/errors/checkin-closed",
     "title": "Check-in closed",
     "status": 409,
     "detail": "Check-in for flight HB900 closed 30 minutes before departure (28 min remaining)",
     "code": "CHECKIN_CLOSED",
     "traceId": "..."
   }
   ```

   Transport failures become **504 LEGACY_TIMEOUT** / **502 LEGACY_UNAVAILABLE**.

2. **Warnings**: overweight baggage is *not* an error. The backend replies `Success=true`
   with a cryptic pipe-delimited legacy string, which the middleware parses into structure:

   ```
   legacy in :  W|BAGGAGE_WEIGHT|checked|24.9|23.0
   JSON out:    { "code": "CHECKED_BAGGAGE_OVERWEIGHT", "category": "baggageWeight",
                  "message": "The checked bag weighs 24.9 kg which exceeds the allowance of 23 kg.",
                  "actualKg": 24.9, "limitKg": 23, "legacyMessage": "W|BAGGAGE_WEIGHT|checked|24.9|23.0" }
   ```

3. **Polymorphism**: baggage bodies are discriminated unions -
   `{"type":"checked","weightKg":23}` or `{"type":"carryOn","weightKg":8}` - implemented with
   `System.Text.Json` polymorphism on the REST side and `[KnownType]`/`xsi:type` on the SOAP
   side. The REST reader is tolerant: it also accepts the Newtonsoft-style
   `"$type":"...CheckedBaggage, ..."` discriminator emitted by generated clients (e.g. Aethrix).
   OpenAPI exposes the union as `oneOf` plus a `discriminator { propertyName: "type" }`.

### Failure injection (chaos header)

All four endpoints accept `X-HB-Simulate`. The relayed value also works through the
middleware (forwarded as an outgoing HTTP header on the SOAP call):

| Value | Effect via middleware |
|---|---|
| `timeout=12` | backend sleeps 12 s -> client timeout -> **504** |
| `fault` | typed `INTERNAL_ERROR` fault -> **502** |
| `unavailable` | backend crash (untyped fault) -> **502 LEGACY_UNAVAILABLE** |

## Test data (virtual schedule, computed from the clock)

The schedule (~80 flights over CDG / PEK / JFK / LHR / FRA / DXB / HND / SIN) is **not**
frozen at startup. Flights are stored as templates and materialised on every read:

* **Line flights** - departure = today's date + day offset + planned time of day, so dates
  roll forward automatically and the timetable never goes stale. Past slots of the current
  day show as `departed`, giving a natural mix of states at any hour.
* **Hot flights `HB900`-`HB907`** - always *now* + fixed minute offsets (29, 31, 45, 55, 90,
  150, 240, 360). The CHECKIN_CLOSED / boarding / check-in-open scenarios work no matter how
  long the app has been running.
* **Status derivation** - on every read: `cancelled` > departed > boarding (< 25 min) >
  check-in open (< 60 min) > scheduled.
* Exactly one line flight is cancelled (discoverable via `GET /api/v1/flights`).

Demo bookings are re-created on every restart:

| Ref | Passenger | Scenario |
|---|---|---|
| `GZT001` | John Doe | tomorrow-bound economy - happy-path check-in & bags |
| `QWX452` | Alice Martin | business, check-in open (~45 min) - overweight tests (limit 30 kg) |
| `LMN789` | Bob Chen | economy departing in ~29 min - triggers `CHECKIN_CLOSED` |
| `PRS205` | Carol Dupont | first class, already checked in - triggers `ALREADY_CHECKED_IN`, ready for bag drop |
| `TRV310` | David Wang | generic update/delete target |

Bookings live in memory only (max 50, oldest evicted); restarting resets them.

### Determinism contract

Everything except clock-derived dates/statuses is deterministic, so automated tests
can assert on exact values:

| Value | Guarantee |
|---|---|
| demo booking refs | fixed: `GZT001`, `QWX452`, `LMN789`, `PRS205`, `TRV310` |
| auto-generated booking refs | sequential per process: `T00001`, `T00002`, ... |
| flight numbers / routes / gates / aircraft | fixed by the schedule template (HB100..., HB900-HB907) |
| hot-flight offsets | fixed minutes from now: 29/31/45/55/90/150/240/360 |
| cancelled flight | exactly one line flight, always the same template slot |
| seat assignment | pure function of booking ref + cabin (`GZT001` -> always `31E`) |
| boarding sequences | live check-ins count from 201; seeded PRS205 keeps 101 |
| bag tags | sequential per process: `HB-00000001`, `HB-00000002`, ... |
| warning strings / fault codes | fixed formats documented above |

The only values that move are dates and derived statuses - by design.

## OpenAPI metadata for code generators

Both projects emit XML documentation files that Swashbuckle folds into the OpenAPI
document, so generated clients and Aethrix object trees carry descriptions and examples:

* every controller action documents summary, parameters (`<param>`) and per-status
  responses (`<response>` with the backend fault code),
* every DTO property carries `<summary>` plus an `<example>` value
  (e.g. `Flight.flightNumber -> "HB900"`, `Warning.code -> "CHECKED_BAGGAGE_OVERWEIGHT"`),
* enums describe when each state applies.

Inspect `/swagger/v1/swagger.json` to see `description` / `example` fields inline.

## Sample calls

```bash
# create a booking - flight is a structured designator {carrier enum, number}
curl -X POST http://localhost:5000/api/v1/bookings -H "Content-Type: application/json" \
  -d '{"flight":{"carrier":"hb","number":100},"cabinClass":"business","passenger":{"firstName":"Ada","lastName":"Lovelace","passport":"P1234567"}}'

# one-shot check-in with a polymorphic bag array (at most one of each type)
curl -X POST http://localhost:5000/api/v1/bookings/GZT001/check-in -H "Content-Type: application/json" \
  -d '{"bags":[{"type":"checked","weightKg":22,"color":"blue","lengthCm":70,"widthCm":45,"heightCm":28,"fragile":false},{"type":"carryOn","weightKg":7,"color":"grey","hasLaptop":true,"fitsUnderSeat":false}]}'

# single-bag drop after check-in (polymorphic single item, also enforces one-per-type)
curl -X POST http://localhost:5000/api/v1/bookings/GZT001/baggage -H "Content-Type: application/json" \
  -d '{"type":"checked","weightKg":31,"color":"black","lengthCm":75}'

# raw SOAP one-shot check-in with xsi:type polymorphic array
# (flat parameters - bookingRef + bags - no meaningless <request> wrapper)
curl -X POST http://localhost:5000/soap/luggage -H "SOAPAction: \"http://hummingbird.airlines/luggage/CheckIn\"" \
  -H "Content-Type: text/xml; charset=utf-8" \
  -d '<?xml version="1.0"?><soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:lug="http://hummingbird.airlines/luggage"><soapenv:Body><lug:CheckIn><lug:bookingRef>QWX452</lug:bookingRef><lug:bags xmlns:b="http://schemas.datacontract.org/2004/07/Hummingbird.Airlines.Backend.Domain" xmlns:i="http://www.w3.org/2001/XMLSchema-instance"><b:Baggage i:type="b:CheckedBaggage"><b:WeightKg>22</b:WeightKg><b:LengthCm>70</b:LengthCm></b:Baggage><b:Baggage i:type="b:CarryOnBaggage"><b:WeightKg>7</b:WeightKg><b:HasLaptop>true</b:HasLaptop></b:Baggage></lug:bags></lug:CheckIn></soapenv:Body></soapenv:Envelope>'
```

## License & legal notices

* **License** - released under the [MIT License](LICENSE).
  Copyright &copy; 2018&ndash;2026 Huaxing YUAN (first commit: 2018-06-11).
* **Trademark** - *Aethrix*&reg; and the Aethrix logo are registered trademarks of
  **Huaxing YUAN** (individual registration, European Patent Office).
* **Intended use** - this public test server exists solely for understanding, learning,
  practice and API testing (e.g. with Aethrix).
* **No abuse** - large-scale load generation, attack traffic, scraping at volume or any
  illegal use of the live endpoints is prohibited. Rate limiting is enforced per IP and
  the operator reserves the right to block offenders without notice.
* **No warranty** - the service is provided "as is" (see MIT license); data is in-memory
  and may be reset or become unavailable at any time.

## Disclaimer

Hummingbird Airlines is a fictional airline used for software testing demonstrations.

