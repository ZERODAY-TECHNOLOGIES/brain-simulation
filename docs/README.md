# Brain Simulation: planning docs

A Windows desktop app that runs a neurochemical agent and shows its whole "brain" live:
the language cortex (an LLM), the 10-substance chemical state, receptors and tolerance,
effectors, the circadian clock and simulated pineal gland, sleep stages, dreams and,
later, ACT-R memory.

The source of truth for the model is the technical spec (Romanian):
[spec/agent-neurochimic-spec.pdf](spec/agent-neurochimic-spec.pdf).
These docs explain how we build it and where we deliberately differ from it.

## Documents

| File | What it covers |
|---|---|
| [decisions.md](decisions.md) | Every architecture and product decision, with status (decided or open) |
| [architecture.md](architecture.md) | System design, solution layout, execution loop, deviations from the spec |
| [ui-design.md](ui-design.md) | Layout, panels, visual language, colors, 3D scene and models |
| [conventions.md](conventions.md) | Code style, project rules, testing, git, writing rules |

## The short version

- Everything is .NET. One WPF app that hosts the engine in-process. No Python, no IPC.
- The LLM is Qwen3.5-0.8B served by LM Studio on this laptop, over its local HTTP API.
- Because LM Studio does not expose internal activations or allow training, we run the
  spec in **reduced mode**: no steering (E1), no LoRA plasticity (E3). Sampling (E2),
  memory export (E4) and sleep (E5) stay. See [architecture.md](architecture.md#reduced-mode).
- Appraisal uses linear probes trained on text embeddings (also served by LM Studio),
  which is the closest thing to the spec's activation probes we can get without hooks.
- Reward is implicit only (a sentiment probe on the user's reply). On top of that, a
  **Lab panel** lets you change the chemistry by hand while the agent runs.
- The UI is a 3D brain in the center with dockable panels around it, and a timeline at the
  bottom for live view and replay.

## Roadmap

Each phase ends with something you can run and see.

| Phase | Goal | Done when |
|---|---|---|
| 0. Spikes | Prove the risky parts: Helix Toolkit on .NET 10 with transparency, glow and GPU particles on the MX550; LM Studio streaming, logprobs and embeddings from C# | A throwaway window shows a glowing transparent mesh with particles at 60 fps, and a console app streams tokens from Qwen3.5-0.8B |
| 1. Chemistry core | State, dynamics, receptors, config, journal. No LLM, no UI | Tests T1 and T6 pass, eigenvalues of -K + W are all negative |
| 2. Clock and sleep | Circadian clock, pineal, sleep decision, cycles and phase chemistry, wake-up | T8 passes in simulated mode |
| 3. Agent loop | LM Studio client, appraisal probes, RPE, sampling effector, full message and token loop | A conversation moves the chemistry in the right direction (T4, partial) |
| 4. Viewer v1 | WPF shell, docking, chat, chemistry charts, timeline, replay from journal, Lab panel | You can chat, watch every substance live, scrub back through a session |
| 5. 3D brain | Anatomical meshes, holographic shading, nuclei glow, release particles along pathways | Releasing DA by hand visibly flows from VTA to striatum and cortex |
| 6. Dreams | REM generation through LM Studio, dream journal, emotional detensioning | T9 passes (the parts that apply in reduced mode) |
| 7. Memory (ACT-R) | Memory module wired through E4 | Defined in its own spec, next stage of the project |
| 8. Drugs | Agonists, antagonists, reuptake inhibitors, releasers mapped onto occ, rho, k, g | Only after the validation tests pass |

## Status

As of 2026-11-04:
- **Phase 0:** done. LM Studio and Helix Toolkit spikes both pass (results in
  [decisions.md](decisions.md#lm-studio-spike-results-2026-11-04)). The Helix spike lives in
  `spikes/HelixSpike`, outside the solution, and is not production code.
- **Phase 1:** done. Chemistry, receptors, modulation channels, config and the Parquet tick
  journal, with tests for T1, T6, baseline neutrality and stability. The spec's W was
  unstable and had to be recalibrated (D-20).
- **Phase 2:** done. Circadian clock, pineal gland, light and jet lag, sleep decision,
  cycles and phase chemistry, forced and natural wake, all driven by `Brain.Tick(time)`.
  T8 passes over a full simulated day. Three more spec changes: D-21, D-22 and D-23.

- **Phase 3:** done. LM Studio client, appraisal (surprise, probes, RPE, impulses), the E2
  sampling effector and the full agent loop with exact checkpoints. All four probes clear
  AUC 0.85 (D-24). T4 passes in part against the real model.

Run the tests with `dotnet test --solution BrainSim.slnx`. To also run the scenario tests
against LM Studio: `BRAINSIM_LMSTUDIO=1 dotnet test --solution BrainSim.slnx`.
To retrain the probes: `dotnet run --project tools/ProbeTrainer` (about 10 minutes the
first time, while it embeds the datasets).

Open questions are listed at the end of [decisions.md](decisions.md).
