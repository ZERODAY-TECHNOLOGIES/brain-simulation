---
name: bug-fix
description: Use whenever the user reports something broken in BrainSim: "bug", "does not work", "error", "crashes", "hangs", "freezes", "why does the brain do Y", a pasted stack trace, events-journal line or test failure, a substance curve going the wrong way, a spec test going red, the LLM status or probes wrong in the header, a panel showing stale or wrong values, a lost checkpoint or memory, a slow exit, a failing build or release. Runs the reproduce, evidence, root cause, class-wide search, graded approval, minimal fix plus regression test protocol. If the fix turns out to need new behaviour, switch to the feature-work skill.
---

# Bug-fix protocol

A bug report is a symptom. In a simulation, a wrong number usually comes from somewhere other than where it shows:
a curve that drifts can be a W term, a tick counted twice, a phase target swapped at the wrong time or a probe trained
on another embedding model. The protocol exists so a fix explains the cause, covers every place the cause lives, and
leaves a test behind.

Talk to the user in the language they write in (usually Romanian). Code, docs, commit messages and app text are in
English, plain and human, with no emojis and no em dashes.

Read before anything else:

1. The "Known traps" list below: match the symptom against what has already been paid for, FIRST
2. The D-numbers of the area in `docs/decisions.md`, and `docs/architecture.md` (the loop order, reduced mode,
   persistence). A behaviour a D-number chose on purpose is not a bug: say so
3. The spec section of the mechanism, if the symptom is in the model
4. `git log --oneline -- <path>` for the area

## Steps

### 1. Reproduce, or prove you cannot

Before any theory: a failing test
(`dotnet test --project tests/BrainSim.Engine.Tests -- --filter-class "*SomeTests"`), a scenario against LM Studio
(`BRAINSIM_LMSTUDIO=1`), the dev app doing it (on a scratch `BRAINSIM_DATA`, driven through UI Automation, never on
the user's screen or data), or the journal row, events line, checkpoint field or `settings.json` value that shows
the fault. The tick journal (Parquet) and the events journal (`.events.jsonl`) of the session usually hold the
exact tick where it went wrong. Write the exact command into the diagnosis. If it cannot be reproduced, say so and
work from evidence, never from a guess.

### 2. Evidence first, then ranked hypotheses

Collect facts: the stack trace, the events-journal lines around it, the tick journal rows (C, C-bar, rho, m_k, T,
top-p), the checkpoint, the config values involved, the model loaded in LM Studio, `git log -- <path>`. Then list
hypotheses **ranked by the evidence**, each with what would confirm or refute it and the command to do that.
Eliminate; do not pick the first plausible one.

### 3. Root cause, not symptom

The diagnosis says *why* the code produces the fault, in one paragraph a reviewer can check. "Catch the exception"
or "clamp the value" without the sentence that explains where it comes from is not a cause.

### 4. The class, not the instance

With the cause known, search the whole repository for the same pattern (`src`, `tests`, `tools`: a concrete grep,
listed with its results in the diagnosis) and name every other occurrence. Check the Known traps too. The fix
covers every occurrence, or the user decides explicitly to leave them. This step is not optional and is the one
most often skipped.

### 5. Graded approval

- **Trivial**: one obvious cause, one file, no change to the model, config, data format or behaviour -> fix it,
  add the regression test, report briefly.
- **Standard**: everything else, including any change to an equation, a config value or a calibration -> write the
  diagnosis (below) with the cause, the fix options and their blast radius (which spec tests could move), and
  **wait for the user's confirmation before editing**.
- **Incident**: the user's real data (`%LOCALAPPDATA%\BrainSim`: the checkpoint, `memory.bin`, journals,
  `settings.json`), a published release, or several areas -> a full plan in plan mode. Read-only first, and copy the
  data folder before touching anything. Every state-changing command (a delete, a restore, a migration, a release
  edit) is confirmed one by one. Rollback is `git revert` and a rebuild, or the previous release.

The diagnosis:

```
Symptom:         what the user saw
Reproduction:    the exact command or steps -> what happens
Evidence:        journal rows, events, checkpoint fields, config, commits
Hypotheses:      ranked; each with the check that confirms or refutes it, and its result
Root cause:      one paragraph
Class search:    the grep -> every hit, and whether it has the same fault
Fix options:     each with its blast radius
Regression test: its name; it fails before the fix
Tier:            trivial | standard | incident
```

### 6. Minimal fix with a regression test

The test calls the code the way its user does and fails before the fix, for the reason you expect; it passes after
and is named in the report. No refactoring alongside, no "while I am here". A fix that needs new behaviour, a new
setting or a new D-number is feature work: stop and switch skills.

