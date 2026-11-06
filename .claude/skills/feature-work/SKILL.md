---
name: feature-work
description: Use whenever the user asks to build a new feature in BrainSim, change or extend an existing one, or add a substance, a receptor, a spec mechanism, a sleep or dream behaviour, a memory rule, a Lab action, a panel, a chart, a 3D effect, a setting, an LLM backend capability, a journal column or a tool, or says "implement", "add", "make X do Y", "change how X works", "next phase". Runs the mandatory analyse, ask, plan, approval protocol before any code is written. Not for a bug fix with a known cause or a one-line copy change.
---

# Feature work protocol

You are about to add or change behaviour in BrainSim: a WPF app that hosts a neurochemical engine on one thread and
shows it live (3D brain, charts, chat, Lab, memory, dreams). The engine follows a spec
(`docs/spec/agent-neurochimic-spec.pdf`, Romanian) in **reduced mode**, because the LLM sits behind an
OpenAI-compatible HTTP API. Chemistry, receptors, the clock, sleep, appraisal, memory and dreams feed each other
every tick, and a change in one of them moves the others. Replay must stay exact. Work that starts from a partial
reading of the code is how a new term in one equation quietly breaks T1, T8 or a calibration that took a day. So:

**Nothing is edited until the user has approved a written plan.** Not a test, not `config.yaml`, not a view. The
only files you may write before approval are the plan itself (plan mode) and notes in the scratchpad.

Talk to the user in the language they write in (usually Romanian). Code, docs, commit messages and app text are in
English, plain and human, with no emojis and no em dashes (`docs/conventions.md`).

Read, in this order, before anything else:

1. `docs/README.md`: phases, status, how to run
2. `docs/decisions.md`: every D-number of the area the change touches, and the Open list at the end. A decision
   that changes the code is written there before the code
3. `docs/architecture.md`: the execution loop, the reduced-mode table, persistence, the spec gaps we filled
4. The spec section of the mechanism, if the change touches the model. Where we differ from the spec, a D-number says why
5. `docs/memory.md` for anything near memory, recall, replay or dreams
6. `docs/ui-design.md` if any UI is involved (panels, colors, substance colors, the 3D scene and its budget)
7. `docs/conventions.md`: code style, testing, writing rules
8. The "Known traps" list in `.claude/skills/bug-fix/SKILL.md`: design around them

## Steps

### 1. Intake (2 minutes)

Restate the request in three lines: what the user wants, where they will see it (a panel, the chat, the 3D brain,
the journal, a test), and what success looks like (a spec test passing, a curve moving the right way, a panel
showing X). List what is explicitly NOT asked. If the request is really a bug fix, say so and switch to `bug-fix`.

### 2. Full analysis (read-only)

Walk the checklist below against the code, not from memory. Produce an **impact map**:
`file or component -> why it is affected -> invariant it carries`, with the tests that already cover each area.

- **Model**: `src/BrainSim.Engine` by folder (`Chemistry`, `Receptors`, `Effectors`, `Appraisal`, `Circadian`,
  `Sleep`, `Dreams`, `Memory`, `Llm`, `Journal`, `Hosting`). Which spec equation or table, which config keys,
  whether the W stability check (eigenvalues of -K + W) still holds.
- **Calibration**: the spec tests (T1, T4, T6, T7, T8, T9) and the calibrated values in `config.yaml` (`# ours, not
  in spec`). A new term that shifts a baseline breaks baseline neutrality.
- **Time and order**: where in the loop it runs (message, token, after the response, idle tick, sleep phase), the
  single engine thread, `Command`s in and `Frame`s out. The app never mutates engine state.
- **Determinism and replay**: fixed seeds, Lab actions applied at a tick boundary and journaled, replay never calls
  the LLM.
- **LLM boundary**: `Llm/LmStudioClient` (chat stream, embeddings, completions, models), what any
  OpenAI-compatible server supports (D-35), what the 0.8B model actually does with the prompt, the probes tied to
  the embedding model (`ProbeFile.Fits`).
- **Data**: the checkpoint, `memory.bin`, the Parquet tick journal and the events journal, `settings.json`, all
  under `%LOCALAPPDATA%\BrainSim` (or `BRAINSIM_DATA`). An old checkpoint or journal must still load: say how.
- **UI**: the panel and its view model, the Frame fields it reads, the timeline and scrub-back, the 3D scene
  budget (58 to 60 fps on the MX550), the dark theme, sizes from 1024 x 640 up, what a screen reader reads, the UI
  thread kept free.
- **Shipping**: the self-contained publish (`config.yaml`, `probes.json`, assets copied next to the exe), the
  release notes.

