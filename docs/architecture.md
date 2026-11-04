# Architecture

## System overview

```
+------------------------------- BrainSim.App (WPF, one process) --------------------------------+
|                                                                                                  |
|   Views (3D brain, chat, charts, Lab, timeline)  <-- Frames --   Engine (single thread)          |
|                                                  -- Commands -->   state, receptors, effectors,  |
|                                                                    appraisal, clock, sleep,      |
|                                                                    dreams, journal               |
+-----------------------------------------------------------------------|--------------------------+
                                                                        | HTTP (localhost:1234)
                                                              +---------v----------+
                                                              | LM Studio          |
                                                              | Qwen3.5-0.8B       |
                                                              | embedding model    |
                                                              +--------------------+
```

- The **engine** owns all mutable state and runs on one dedicated thread. Nothing else
  writes to it. This keeps the order of operations fixed, which is what makes replay exact.
- The **app** sends `Command`s in (user message, Lab action, clock mode, pause) and reads
  immutable `Frame` snapshots out. The 3D view and charts render at display rate from the
  latest frame and a ring buffer of recent frames.
- **LM Studio** is the only external dependency at runtime. It serves the chat model and
  the embedding model.

## Solution layout

Folders, not projects, unless a separate project is actually needed.

```
brain-simulation/
  BrainSim.slnx
  Directory.Build.props        shared build settings (nullable, warnings as errors)
  Directory.Packages.props     central package versions
  .editorconfig
  src/
    BrainSim.Engine/           class library, no UI references
      Config/                  config.yaml + loader            (spec: config.yaml)
      Chemistry/               concentrations, dynamics, W     (spec: state.py)
      Receptors/               Hill curves, tolerance          (spec: receptors.py)
      Effectors/               sampling, memory export         (spec: effectors.py)
      Appraisal/               probes, surprise, RPE, impulses (spec: appraisal.py)
      Circadian/               clock, pineal, light, sleep gate(spec: circadian.py)
      Sleep/                   cycles, phase targets, wake     (spec: sleep.py)
      Dreams/                  seeds, REM generation, journal  (spec: dreams.py)
      Llm/                     LM Studio client (chat stream, embeddings, logprobs)
      Journal/                 Parquet writer and reader       (spec: jurnal)
      Engine.cs                the execution loop              (spec: loop.py)
    BrainSim.App/              WPF app
  tests/
    BrainSim.Engine.Tests/     xUnit, one file per spec test (T1, T6, T8, ...)
  tools/
    ProbeTrainer/              console app, trains and evaluates probes (Phase 3)
  assets/
    models/                    glTF meshes + ATTRIBUTION.md
  docs/
```

