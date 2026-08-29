# 5. Permissive dependencies only, checked by the build

Status: accepted
Date: 2026-08-29

## Context

A licence is a property of the whole dependency tree, and most of that tree is not chosen: it arrives
transitively, its terms live in a file inside the `.nupkg`, and nothing in a normal build mentions any of
it. The package restores, the code compiles, and that is the end of the feedback.

Auditing this repository and its sibling found dependencies that were not permissively licensed, none of
them obvious from a package list. The one that reached here is **JsonPatch.Net 5.0.2**, three levels below
`Aspire.Hosting`, which ships an Open Source Maintenance Fee agreement: the code is MIT and the agreement
says so, but it asks revenue-generating users above US$10,000 a year to pay a monthly fee for the pre-built
binaries. Nobody adds that dependency on purpose; it comes with Aspire.

## Decision

Only permissive licences, and the build checks rather than trusts.

**Aspire is pinned to 13.4.6**, the last release before `Aspire.Hosting` moved to JsonPatch.Net 5. A
patch-level step within the same major, so the local orchestration story is unchanged.

**`tools/HookRelay.LicenseAudit` runs on every build.** It resolves the full tree with
`dotnet list package --include-transitive`, then reads each package's terms from the restored `.nupkg`
directory: the SPDX expression when there is one, the licence file when the package ships one, and any
licence file it can find when the package predates SPDX expressions and only carries a `licenseUrl`.
Anything it cannot match against a permissive licence fails the build with the package name and the first
line of what it found.

## Consequences

- 170 packages, every one permissive, and it stays that way without anyone remembering to look.
- The audit is offline. Everything it reads is on disk after a restore, so it behaves the same in CI as
  behind a proxy that blocks nuget.org.
- The allow list is a small set of SPDX identifiers in the tool itself. Adding one is a code change with a
  diff and a reviewer, which is the right amount of friction.
- Unreadable is treated as not permissive. A package that declares nothing and ships nothing readable
  fails, because "I could not tell" and "it is fine" are different answers.
- A routine dependency bump can now fail on licence terms rather than on compilation. That is the point.
