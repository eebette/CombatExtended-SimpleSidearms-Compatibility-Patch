# Adversarial review — how these are run

Three fresh subagents, no shared context, distinct lenses, dispatched together. Every
upstream claim is verified against locally decompiled CE and SS and cited `file:line`.
Findings are then verified by hand before anything is filed; agents are confidently wrong
often enough that an unverified finding is a hypothesis, not a defect.

## The constraint block — put this in every brief, verbatim

```
HARD CONSTRAINT — licensing. Simple Sidearms has no published license and Combat
Extended is CC BY-NC-SA, incompatible with this project's MIT license. The decompiled
sources are behavioural reference only.

This binds your RECOMMENDATIONS as well as your report:
- Do not quote or reproduce CE/SS source in your findings. Cite file:line and describe
  behaviour in your own words.
- Do not recommend reimplementing CE/SS logic in this repository. Transcribed
  predicates, formulas and constants count as copying, however short.
- When upstream exposes no entry point for something this mod needs, propose one of:
  composing the public API that does exist, using vanilla RimWorld's API, an upstream
  feature request, or an explicitly documented decision not to enforce that rule.
- Flag any code in this repository that appears to be transcribed from upstream as a
  finding in its own right, at high severity, regardless of whether it is correct.
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

## Verifying before filing

Read the load-bearing claims yourself, especially anything critical or anything that
contradicts a previous round. Convergence between agents with no shared context is strong
evidence; a lone confident finding is not. Findings that fail verification are worth
recording in the issue as rejected, with the reason — that is how the next round avoids
re-raising them.
