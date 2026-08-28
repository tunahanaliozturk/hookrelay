# Benchmarks

Measured on a quiet developer machine, not a CI runner. Reproduce with:

```bash
dotnet run --project tests/HookRelay.Benchmarks --configuration Release -- --filter '*' --job Short
```

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.9106)
Intel Core Ultra 7 255H, 16 logical and 16 physical cores
.NET SDK 10.0.303, .NET 10.0.11, X64 RyuJIT x86-64-v3
Job=ShortRun  IterationCount=3  LaunchCount=1  WarmupCount=3
```

## Signing

| Method | Payload | Mean | Allocated |
| --- | --- | ---: | ---: |
| Sign | 256 B | 386 ns | 336 B |
| Verify | 256 B | 465 ns | 0 B |
| Sign | 4 KB | 1,497 ns | 336 B |
| Verify | 4 KB | 1,629 ns | 0 B |
| Sign | 64 KB | 19,204 ns | 336 B |
| Verify | 64 KB | 19,319 ns | 0 B |

Signing a realistic webhook body costs about 400 nanoseconds, which is four to
five orders of magnitude below the HTTP round trip it precedes. It is not worth
optimising further, and the point of measuring was to establish that rather than
to make it faster.

The 336 bytes are the returned header string and the hex encoding of the MAC.
The MAC itself is computed without allocating: no hasher instance, no
intermediate `"{timestamp}.{body}"` string, and a pooled buffer above 1 KB. That
is why the cost stays flat in allocations as the payload grows from 256 bytes to
64 KB, and why it grows linearly in time rather than in garbage.

Verification allocates nothing at all. It is the path a customer pays on every
inbound webhook, so it parses the header with a `ref struct` splitter and
compares in a stack buffer.

## Per-endpoint resilience pipeline

| Method | Endpoints | Mean | Allocated |
| --- | --- | ---: | ---: |
| Lookup | 1 | 15.6 ns | 152 B |
| Execute through pipeline | 1 | 355 ns | 152 B |
| Lookup | 1,000 | 23.7 ns | 152 B |
| Execute through pipeline | 1,000 | 381 ns | 152 B |

The question worth answering here is what per-endpoint isolation costs, since
giving every destination its own circuit breaker sounds expensive.

It is not. Looking a pipeline up by endpoint id costs about 20 nanoseconds and
barely moves between one endpoint and a thousand. Running a successful call
through the breaker costs around 370 nanoseconds. Against a delivery that spends
milliseconds on the network, isolation is free.

What the registry does cost is memory: one pipeline per endpoint, held for the
life of the process, with no eviction. At tens of thousands of endpoints per
worker that wants an eviction policy keyed on last use. It is noted in the code
rather than pre-emptively built.
