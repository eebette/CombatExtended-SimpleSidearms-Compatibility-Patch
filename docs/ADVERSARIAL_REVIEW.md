# Adversarial review — how these are run

Three fresh subagents, no shared context, distinct lenses, dispatched together. Every
upstream claim is verified against locally decompiled CE and SS and cited `file:line`.
Findings are then verified by hand before anything is filed; agents are confidently wrong
often enough that an unverified finding is a hypothesis, not a defect.

## The constraint block — put this in every brief, verbatim

```
CONSTRAINT — no mirrored upstream logic. This binds your RECOMMENDATIONS as well as your
report. Mirroring CE/SS logic locally is a design anti-pattern (a fork that drifts and
breaks on upstream change). Licensing is handled
pragmatically (see CONTRIBUTING); the rule here is about design, not law.
- Cite file:line and describe upstream behaviour; do not paste their source into findings.
- Do not recommend reimplementing CE/SS logic here — transcribed predicates, formulas and
  constants are mirrors however short. Propose instead: composing the public API, vanilla
  RimWorld's API, an upstream feature request, or an explicitly documented decision not to
  enforce that rule. Publicized private members count as calls.
- Flag code in this repository that appears mirrored from upstream as a finding in its own
  right — a DESIGN finding, severity rated by fragility and drift risk, regardless of
  whether the code is currently correct.
```

Omitting the last two lines is how a two-line pacifist check got lifted out of SS's
`CanPickupSidearmType` and shipped to a PR on 2026-08-24. All three reviewers then
independently recommended reimplementing *more* of the same method — correctly, by the
brief they had been given. See `CONTRIBUTING.md`.

## Lens splits that have worked

Pick a split matched to the change, not a fixed set.

**Reviewing a module cold:** state machine and convergence / upstream contract fidelity /
persistence, lifecycle and player-visible failure.

**Reviewing a set of claimed fixes:** refute the fixes (reconstruct each original failure
against the new code, default to "still broken") / the new code with no knowledge of the
prior findings / test integrity.

## Rules that keep paying off

- **Give the reviewer the decompiles and require citations.** Claims from general RimWorld
  knowledge are worthless; both mods do surprising things.
- **Tell them what is settled.** Re-litigating a closed architecture decision is noise.
  List the decisions and say they are out of scope.
- **Ask for a concrete failure scenario per finding.** "This might not handle X" is
  unactionable; "player does A then B and the pawn ends up C" is a defect.
- **Ask for CONFIRMED vs PLAUSIBLE**, and for what they could not check.
- **Ask for a verified-clean list.** It stops the next review re-deriving the same ground.
- **Do not let a reviewer audit its own suggestion.** Fresh agents, every round.

**Reviewing a branch against its base:** give one agent `git diff base..branch` as its
primary artefact, not the end state. Three reviews of one rewrite missed a deleted behaviour
(an exclusion-lifecycle prune) because nothing in the *result* points at code that stopped
existing — only the diff shows a removal.

**Check the premise behind a severity, not just the finding.** One reviewer rated a missing
save-migration "high" by modelling a released mod with users; the mod was unreleased and the
migration it prompted guarded an empty set — and reintroduced the node it migrated away from.
A finding can be technically correct and still rest on a situation that does not exist.

## Verifying before filing

Read the load-bearing claims yourself, especially anything critical or anything that
contradicts a previous round. Convergence between agents with no shared context is strong
evidence; a lone confident finding is not. Findings that fail verification are worth
recording in the issue as rejected, with the reason — that is how the next round avoids
re-raising them.
