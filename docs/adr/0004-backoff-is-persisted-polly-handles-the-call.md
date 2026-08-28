# 4. Backoff is persisted; Polly only guards the call

Status: accepted
Date: 2026-08-28

## Context

Polly is the obvious place to put retries in a .NET service, and the obvious shape is a retry strategy
configured with the published ladder: 30 seconds, 2 minutes, 10 minutes, 1 hour, 6 hours, 24 hours.

That shape does not survive contact with the requirement. A Polly retry holds the delay in memory. Waiting
out a 24 hour rung means one process staying up for a day with a task parked on it, and a deploy, a crash,
or a scale-in loses every pending retry in flight. The bounded retry window is a durability promise, and
in-memory delays cannot make it.

## Decision

The two concerns are split.

**The ladder is persisted.** A failed delivery writes `next_attempt_at_utc` on its row and the dispatcher
polls for what is due. Nothing is held in memory between attempts, so restarts, deploys, and crashes cost
nothing. The schedule is configuration, which is also what lets CI run the whole cycle with a compressed
ladder in seconds instead of a day and a half.

**Polly guards the individual call.** Each endpoint gets its own pipeline from a
`ResiliencePipelineRegistry<Guid>`, holding a circuit breaker. That is per-endpoint state that only makes
sense in memory and only matters within a short window, which is exactly what Polly is good at.

Polly's breaker samples a failure ratio rather than counting consecutive failures. A ratio of 1.0 with a
minimum throughput of N gives the behaviour the docs describe: the circuit opens once N calls inside the
sampling window have all failed.

## Consequences

- The published ladder is a property of the data, so a delivery's next attempt is visible in the delivery
  log rather than implied by a policy object somewhere in a process.
- An open circuit still records an attempt, with outcome `CircuitOpen` and no request sent, and still
  advances the ladder. Two things follow: the bounded retry window stays honest even for an endpoint that
  is down for hours, and a support engineer reading the log can see that the fleet deliberately did not
  call, and why.
- The registry holds one pipeline per endpoint for the life of the process and does not evict. At tens of
  thousands of endpoints per worker that wants an eviction policy keyed on last use. It is noted in the
  code rather than pre-emptively built.
- Circuit-breaker state is per worker process, not shared. A fleet of four workers can each need to learn
  independently that an endpoint is down. Shared breaker state across a fleet is a much larger piece of
  machinery and is not worth it here.
