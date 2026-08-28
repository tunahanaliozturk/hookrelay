# 1. Hand-rolled outbox relay instead of an off-the-shelf library

Status: accepted
Date: 2026-08-28

## Context

The pipeline needs the transactional outbox pattern: a domain event has to be written in the same
transaction as the business change that caused it, then picked up and published by something else.

`DotNetCore.CAP` already does this. It is mature, widely used, supports Postgres and Kafka natively, and
would remove most of the relay code from this repository. Choosing to write the relay by hand needs an
actual reason, not a preference.

## Decision

The relay is hand-written, roughly 150 lines of polling and one SQL statement.

The reason is the ordering guarantee. Ordering here is scoped to an ordering key, which is either the
destination endpoint or the pair of endpoint and aggregate, configurable per endpoint. Two things follow
from that:

- The relay has to fan one outbox row out into one delivery per subscribed endpoint, because the ordering
  key is not known until the subscriber list is. An event-level publish cannot carry a per-endpoint key.
- The claim query has to refuse to pick up a row while an older row with the same ordering key is still
  pending. Without that, two relay instances polling the same table will fan out two rows for the same
  aggregate concurrently and finish in whichever order they happen to finish, which breaks ordering long
  before anyone notices.

Both of those live inside the claim, and CAP's model does not expose that seam. Re-deriving the control
from inside its abstraction would cost more than the poller does.

## Consequences

- The claim query carries a not-exists probe for an older pending row with the same key, so each key only
  ever has one row in flight regardless of how many relay instances are running.
- One key advances one row per pass. Different keys still go out in parallel, and a pass that found work
  loops straight round rather than sleeping, so throughput across many keys is unaffected. A single key
  that needs more throughput wants a finer ordering strategy, not a faster relay.
- Retry, dead-lettering, and the delivery log are ours to build. They were going to be anyway: none of
  them are things a generic outbox library provides.
- If the ordering requirement is ever dropped, this decision should be revisited. Without it, CAP is the
  better answer.
