# Backup Platform — Agent Code Quality Rules

## Core Principles

- Prefer simple, readable, maintainable code over unnecessary abstractions.
- Minimize code volume without compromising correctness, security or backup safety.
- Do not introduce wrappers, interfaces, helpers or services without a clear responsibility.
- Avoid duplicate business logic and multiple sources of truth.
- Prefer extending existing implementations over introducing parallel systems.
- Never simplify away tenant isolation, lease fencing, checksum verification, retention pinning or durable recovery guarantees.
- Frontend visual redesign is not a priority; functional correctness and maintainability are.

## C# Comments & Documentation

- Follow standard C# XML documentation conventions.
- Use `/// <summary>` for public APIs and non-obvious contracts when documentation adds value.
- Use `/// <param>`, `/// <returns>` and `/// <exception>` only when they clarify important behavior.
- Use normal `//` comments primarily to explain **why**, not **what**.
- Document non-obvious concurrency, security and data-integrity invariants.
- Do not add comments to every fix, condition, assignment, try/catch or method.
- Do not write comments describing obvious code.
- Avoid changelog-style comments such as "Fixed bug", "New implementation" or "Previously this method...".
- Remove stale or redundant comments when modifying the associated code.
- Prefer meaningful naming and small cohesive methods over explanatory comments.
- Keep documentation concise and accurate. Existing XML documentation must remain valid after refactoring.

## Code Structure

- Follow SRP and maintain clear module boundaries.
- Identify root causes instead of layering patches.
- Avoid God Services, deeply nested control flow and repeated error classification.
- Do not extract methods solely to reduce line count.
- Do not create an interface for every class.
- Do not introduce generic infrastructure for hypothetical future requirements.
- Keep provider-specific logic out of shared domain code.
- Avoid fire-and-forget tasks, untracked background operations and long database transactions spanning network I/O.

## Tests

- Add tests for new behaviors, regressions and meaningful failure scenarios.
- Before adding a test, check whether an existing test already covers the same contract.
- Prefer parameterized tests when they meaningfully reduce duplication.
- Do not add multiple tests proving exactly the same behavior at the same layer.
- Preserve critical safety tests: tenant isolation, authorization, checksum, retention, quota, backup/restore, cancellation, leasing and concurrency.
- Run focused tests during development; reserve full integration suites for validation milestones.
- Never delete tests merely to reduce the total test count.
- Do not fabricate PASS results for unexecuted tests.

## Pre-v1 Development

- Backward compatibility with unpublished development versions is not automatically required.
- Remove obsolete DTOs, adapters and legacy fallbacks when their usage has been verified.
- Frontend, backend and Agent contracts may evolve together.
- Prefer EF Core-generated migrations; manually customize SQL where PostgreSQL-specific behavior requires it.
- Never modify persistent user data or discard migration history without an explicit migration/data-preservation strategy.

## Change Discipline

- Make focused, minimal changes.
- Avoid unrelated refactoring inside a bug fix.
- Review the complete lifecycle, including failure, cancellation and restart paths.
- Before completion, review the diff for unnecessary code, comments, dependencies and duplicate tests.
- Report remaining risks and verification limits.
- Use no more than 2 subagents unless explicitly authorized.

**Goal: Less unnecessary code, lower complexity and higher reliability—not fewer lines at the cost of correctness.**