### 7. Verify and close

The area's tests, the build (`dotnet build BrainSim.slnx -p:UseSharedCompilation=false`, warnings are errors), the
full suite (`dotnet test --solution BrainSim.slnx --no-build`), and the LM Studio scenarios when the fix touches the
agent loop, appraisal, memory or dreams. A bug found in the running app is checked again in the running app. If it
reveals a new trap, add it to Known traps below in the same commit (symptom, cause, where to search, the commit).
If it contradicts a D-number or a doc, correct that.

The commit is `fix(<scope>): <what the user sees now>`, with a body that says where it was found, the cause and the
regression test; commit and push to `main`. Report: symptom, cause, the class search and its result, the fix, the
test, and what was deliberately left.

## Known traps

Traps this repository and this machine already paid for. Add to the list when a fix reveals a new one.

- **Processes that never exit (this machine).** `dotnet` and `git` finish their work but are not reaped, so a
  pipe or a wait hangs forever. Run long commands through `Start-Process` with output redirected to files and poll
  `HasExited` with a timeout. Never pipe `dotnet build` into `grep`. Build with `-p:UseSharedCompilation=false`,
  because VBCSCompiler keeps the pipes open. Check that a background job is making progress; do not wait on it
  blindly. A Windows restart clears it before a long Stryker run.
- **git push hangs in the TLS handshake.** Use `git -c http.sslBackend=openssl push`.
- **Memory pressure.** LM Studio can hold 9 GB with a model loaded, and the OS then kills Stryker or test jobs.
  `~/.lmstudio/bin/lms.exe unload --all` before a mutation run, `load <model> -y` after.
- **UI Automation finds the wrong element.** Look fields up by `AutomationId`, not by name, because the label has
  the same name. An `AutomationProperties.Name` on a TextBlock hides its text from UIA. A PasswordBox cannot be set
  through UIA.
- **Probes and the embedding model.** Probes trained on one embedding model or prefix give nonsense on another;
  `ProbeFile.Fits` must gate them (D-35).
- **The 0.8B model.** It sometimes stores the user's requests or its own replies as facts (D-33), and its output is
  not reproducible: never assert on exact LLM text, only on the engine's response to it.
- **Text written by a script.** A heredoc can put real newlines into a C# string literal (CS1010). Edit C# with the
  Edit tool.
- **Helix keeps the process alive.** The 3D render host runs a foreground thread from `StartD3D` to `EndD3D`,
  and nothing calls `EndD3D` on app shutdown, so now and then the process outlives its closed window and keeps
  `BrainSim.exe` locked. `BrainView` ends it on `Dispatcher.ShutdownStarted`. A hung process: `dotnet tool exec
  dotnet-stack --yes -- report -p <pid>`, and `dotnet-dump` `clrthreads` for which thread is not background.
  Search: `Viewport3DX`, `new Thread(`.
- **Restoring a file from a backup.** `Copy-Item` keeps the backup's old timestamp, so an incremental build skips it
  and the tests still run the temporary version. Touch the file (`LastWriteTime = Get-Date`) before rebuilding.
- **UIA and ComboBoxes.** After a ComboBox dropdown opens and closes through UIA, the window ignores a UIA close
  (SC_CLOSE). Close test runs with `PostMessage(hwnd, WM_CLOSE)`. Whether a real mouse sees it is open (D-37).
- **UIA and MessageBox.** A WPF `MessageBox` is a Win32 `#32770` dialog owned by the window that opened it (find it
  among the descendants, not the top-level windows), and its buttons show up as panes without Invoke. Answer it with
  `PostMessage(dialog, WM_COMMAND, IDOK, 0)`, which moves no mouse (D-39).
- **Signed number formats.** `"+0.00;-0.00"` turns a tiny negative into `-+0.00`: give every
  signed format a zero section, `"+0.00;-0.00;0.00"` (`53fe834`). Search: `\+0(\.0+)?;-0`.
- **Windows PowerShell 5.1.** `Get-Content -Raw` returns a PSObject that serializes wrongly in a JSON body; use
  `[IO.File]::ReadAllText`. Input files piped to native tools must be written without a BOM.

## Hard rules

- No cause without a reproduction or evidence.
- No fix without the class-wide search.
- No fix without a regression test.
- Standard and incident fixes wait for confirmation; "it is obvious" is not a tier.
- Nothing that changes the user's real data folder or a published release without a backup and the user's explicit
  yes for that command.
- The repository is public. Secrets (the API key, the GitHub token) are never printed, logged, committed or pasted
  while debugging: read them only where they are used.
