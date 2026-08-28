# Verifying a webhook

Every request carries these headers:

| Header | Example | What it is for |
| --- | --- | --- |
| `X-HookRelay-Signature` | `t=1773480413,v1=9f86d081...` | The signature and the time it was signed |
| `X-HookRelay-Delivery-Id` | `0195c4f1-...` | Stable per delivery. Use it to de-duplicate |
| `X-HookRelay-Event-Type` | `invoice.paid` | Route without parsing the body |
| `X-HookRelay-Attempt` | `3` | Which attempt this is, starting at 1 |

## The scheme

The signature is `HMAC-SHA256` over `"{timestamp}.{raw request body}"`, hex encoded, using the signing
secret shown once when the endpoint was registered. It is the same construction Stripe publishes, so if
you already verify Stripe webhooks the code is nearly identical.

Three things matter and all three are easy to get wrong:

1. **Sign the raw bytes.** Deserialising the body and re-serialising it changes whitespace and key order,
   and the signature will not match. Read the body as bytes before anything else touches it.
2. **Check the timestamp.** Reject anything outside a few minutes. Without that check, a captured request
   stays replayable forever and signing the timestamp bought you nothing. Check both directions: a
   far-future timestamp is just as replayable as an old one.
3. **Compare in constant time.** A byte-by-byte comparison that exits early tells an attacker how much of
   a forged signature was right, one request at a time.

## At-least-once, not exactly-once

You will occasionally receive the same delivery twice. A network timeout after your server committed but
before the response reached us is indistinguishable from a failure, so it gets retried. This is the same
guarantee Stripe documents for its own webhooks.

Key your processing on `X-HookRelay-Delivery-Id` and make it idempotent. The delivery id is stable across
every retry of the same delivery.

## Ordering

Events for one destination arrive in the order they were produced. If the endpoint is configured for
per-aggregate ordering, that guarantee holds per aggregate and unrelated aggregates flow in parallel.

Ordering is never claimed across destinations, and a delivery that is retrying holds back later events for
its own ordering key until it succeeds or is dead-lettered.

## Retries

| Attempt | Delay before it |
| --- | --- |
| 2 | 30 seconds |
| 3 | 2 minutes |
| 4 | 10 minutes |
| 5 | 1 hour |
| 6 | 6 hours |
| 7 | 24 hours |

Seven attempts across a bounded window of 31 hours and 12 minutes, each delay jittered by up to 10%. Any
2xx is a success. Anything else, including a timeout, is a failure. After the last attempt the delivery is
dead-lettered and kept, so it can be replayed once the endpoint is fixed.

If your endpoint returns non-2xx often enough to trip its circuit breaker, we stop sending entirely for a
while rather than retrying harder. Deliveries still walk the same ladder and still dead-letter at the end
of it.

## Secret rotation

Rotating gives you a new secret immediately, and the previous one keeps working for deliveries that were
already in flight. Verify against both for the overlap, then drop the old one:

```
t=1773480413,v1=<signed with the new secret>
```

A delivery that was first dispatched before the rotation is still signed with the secret that was current
when it started, so a retry arriving after you rotated will carry the old signature. That is deliberate,
and it is why the overlap exists.

## Reference implementations

### C#

The verifier the service itself ships is public API, so you can reference it directly instead of copying
it. It is also what the test suite runs against, so it does not drift.

```csharp
using HookRelay.Domain.Signing;

SignatureVerificationResult result = WebhookSignatureVerifier.Verify(
    header: request.Headers["X-HookRelay-Signature"],
    body: rawBodyBytes,
    secret: signingSecretBytes,
    now: DateTimeOffset.UtcNow);

if (result is not SignatureVerificationResult.Valid)
{
    return Results.Unauthorized();
}
```

### Node

```js
import { createHmac, timingSafeEqual } from "node:crypto";

const TOLERANCE_SECONDS = 300;

export function verify(header, rawBody, secret, now = Date.now()) {
  if (!header) return false;

  const parts = Object.fromEntries(
    header.split(",").map((part) => part.trim().split("=", 2)),
  );
  const timestamp = Number(parts.t);
  if (!Number.isInteger(timestamp)) return false;

  const drift = Math.abs(now / 1000 - timestamp);
  if (drift > TOLERANCE_SECONDS) return false;

  const expected = createHmac("sha256", secret)
    .update(`${timestamp}.`)
    .update(rawBody)
    .digest();

  const provided = Buffer.from(parts.v1 ?? "", "hex");
  return (
    provided.length === expected.length && timingSafeEqual(provided, expected)
  );
}
```

Read the raw body, not the parsed one. In Express that means
`express.raw({ type: "application/json" })` on this route.

### Python

```python
import hashlib
import hmac
import time

TOLERANCE_SECONDS = 300


def verify(header: str | None, raw_body: bytes, secret: str, now: float | None = None) -> bool:
    if not header:
        return False

    parts = dict(
        piece.strip().split("=", 1) for piece in header.split(",") if "=" in piece
    )

    try:
        timestamp = int(parts["t"])
        provided = bytes.fromhex(parts["v1"])
    except (KeyError, ValueError):
        return False

    if abs((now or time.time()) - timestamp) > TOLERANCE_SECONDS:
        return False

    expected = hmac.new(
        secret.encode(),
        f"{timestamp}.".encode() + raw_body,
        hashlib.sha256,
    ).digest()

    return hmac.compare_digest(provided, expected)
```

In FastAPI, read `await request.body()` before touching the parsed model. In Django, use
`request.body`, not `request.POST`.
