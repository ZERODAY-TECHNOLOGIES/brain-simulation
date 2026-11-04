# Conventions

## Writing rules

These apply to every piece of text in the project: docs, UI copy, code comments, commit
messages, test names and log messages (D-10).

- Write like a person explaining something to a colleague. Plain words, short sentences.
- No emojis.
- No em dashes. Use a period, a comma, a colon or parentheses instead.
- No filler ("It's worth noting that", "In order to", "leverage", "robust", "seamless").
- Prefer the spec's symbols next to a plain name: "sleep pressure (P)", "tolerance (rho)".
- English everywhere (D-09). The spec PDF stays in Romanian.

## C# and .NET

- Target `net10.0-windows` for the app and `net10.0` for the engine and tests (D-11).
- `Directory.Build.props` sets for every project: `Nullable` enabled, `ImplicitUsings`
  enabled, `TreatWarningsAsErrors` true, `LangVersion` latest.
- `Directory.Packages.props` holds every package version (central package management).
- File-scoped namespaces. One public type per file, file named after the type.
- Naming: `PascalCase` for types, methods and properties, `camelCase` for locals and
  parameters, `_camelCase` for private fields. Async methods end in `Async`.
- Prefer `record` and `readonly record struct` for data (frames, events, config sections).
- `var` when the type is obvious from the right side.
- No interface until there is a second implementation. No factories, no service locator.
- The engine library never references WPF. The app never mutates engine state directly,
  only through `Command`s.
- Use `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`)
  instead of hand-written `INotifyPropertyChanged`.
- Deliberate shortcuts get a comment that names the limit and the way out:
  `// ponytail: per-response sampling, switch to per-token requests if T lags (D-04)`.

## Numbers and units

- Ticks are `long`. Concentrations, rho and modulation values are `double`.
- Substances are an `enum Substance { Norepinephrine, Acetylcholine, Dopamine, Gaba,
  Oxytocin, Serotonin, Cortisol, GrowthHormone, Melatonin, Adenosine }` in spec table
  order. The UI and config use the spec abbreviations (NE, ACh, DA, ...). State vectors
  are arrays indexed by the enum, so the 10 x 10 W matrix stays a plain `double[,]`.
- Names follow the spec: `HalfLife`, `Baseline`, `Gain`, `Ec50`, `HillN`, `Emax`, `Rho`,
  `MeanC` (C-bar). The spec equation or table is cited in a comment where the math lives.
- `config.yaml` keys are `snake_case` and mirror the spec tables. Values that the spec
  does not give are marked with `# ours, not in spec`.

## Testing

- xUnit. One test class per spec test where it applies: `T1ImpulseResponseTests`,
  `T6ToleranceWithdrawalTests`, `T8ClockAndSleepTests`, and so on.
- Each test states its pass criterion from the spec in its name or first assert message.
- Engine tests run headless and fast. Anything that needs LM Studio is tagged
  `[Trait("Category", "LmStudio")]` and skipped in normal runs.
- Fixed seeds everywhere randomness appears (dream seed selection, replay order ties).

## Git

- `main` is always buildable. Work happens on short branches: `feat/...`, `fix/...`,
  `docs/...`, `chore/...`.
- Conventional Commits: `feat(engine): add Hill receptor curves`, `fix(ui): ...`,
  `docs: ...`. Scopes: `engine`, `ui`, `3d`, `llm`, `journal`, `tools`, `docs`.
- Commit messages follow the writing rules above.
- Large binaries (`*.glb`, `*.blend`, probe weights) go through Git LFS.

## Docs

- A decision that changes the code goes into [decisions.md](decisions.md) before the code.
- Each doc keeps to its topic. Link instead of repeating.
