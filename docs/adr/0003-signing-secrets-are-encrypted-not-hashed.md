# 3. Signing secrets are encrypted, not hashed

Status: accepted
Date: 2026-08-28

## Context

The rule for credentials at rest is to hash them. It holds for anything you only ever need to verify: a
password, an API key someone presents back to you. You compare, you never reproduce.

A webhook sender is the other side of that relationship. It has to reproduce the secret to compute the
HMAC on every outgoing request. A one-way hash makes that impossible, so hashing is not an option here
regardless of how good a default it is elsewhere.

## Decision

Signing secrets are encrypted with AES-256-GCM and stored as `v1.nonce.ciphertext.tag`, base64url per
segment.

GCM rather than CBC, because the authentication tag makes a tampered ciphertext fail loudly. Silently
decrypting into garbage would mean signing a customer's payload with a secret they never had, producing a
signature that fails verification for no visible reason.

The version prefix exists so the key can be rotated later without a flag-day migration of the whole table.

The plaintext is returned exactly once, from the call that creates or rotates it. No read path decrypts it
for a caller. A customer who loses it rotates.

## Consequences

- Whoever holds the encryption key can read every signing secret. That is unavoidable given the sender has
  to sign; it is bounded by keeping the key out of the database and out of the repository.
- In this demo the key comes from configuration, generated per run by the Aspire host. A real deployment
  wraps a data-encryption key with a KMS and injects it from a secret store. The `ISecretProtector` seam is
  where that swap happens, and nothing above it changes.
- Secrets are never logged. `WebhookSender` zeroes its copy of the key material after each request, and no
  API response carries a secret except the two that mint one.
- The `whsec_` prefix makes a leaked secret findable in a log or a code search, which matters more than it
  sounds: the fastest way to catch a leak is for it to be greppable.
