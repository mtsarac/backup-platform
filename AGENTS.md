# Repository Agent Rules

These rules apply to all changes in this repository, regardless of task, branch, phase, or coding assistant.

## Engineering Principles

- Prefer clear, correct, maintainable solutions with the smallest justified scope.
- Understand the existing behavior and root cause before changing code; avoid patch stacking.
- Reuse established components and conventions instead of creating parallel implementations.
- Avoid duplicate logic, unnecessary wrappers, speculative abstractions, and multiple sources of truth.
- Keep responsibilities cohesive and module boundaries explicit. Do not split or combine code merely to change line counts.
- Never trade correctness, security, or recoverability for fewer lines or faster delivery.
- Prioritize functional correctness and maintainability over unrelated visual or stylistic changes.

## Correctness & Safety

- Preserve authorization and tenant isolation at every relevant entry point and data access boundary.
- Respect transactional consistency, concurrency limits, ownership, leases, and fencing. Stale workers must not mutate newer owners' state or data.
- Preserve backup and restore integrity: validate data before publishing it as verified; do not silently discard valid data.
- Handle failure, timeout, cancellation, shutdown, retries, and restart recovery deliberately; distinguish their semantics.
- Remote side effects must have appropriate ownership checks and durable recovery or cleanup paths.
- Do not introduce untracked background tasks or hold database transactions/locks across slow external I/O without a justified, safe design.
- Preserve retention rules, active-operation pins, quota accounting, and existing persistent data. Do not remove safety checks as "redundant" without proving equivalence.
- Treat secrets and credentials as sensitive: avoid logging, exposing, or persisting them in unauthorized locations.

## Code & Documentation

- Follow existing language conventions and use meaningful names rather than explanatory comments.
- In C#, follow standard XML documentation conventions; use `/// <summary>`, `<param>`, `<returns>`, and `<exception>` where they clarify a public API or non-obvious contract.
- Write comments mainly to explain *why* a decision or invariant exists, not *what* obvious code does.
- Document subtle security, concurrency, and integrity constraints when the code alone cannot explain them.
- Do not add comments to every fix, method, conditional, or assignment; avoid changelog-style comments and stale explanations.
- Keep error handling intentional. Do not swallow failures, conceal incomplete work, or add fallback behavior without a defined contract.

## Tests & Validation

- Add or update tests for changed behavior, regressions, and meaningful failure scenarios.
- Check existing coverage first; do not duplicate tests that establish the same contract at the same layer.
- Prefer deterministic tests and parameterization where they improve clarity; avoid unnecessary sleeps and brittle implementation-detail assertions.
- Preserve tests for authorization, tenant isolation, backup/restore integrity, checksums, cancellation, retries, retention, quota, fencing, and concurrency.
- Run focused tests while developing, then the relevant build, broader tests, or CI checks according to change risk.
- Never delete, skip, weaken, or rewrite tests merely to make a failing build green.
- Report which checks ran, their actual results, and any checks that could not run. Never fabricate validation results.

## Contracts, Persistence & Pre-Release Changes

- Pre-1.0 status does not automatically require compatibility with unpublished versions, but it also does not permit losing existing data.
- When changing contracts, inspect and update every affected backend, frontend, agent, persisted snapshot, and test fixture together as needed.
- Remove obsolete adapters or legacy paths only after verifying their callers, deployed consumers, and persisted data implications.
- Prefer generated EF Core migrations for routine schema changes; customize SQL when justified by database-specific requirements.
- Do not rewrite applied migration history, delete development data, or change existing storage objects without an explicit, safe migration plan.

## Scope, Git & Attribution

- Keep changes task-focused; avoid unrelated refactoring or speculative features.
- Review the final diff for unnecessary code, dependencies, comments, and tests.
- Follow repository commit conventions; commit only when requested. Do not push, merge, create PRs, tag, or publish releases without explicit authorization.
- The agent MUST NOT add itself as an author, co-author, contributor, reviewer, signer, or generator of repository changes.
- Do not insert AI/agent attribution into commit messages, commit trailers (`Co-authored-by`, `Signed-off-by`, `Generated-by`), PR descriptions, code headers, or generated-file metadata.
- Do not change Git identity settings to impersonate a person. If committing would attribute changes to an agent or an unauthorized identity, stop and report the blocker rather than creating that commit.
- State remaining risks, limitations, and incomplete steps accurately.
- Use no more than 2 subagents unless explicitly authorized.
- Do not commit anything in docs/ directory.

**Goal:** Minimal complexity, reliable behavior, and maintainable code—not minimal line count at the expense of safety.