When the surface is wide, delegate read-only exploration to subagents, and give each one the reading list above and
the area's D-numbers in its prompt: a subagent starts with no context.

Deliverable: the impact map, written into the plan draft. If you cannot explain how the change travels from where
it starts (a user message, a token, a clock tick, a sleep phase, a Lab action) through the engine to the Frame, the
journal and what the user sees, you are not done analysing.

### 3. Ask everything, once

Collect every ambiguity and every decision that would change the work materially, and ask them in ONE round with
`AskUserQuestion` (options plus a recommended default), grouped: scope and non-goals, spec fidelity (follow the
spec, adapt it for reduced mode, or our own value), where the user sees it, data and old checkpoints, LLM backend
and model limits, performance, naming. Do not assume an answer that changes the design. Do not ask what the code or
the docs already answer.

### 4. Plan and approval

Enter plan mode (`EnterPlanMode`). The plan has: context; answers received; the impact map; the design (the new
D-number first, config keys, data shape and its migration, UI from `ui-design.md`); numbered steps, each one
shippable, naming the files it touches and the tests written first; risks and rollback (per step, `git revert` of
that step); docs; acceptance criteria (which spec test or scenario proves it); verification (the commands below
and the real-app check). Leave plan mode with `ExitPlanMode`: that is the approval request. If the user changes
anything, update the plan and ask again. "Looks good" on a plan the user has not seen is not approval.

A large feature (a new phase) gets its design in `docs/` first, as `memory.md` did for Phase 7.

### 5. Execute strictly on the approved plan

Follow the steps in order, under the `ub-mode` principles. A discovery that forces a deviation (a hidden coupling, a
spec test going red, a calibration that no longer holds) is reported and approved before you act on it. TDD: the
tests named in the plan are written before the code they cover, and you watch them fail. Every step ends with the
build, its tests, the full suite, a commit and a push to `main`:

```sh
dotnet build BrainSim.slnx -p:UseSharedCompilation=false            # warnings are errors everywhere
dotnet test --project tests/BrainSim.Engine.Tests --no-build -- --filter-class "*SomeTests"
dotnet test --solution BrainSim.slnx --no-build                     # the full suite
BRAINSIM_LMSTUDIO=1 dotnet test --solution BrainSim.slnx --no-build # scenario tests, LM Studio on, both models loaded
```

Then the `ub-mode` quality pass on what changed:

```sh
dotnet test --project tests/BrainSim.Engine.Tests -- --coverage --coverage-output-format cobertura --coverage-output coverage.xml
python -I tools/quality/crapper.py <path to coverage.xml> 10       # every method under CRAP 10
cd tests/BrainSim.Engine.Tests && dotnet stryker                    # mutation run (long: ask first, see Known traps)
```

### 6. Close

- **The real app.** Run the dev build and use the feature as the user would: `BRAINSIM_DATA` pointed at a scratch
  folder so the user's real brain is untouched, driven through UI Automation and captured with PrintWindow, never
  by moving the mouse or capturing other windows. A locked workstation gives white captures; UIA checks still
  work. What needs the user's eyes waits until they are at the PC. A fix found this way is its own `fix:` commit
  (see `bug-fix`).
- **Docs, in the same commits**: the D-number in `decisions.md` (and the Open list), `architecture.md` (the loop,
  the reduced-mode table, persistence), `memory.md` or `ui-design.md` when touched, the phase status in
  `docs/README.md`.
- **Report** what shipped against the acceptance criteria, what did not, and what is left with the user.

## Hard rules

- No edits before approval. No "small prep" edits either.
- The impact map names files; "probably affects X" is not analysis.
- Questions go to the user in one batch, with options; silence is not an answer.
- A plan without tests, rollback and acceptance criteria is not a plan.
- Work on `main`; Conventional Commits (`feat(engine): ...`) that say why, what and how it was proved; commit and
  push after every step.
- These are invariants: the plan states how each affected one is preserved.
  - The engine owns all mutable state on one thread. The app sends `Command`s and reads `Frame`s. The engine
    never references WPF.
  - Replay is exact: fixed seeds, Lab actions journaled at their tick, replay reads tokens from the journal and
    never calls the LLM.
  - The W matrix stays stable (all eigenvalues of -K + W negative) and baselines stay neutral, unless a D-number
    says otherwise.
  - A spec value we change, or a gap we fill, is marked `# ours, not in spec` in `config.yaml` and has a D-number.
  - Old checkpoints, `memory.bin` and journals still load.
  - The UI thread never waits on the engine or the network.
  - The repository is public: no secret in code, config, docs, logs, commits or memory. The API key lives only in
    `settings.json`, DPAPI-encrypted.
  - Tests and UI checks never touch the user's real data folder.
