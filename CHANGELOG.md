# Changelog

Notable changes to this project. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [semantic versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-08-29

First release. Outbound webhook delivery with signed payloads, a durable retry ladder, per-endpoint
circuit breaking, a dead-letter store with replay, and an ordering guarantee that survives a delivery
sitting in backoff for a day.

### What it does

- Customers register endpoints and receive a signing secret once, at creation. It is encrypted at rest and
  no read path ever returns it.
- Payloads are signed with HMAC-SHA256 over `"{timestamp}.{body}"`, the same construction Stripe publishes,
  so a receiver can verify with a few lines of code. A reference verifier ships in the repository and is the
  same code the test suite runs.
- Events are captured in a transactional outbox, so a committed business write and its event can never be
  observed independently.
- Failed deliveries walk a persisted ladder of 30s, 2m, 10m, 1h, 6h and 24h before dead-lettering. The
  schedule lives in the database rather than in a retry policy's memory, so a restart loses nothing.
- Each endpoint has its own circuit breaker. One customer's outage cannot affect anyone else's deliveries.
- Deliveries for one endpoint arrive in order, enforced by a head-of-line claim rather than by partitioning,
  because a partition cannot hold a slot open across a day of backoff.
- An endpoint can be paused: deliveries queue in order and resume on unpause, with nothing dropped.
- Dead letters keep their payload and can be replayed individually or in bulk.

### Proven, not asserted

Against real Postgres, real Kafka and a deliberately unreliable receiver:

- Every published event is eventually delivered against a receiver failing 30% of requests.
- Ten concurrent copies of one message produce exactly one side effect.
- An open circuit on one endpoint leaves another endpoint's delivery latency unchanged.
- A message staged by a process that is killed before publishing is still published after restart.
- Signature verification rejects a tampered payload, an expired timestamp and a wrong secret.

### Security

- Destination URLs are checked at registration and again at connect time against the resolved address, so
  a hostname repointed at an internal address after registration is still refused. Redirects are refused
  outright.
- Signing secrets are encrypted with AES-GCM, never hashed, because the sender must reproduce them.
- All 170 packages in the dependency tree are permissively licensed, enforced on every build.

[1.0.0]: https://github.com/tunahanaliozturk/hookrelay/releases/tag/v1.0.0
