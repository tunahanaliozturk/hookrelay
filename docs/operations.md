# Running this in anger

What to watch, what to do when a customer's endpoint misbehaves, and which knobs exist.

## The shape of the system

Three roles, each independently scalable, all reading one Postgres:

- **API** takes endpoint registrations, serves the delivery log, and accepts replays.
- **Relay** turns outbox rows into deliveries, then claims due deliveries and publishes them to Kafka.
- **Worker** consumes those signals and makes the HTTP call.

The delivery row is the source of truth for state. Kafka only carries a wake-up signal, so a lost message
costs a delay rather than a delivery, and a duplicated one costs at most one extra attempt.

## Endpoints an operator cares about

| Endpoint | Use |
| --- | --- |
| `GET /health/ready` | Database reachable. Point the load balancer here. |
| `GET /health/live` | Process up. Point a restart probe here. |
| `GET /v1/endpoints/{id}/deliveries` | The delivery log. Every attempt, not just the outcome. |
| `GET /v1/deliveries/{id}` | One delivery with its full attempt history and the reason for each failure. |
| `GET /v1/endpoints/{id}/dead-letters` | What gave up, with the payload kept for replay. |
| `POST /v1/deliveries/{id}/replay` | Requeue one dead letter with a fresh ladder. |
| `POST /v1/endpoints/{id}/replay-dead-letters` | Requeue all of them for one endpoint. |
| `POST /v1/endpoints/{id}/pause` | Stop delivering. Nothing is dropped; it queues in order. |
| `GET /openapi/v1.json` | The contract, served in every environment. |

## Metrics

On the `HookRelay` meter, exported over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

| Instrument | What it tells you |
| --- | --- |
| `hookrelay.delivery.attempts` | Attempts, tagged by endpoint and outcome. `CircuitOpen` means no request was sent. |
| `hookrelay.delivery.attempt.duration` | How long a customer's endpoint took to answer. |
| `hookrelay.delivery.dead_lettered` | Deliveries that used up the ladder. The number to alert on. |
| `hookrelay.outbox.fanned_out` | Deliveries created from outbox rows. |
| `hookrelay.delivery.dispatched` | Deliveries claimed and published. |
| `hookrelay.delivery.stale_claims_reclaimed` | Deliveries recovered from a worker that stopped responding. |

## Alerts worth having

| Condition | Why | First thing to check |
| --- | --- | --- |
| `dead_lettered` rising for one endpoint | That customer's endpoint has been down for the whole retry window. | Their last attempt's status code and response snippet in the delivery log. |
| `dead_lettered` rising across many endpoints | Not a customer problem. Something here is broken: DNS, egress, or the URL policy rejecting everything. | Attempt outcomes. A wall of `BlockedByPolicy` or `NetworkError` points outward. |
| `stale_claims_reclaimed` above zero, sustained | Workers are dying mid-attempt, or the claim timeout is shorter than a real request. | Worker logs, then `StaleClaimTimeout` against `RequestTimeout`. |
| Oldest pending outbox row aging | The relay is stuck or not running. Nothing is being delivered at all. | Relay liveness first, then its logs. |
| `attempts` with outcome `CircuitOpen` for one endpoint | Expected: their endpoint is failing and the breaker is resting it. Only worth noticing if it never closes. | Whether that endpoint has recovered. |

## Failure modes and what happens

**One customer's endpoint is down.** Their circuit opens after the configured consecutive failures, and
further attempts are recorded without a request being sent. Every other endpoint is untouched, because the
resilience pipeline is keyed per endpoint rather than shared. After the ladder is exhausted the delivery
dead-letters and waits for a replay.

**A worker dies mid-attempt.** Its delivery stays claimed until the stale-claim sweep returns it to the
pending pool, then another worker picks it up. Cost is one duplicate HTTP call at worst, which is the trade
at-least-once already makes.

**Kafka is unavailable.** The dispatcher fails to publish, releases the claim, and the delivery is picked
up on a later pass. Nothing is lost: the delivery row already exists and is what the system works from.

**Postgres is unavailable.** Everything stops, and nothing is lost. No event is acknowledged that has not
been committed, because the outbox row and the business write are the same transaction.

**A secret was rotated twice while a delivery was in flight.** That delivery pinned a version that is no
longer reachable, so it dead-letters immediately with a clear reason rather than signing with a secret the
receiver was never given.

## Retention, and the coupling that matters

| Setting | Default | Notes |
| --- | --- | --- |
| `HookRelay:Relay:AttemptRetention` | 90 days | The customer-facing debugging window. |
| `HookRelay:Relay:DeadLetterRetention` | 30 days | Only replayed dead letters are swept. An unreplayed one still needs a human, however old. |
| `HookRelay:Delivery:StaleClaimTimeout` | 2 minutes | Must comfortably exceed `RequestTimeout`, or healthy in-flight deliveries get reclaimed and duplicated. |
| `HookRelay:Delivery:RetryDelays` | 30s, 2m, 10m, 1h, 6h, 24h | The published contract. Changing it changes what customers were told to expect. |

The last two are the ones to be careful with. Shortening the claim timeout below the request timeout turns
every slow customer endpoint into a duplicate delivery, and the symptom looks like a customer bug rather
than a configuration one.

## Configuration

| Setting | Default | Notes |
| --- | --- | --- |
| `ConnectionStrings:hookrelay` | none, required | Postgres. |
| `HookRelay:Kafka:BootstrapServers` | `localhost:9092` | |
| `HookRelay:Kafka:PartitionCount` | 12 | Cannot be lowered later without recreating the topic. |
| `HookRelay:Kafka:MaxConcurrency` | 16 | Deliveries one worker attempts at once. Independent of partition count. |
| `HookRelay:SecretProtection:Key` | none, required | Base64, 32 bytes. In production this belongs behind a KMS, not in configuration. |
| `HookRelay:Delivery:AllowInsecureHttp` | `false` | Development only. Puts signed payloads on the wire in the clear. |
| `HookRelay:Delivery:AllowPrivateNetworkDestinations` | `false` | Development only. In production this is the difference between a webhook sender and an open proxy into your own network. |

Those last two exist so a local run can opt out explicitly rather than by weakening the policy itself.
Setting either in production is how this service becomes a way to read your cloud metadata endpoint.

## Security notes

Signing secrets are encrypted at rest, never hashed, because the sender has to reproduce them to sign.
They are returned exactly once, at creation or rotation, and no read path decrypts them. A customer who
loses one rotates.

Destination URLs are checked twice: at registration, and again at connect time against the resolved
address. The second check is the one that matters, because a hostname that passed the first can be
repointed at an internal address afterwards. Redirects are refused outright for the same reason.
