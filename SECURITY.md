# Security

## Reporting a vulnerability

Open a [private security advisory](https://github.com/tunahanaliozturk/hookrelay/security/advisories/new)
rather than a public issue. I will acknowledge within a few days and keep you updated while it is being
worked on.

## What this project treats as security-relevant

An outbound webhook sender forwards requests to URLs its own users supply, which puts a few things on the
critical path that would be ordinary code elsewhere.

**Server-side request forgery.** Destinations are checked at registration, redirects are refused, and the
resolved address is re-checked after DNS resolution and before the socket opens. Any way to get the fleet
to reach a private, loopback, or link-local address, `169.254.169.254` in particular, is a vulnerability.

**Signature forgery.** Anything that lets a payload be accepted by the reference verifier without the
signing secret: a comparison that is not constant time, a timestamp window that can be bypassed in either
direction, a header parse that can be tricked into checking the wrong bytes.

**Secret disclosure.** Signing secrets are encrypted at rest and returned exactly once, by the call that
mints them. A secret appearing in a log line, a delivery-attempt record, an error message, or any read
endpoint is a vulnerability.

**Tenant isolation.** Every read filters on the tenant inside the query predicate, and a mismatch returns
`404` rather than `403`. Any way to read or act on another tenant's endpoints, deliveries, or dead letters
is a vulnerability.

**Response handling.** Response bodies come from someone else's server. They are read up to a bounded size
and stored truncated. An unbounded read, or a stored snippet that is rendered somewhere without escaping,
is a vulnerability.

## What is out of scope

- The demo compose file ships a fixed encryption key and enables plain http and private-network
  destinations. It is labelled as a demo and the switches default to off. Real deployments inject a key
  from a secret store.
- `POST /v1/events` and the tenant header exist because authentication belongs to the platform this service
  sits inside. Missing authentication on those is a documented boundary, not a finding.
- The chaos receiver is a test fixture. It is deliberately unreliable and deliberately unauthenticated.
