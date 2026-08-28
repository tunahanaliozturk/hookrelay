# 2. Ordering comes from a database claim, not from Kafka partitioning

Status: accepted
Date: 2026-08-28

## Context

The usual way to keep webhook deliveries in order is to key the queue message by destination and let the
broker's per-partition ordering do the rest. One partition, one consumer, events consumed in the order
they were produced.

That works right up until the first retry.

A delivery that fails is not retried in a tight loop. It backs off: 30 seconds, then 2 minutes, then 10
minutes, and eventually a full day. Holding a partition for a day is not an option, and neither is holding
the message in memory across a deploy. So the retry has to be persisted and re-dispatched later, and by the
time it comes back the events that were behind it in the partition have long since been consumed and
delivered. Partition ordering gave the right answer for the first attempt and the wrong one from then on.

## Decision

Ordering is enforced in the dispatcher's claim query, not by the broker.

A delivery is only claimed when no older delivery with the same ordering key is still pending or in flight:

```sql
AND NOT EXISTS (
    SELECT 1 FROM deliveries AS earlier
    WHERE earlier.ordering_key = d.ordering_key
      AND earlier.status IN (0, 1)
      AND earlier.sequence < d.sequence)
```

`sequence` is a monotonic value assigned by the database, copied onto each delivery from the outbox row it
was fanned out from.

The first version compared UUIDv7 primary keys instead, on the reasoning that a v7 identifier sorts by
time. It does, but only down to the millisecond, and the relay creates every delivery in a fan-out pass
with one timestamp, so inside a batch the ids sorted by their random tail. Ordering happened to hold when
batches were small and broke as soon as they were not. A database sequence is monotonic by construction and
does not depend on clock resolution, on the clock being monotonic, or on rows being created at distinct
instants.

Kafka is still keyed by the ordering key, but for affinity rather than correctness: it keeps a stream's
attempts on one worker, one connection pool, and one circuit-breaker instance.

## Consequences

- The guarantee survives a 24 hour gap between attempts, a worker restart, a rebalance, and a replay.
- A stuck delivery blocks its own key and nothing else. That is the intended behaviour, and it is what
  makes head-of-line blocking a scoping decision rather than an outage: `PerEndpointAndAggregate` narrows
  the stream so an unrelated invoice is not held up.
- Workers can process signals with bounded concurrency instead of one partition at a time, because no two
  in-flight signals can belong to the same ordered stream. Throughput is not capped by partition count.
- A stranded claim, from a worker that died, blocks its key until the stale-claim sweep returns it. That
  bounds the damage by `StaleClaimTimeout` rather than forever, and it is covered by its own test.
- The claim query is more expensive than a plain "oldest pending row". It is backed by a partial index on
  `(ordering_key, sequence)` limited to non-terminal rows, which keeps the probe off the bulk of the table.
- Sequence values are assigned at insert, and a transaction that took a lower value can commit after one
  that took a higher value. Within one ordering key that would need two concurrent writers producing events
  for the same aggregate, which is not how aggregates are usually written, but it is a real limit rather
  than an impossibility. Closing it properly means serialising writers per aggregate, which belongs to the
  producing service rather than here.