`plasticity.py` has no counterpart: see [Reduced mode](#reduced-mode).

## Time and ticks

- 1 tick = 1 generated token while the agent is responding.
- When the agent is not generating, a clock advances 10 ticks per real second (real mode)
  or 10 x f ticks per second (simulated mode). Wall-clock ticking pauses during
  generation, so a tick is never counted twice.
- When the app starts after being closed, the engine catches up the elapsed time first,
  running sleep for that interval if the sleep conditions were met (spec loop step 1).
- All integration is Euler with dt = 1, including idle time and catch-up (D-12).
- The engine publishes at most 30 frames per second. In fast simulated mode it integrates
  many ticks between frames; the journal still gets every tick.

## Execution loop

Same order as the spec, with reduced-mode changes marked.

**On user message**
1. Flush pending clock ticks. If the app was closed, catch up (with sleep if due).
   A message during sleep triggers a forced wake first.
2. Embed the message through LM Studio. Request prompt logprobs if available (O-04).
3. Compute S, r, A, W. r is the sentiment of this message, applied to the previous
   response: delta = r - V.
4. Schedule the impulses over the next 10 ticks.
5. *(Reduced mode)* Compute T, top-p and max length from the current chemistry and start
   the streaming request (D-04).

**On each streamed token**
6. Integrate one tick: concentrations first, then C-bar and rho.
7. Compute Delta and m_k for every channel, including the six steering directions, which
   are shown in the UI but not applied (no hook).
8. Append the token, write the journal row, publish a frame.
9. *(Not in reduced mode)* LoRA gradient step every 64 tokens.

**After the response**
10. ADO += 0.001 x E. Update V and the surprise running stats.
11. Check the sleep decision: P = ADO - C(phi), MEL level, user idle time.
12. Save the checkpoint.

**During sleep** the loop runs on phases: advance the clock, swap the baseline targets at
each phase change, log replay order in NREM, generate the dream in REM.

## Reduced mode

What the spec asks for and what we can do with LM Studio.

| Spec item | Status | Notes |
|---|---|---|
| Chemistry, W, special dynamics | Full | |
| Receptors, tolerance, withdrawal | Full | |
| Appraisal S, r, A, W | Adapted | Probes on embeddings instead of residual stream (D-05). S from prompt logprobs or embedding novelty (O-04) |
| E1 steering | Display only | m_k computed and shown, never applied |
| E2 sampling | Adapted | Once per response (D-04) |
| E3 plasticity | Not available | No LoRA. eta is computed and shown |
| E4 memory export | Full | Consumed by ACT-R in Phase 7 |
| E5 sleep | Full | |
| NREM replay | Logged | Salience order is computed and logged, feeds ACT-R later |
| Downscaling, pruning | Not available | Act on LoRA weights. GH is still simulated and shown |
| Dreams | Adapted | Generated by LM Studio from the seed prompt with REM temperature |
| T1, T4, T6, T8 | Apply | |
| T2, T3, T7 | Do not apply | Need hooks or training |
| T5 | Changes meaning | Only sampling differs between the chemistry and the prompt control |
| T9 | Partial | Perplexity part needs logprobs |

Upgrade path: a second backend running transformers (Python or TorchSharp) behind the
same `Llm/` boundary. Not built until needed.

## Dreams in reduced mode

At the start of a REM period of length D, the engine picks 3 to 5 fragments (probability
proportional to their emotional tag), shuffles them into a prompt, and requests
D / 2 seconds worth of tokens with the REM temperature. Tokens are revealed and journaled
at 1 token per 2 seconds of REM. A forced wake truncates the dream at the revealed point,
and that text enters the wake-up context. Fragment tags drop by 20% after use.

Until ACT-R exists, fragments are a simple list in the checkpoint: one entry per turn,
tagged with DA, NE and CORT at encoding.

## Lab panel and determinism

Lab actions (inject, hold, edit a parameter) are `Command`s like any other. The engine
applies them at a tick boundary and writes them to the events journal. Replay re-applies
them at the same tick. Editing W or a half-life re-runs the stability check
(eigenvalues of -K + W) and the UI warns if the system becomes unstable.

LM Studio output is not reproducible across machines, so replay never calls the LLM. It
reads tokens from the journal.

## Persistence

| What | Format | When |
|---|---|---|
| Tick journal | Parquet, one row per tick: tick, wall time, phase, C, C-bar, rho, m_k, T, top-p, eta, token | Row group every 1,000 ticks |
| Events journal | Parquet: tick, type, JSON payload (messages, Lab actions, phase changes, sleep and wake) | Same |
| Checkpoint | JSON: state, V, surprise stats, clock offset, sleep state, fragments, dream journal | After each response and on exit |
| Config | `config.yaml`, versioned in git | Lab can save a copy |

A crash loses at most the last row group.

## Spec gaps we have to fill

The spec leaves these values or shapes undefined. Proposed defaults go in `config.yaml`
and are marked as ours, not the spec's.

| Gap | Proposed default |
|---|---|
| Emax, EC50, n per substance and channel | EC50 = 1.0, n = 1.5, Emax = +/-0.5 with the signs from the steering table |
| g_GABA in the m_k denominator | 1.0 |
| p_max of the pineal | Chosen so MEL settles near 2.0 in the dark window |
| GABA tonic production | A source term that exactly balances decay at baseline |
| CORT circadian shape | Minimum at sleep onset, linear rise from 03:00, +50% peak in the first 30 minutes after waking |
| Max length and top-p mappings | Linear in (C - 1) clamped to the spec bounds (-30% length, top-p 0.95 to 0.8) |
| "Elevated" thresholds | C > 1.5 |
| Rho per channel or per substance | Per substance (same gamma everywhere), until a channel needs its own |
