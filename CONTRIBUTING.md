# Contributing

## Getting set up

You need the .NET 10 SDK and Docker. Nothing else.

```bash
dotnet restore
dotnet build
dotnet run --project tests/HookRelay.UnitTests
dotnet run --project tests/HookRelay.IntegrationTests   # needs Docker running
```

`dotnet run --project src/HookRelay.AppHost` brings the whole stack up locally with a dashboard.

## Ground rules

**Warnings are errors.** `TreatWarningsAsErrors` is on for every project, and CI runs
`dotnet format --verify-no-changes`. Run `dotnet format` before you push.

**A change to a guarantee needs a test that would fail without it.** The four promises in the README are
the point of the project. If you touch ordering, the retry ladder, circuit-breaker isolation, or the
outbox, the pull request should include the test that proves the new behaviour, not just one that passes.

**Tests run against real infrastructure.** Postgres and Kafka come from Testcontainers, and the receiver is
a real Kestrel server that fails on purpose. Please do not replace them with in-memory doubles: the
behaviour under test is exactly what a stand-in gets wrong.

**Explain the why, not the what.** Comments in this repository exist where a reader would reasonably ask
"why is it done this way", usually because the obvious approach is wrong for a non-obvious reason. Comments
that restate the code get removed in review.

**Decisions with a real trade-off get an ADR.** Short, in `docs/adr/`, following the existing four:
context, decision, consequences. Include the option you did not take and why.

## Commits and pull requests

Present tense, one concern per commit, and a body when the reason is not obvious from the diff. Pull
requests should say what changed, why, and how you know it works.
