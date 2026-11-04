# Decision log

Every decision that shapes the code goes here. Once something is decided, it stays decided
unless we write a new entry that replaces it. Dates are absolute.

Status values: **Decided**, **Open**, **Default** (we go with this unless someone objects).

## Decided

### D-01. Everything in .NET, engine in-process
- **Date:** 2026-11-04
- **Context:** The spec describes a Python package on top of Hugging Face transformers.
  That made sense for hooks and LoRA training. With LM Studio as the LLM (D-03), the
  engine is a few dozen numbers, an HTTP client and some linear classifiers.
- **Decision:** One .NET solution. The engine is a class library used directly by the WPF
  app. No Python, no gRPC, no second process.
- **Consequence:** The spec's module list (`state.py`, `appraisal.py`, ...) maps to C#
  folders one to one (see [architecture.md](architecture.md#solution-layout)). If we ever
  need the full spec (steering, LoRA), that backend will need Python or TorchSharp, and we
  will revisit this entry then.

### D-02. UI stack: WPF + Helix Toolkit (SharpDX) + ScottPlot + AvalonDock
- **Date:** 2026-11-04
- **Decision:** WPF for the shell, HelixToolkit.Wpf.SharpDX for 3D (DirectX 11),
  ScottPlot for real-time charts, AvalonDock for dockable panels, CommunityToolkit.Mvvm
  for view models.
- **Risk:** SharpDX itself is no longer maintained. Helix Toolkit still ships on it.
  Phase 0 spike must confirm it runs on .NET 10 and on the MX550 with transparency,
  bloom and GPU particles before we commit code to it.

### D-03. LLM: Qwen3.5-0.8B through LM Studio, reduced mode
- **Date:** 2026-11-04
- **Decision:** The cortex is Qwen3.5-0.8B served by LM Studio on localhost, through its
  OpenAI-compatible API with streaming.
- **Consequence:** No access to the residual stream and no training. E1 (steering) and
  E3 (LoRA plasticity) are not implemented. Tests T2, T3 and T7 do not apply. T5 and T9
  apply only in part. Full list in [architecture.md](architecture.md#reduced-mode).

### D-04. Sampling parameters are set once per response
- **Date:** 2026-11-04
- **Decision:** T, top-p and max length are computed from the chemistry when the response
  starts and sent with the request. The chemistry still advances one tick per streamed
  token, so the display stays live.
- **Ceiling:** If one response ends up much longer than NE's half-life (40 ticks), the
  temperature lags behind the chemistry. Upgrade path: one request per token with
  `max_tokens = 1` and prefix caching.

### D-05. Appraisal through linear probes on embeddings
- **Date:** 2026-11-04
- **Decision:** LM Studio serves an embedding model. We train small linear classifiers
  (logistic regression) on those embeddings for threat A, social warmth W and sentiment r.
  The spec's acceptance gate stays: a probe ships only with AUC > 0.85 on held-out data.
- **Data and results:** D-24. The embedding model is settled by D-17, surprise S by D-19.

### D-06. Reward is implicit only, plus a live chemistry Lab
- **Date:** 2026-11-04
- **Decision:** r comes only from the sentiment probe on the user's next message. No like
  or dislike buttons. Separately, the Lab panel lets the user change the chemistry while
  the agent runs: inject an impulse, hold a substance at a level, edit baselines, half-lives,
  gains and W entries.
- **Rule:** Every Lab action is an event in the journal with its tick, so a session can
  still be replayed exactly.

### D-07. Layout: 3D in the center, dockable panels, timeline at the bottom
- **Date:** 2026-11-04
- **Decision:** See [ui-design.md](ui-design.md#layout).

### D-08. 3D style: hybrid holographic
- **Date:** 2026-11-04
- **Decision:** Real anatomical meshes rendered in a holographic style: fresnel-transparent
  cortex, glowing nuclei, colored particles flowing along projection pathways.

### D-09. Language: English everywhere
- **Date:** 2026-11-04
- **Decision:** Code, comments, commit messages, docs and UI text are in English. The spec
  PDF stays in Romanian as the reference.

### D-10. Writing rules for all project text
- **Date:** 2026-11-04
- **Decision:** All text in this project reads like a person wrote it. No emojis. No em
  dashes. This covers docs, UI copy, code comments and commit messages.
  Details in [conventions.md](conventions.md#writing-rules).

## Default

### D-11. Target .NET 10 (LTS)
.NET 9 support ends on 2026-11-10. The .NET 10 desktop runtime is already installed, but
only SDK 9 is. Action: install the .NET 10 SDK before Phase 0.

### D-12. Idle time is integrated with Euler, not the closed form
The spec applies the closed form `C(t+D) = b + (C(t) - b) e^(-kD)` between messages. That
form ignores the W interactions, GABA's tonic production, MEL from the pineal and the
CORT circadian rhythm, so a night of idle time would skip the very things that should
happen at night. Euler on 10 variables is cheap: a full 8-hour night at 10 ticks per
second is 288,000 steps, well under a second. So we use one code path (Euler) everywhere
and keep the closed form only as the reference in test T1.

### D-13. Real clock in the system time zone, dark window 21:00 to 07:00
Spec defaults. Simulated mode with factor f is available from the toolbar for testing.

### D-14. Time conversion: 1 second = 10 ticks
Spec default, kept in `config.yaml`.

### D-15. Git: Conventional Commits on short-lived branches off `main`
See [conventions.md](conventions.md#git).

### D-16. 3D meshes from Z-Anatomy
- **Date:** 2026-11-04
- **Decision:** Z-Anatomy (derived from BodyParts3D, CC BY-SA). It covers cortex,
  cerebellum, brainstem, thalamus, hypothalamus, hippocampus, amygdala, basal ganglia,
  pituitary and pineal as separate meshes. Nuclei too small to have meshes (locus
  coeruleus, raphe, VTA, nucleus basalis, SCN) get proxy shapes at their anatomical
  positions.
- **Consequence:** Credits go in `assets/models/ATTRIBUTION.md`. The derived meshes are
  shared under the same license.

### D-17. The agent chats in English
- **Date:** 2026-11-04
- **Decision:** A 0.8B model writes far better English than Romanian, so the conversation
  is in English.
- **Consequence:** Probes use `nomic-embed-text-v1.5`, which is already installed in
  LM Studio, and are trained on public English datasets.

### D-18. Writing rules do not apply to the agent's output
- **Date:** 2026-11-04
- **Decision:** D-10 covers project text only. The agent's replies and dreams are not
  filtered.

## Helix Toolkit spike results (2026-11-04)

`spikes/HelixSpike`: HelixToolkit.Wpf.SharpDX 3.1.2 on .NET 10, on this laptop's MX550.
- Runs on .NET 10 with no compatibility problems (the package targets net6.0-windows and net8.0-windows).
- Scene: transparent cortex ellipsoid (weighted OIT), 5 emissive nuclei with bloom, 20,000
  points rebuilt on the CPU every frame along 11 Bezier pathways, with bloom too.
- **59 to 60 frames per second** after warm-up, which is the vsync cap. D-02 risk closed.
- Bloom (`PostEffectBloom` with `PostEffects = "bloom"`) and transparency work as expected.
- **No fresnel material ships with Helix.** The cortex looks like a flat translucent
  shell. Phase 5 options: `PostEffectMeshXRay` for the rim glow, or a custom shader.
- At full release rate the particles merge into continuous lines. Phase 5 should spawn
  particles in proportion to the impulse instead of keeping a fixed pool always moving.
- Helix 3 namespaces: `HelixToolkit` (collections), `HelixToolkit.Maths` (Color4),
  `HelixToolkit.Geometry` (MeshBuilder), `HelixToolkit.SharpDX` (geometries, OIT),
  `HelixToolkit.Wpf.SharpDX` (WPF elements). The XML docs are incomplete, so check the
  compiler first.

### D-20. Slow OXT, 5-HT, CORT interactions scaled by 0.4
- **Date:** 2026-11-04
- **Context:** With the spec's W, the linearized system -K + W has an eigenvalue of
  +1.4e-4, so the spec fails its own stability criterion. The cause is two pairs of mutual
  inhibition, CORT and 5-HT, and CORT and OXT. Each pair is a positive feedback loop whose
  gain (5e-8 and 6e-8) matches or beats the product of the two decay rates (3e-8 and
  6e-8). CORT decays too slowly to hold them back. Left alone, sustained stress locks the
  system into high CORT and low 5-HT.
- **Decision:** Multiply the five W entries among OXT, 5-HT and CORT by 0.4. Half-lives
  and signs stay as in the spec. The largest eigenvalue becomes -3.1e-5, so the slowest
  mode settles in about 6 hours of ticks.
- **Rejected:** x0.5 (barely stable, -6.3e-6, and one Lab edit could tip it over); CORT
  half-life 2000 (CORT would no longer be the slow stress signal); keeping the
  instability as a feature (contradicts the spec's validation).
- **Check:** `StabilityTests` runs on every build.

### D-21. Sleep pressure is measured from the ADO baseline
- **Date:** 2026-11-04
- **Context:** The spec defines P = ADO - C(phi) and wakes the agent when P < 0.5. ADO
  drains toward its baseline of 1.0 in sleep and C(phi) never exceeds 0.5, so P never drops
  below 0.5 and natural wake could never happen.
- **Decision:** P = (ADO - b_ADO) - C(phi), the same baseline-relative reading used by W and
  the receptors. This also keeps T8's "ADO under 1.2 after a full night" meaningful.
- **Consequence:** Falling asleep at about 22:30 needs ADO around 3.2, which means about
  2,200 generated tokens since the last sleep (see O-05).

### D-22. The cortisol awakening response is secretion, not a moved target
- **Date:** 2026-11-04
- **Context:** Raising b_CORT to 1.5 after waking makes W read CORT as below its target,
  which pushes 5-HT up and drags CORT back down. The peak only reached +20%.
- **Decision:** After a natural wake, CORT gets a constant source for 30 minutes, sized so
  CORT alone would reach 1.5. The CORT and 5-HT loop carries the peak to about +60%.
  T8 accepts a peak between 1.35 and 1.75.

### D-23. ADO also rises with time awake
- **Date:** 2026-11-04
- **Context:** In the spec ADO grows only with effort. A quiet day gave the agent too little
  sleep pressure to ever fall asleep.
- **Decision:** Like process S in Borbely's model, ADO gets a small constant source on every
  awake tick, 3.8e-6, sized so a day with no conversation ends in sleep around 23:00.
  Effort still adds 0.001 per generated token on top.
- **Consequence:** A busy day reaches the forced-sleep limit (ADO 5.0) sooner: after about
  1,800 generated tokens on top of a full day awake. T8 checks both a quiet day and a day
  with hourly conversation.

### D-24. Probe data and results
- **Date:** 2026-11-04
- **Decision:** Four linear probes on `nomic-embed-text-v1.5` embeddings, trained by
  `tools/ProbeTrainer` and stored in `src/BrainSim.Engine/probes.json`:

  | Probe | Signal | Data | AUC (held out) |
  |---|---|---|---|
  | hostility | threat A | tweet_eval `offensive`, 8,000 train, 860 test | 0.868 |
  | warmth | social W | GoEmotions: admiration, caring, gratitude, love vs the rest | 0.919 |
  | positive | half of r | GoEmotions positive group | 0.894 |
  | negative | half of r | GoEmotions negative group | 0.881 |

  r = p(positive) - p(negative). A and W count only above a probability of 0.5, rescaled
  to [0, 1], because neutral messages score up to about 0.3 on both.
- **Check:** On sample messages, questions and requests stay under 0.3 on hostility and
  warmth, "You are useless" gives hostility 0.83, "Thanks, that helps" gives warmth 0.99,
  "That answer was wrong" gives r = -0.61. T4 passes in part against the real model
  (`BRAINSIM_LMSTUDIO=1`): hostility raises CORT, which then recovers, and praise gives
  DA and OXT peaks.

## Open

No open questions right now.

## LM Studio spike results (2026-11-04)

Measured on this laptop with LM Studio's local server:
- The server runs on port **1235** here, not the usual 1234, so the base URL is a config value.
- Qwen3.5-0.8B (GGUF, 1.02 GB) generates about **21 tokens per second**.
- Streaming sends one SSE chunk per token, so 1 tick = 1 chunk works directly.
- `/v1/chat/completions` returns logprobs for **generated** tokens only.
  `/v1/completions` with `echo` returns no logprobs at all. There are **no prompt logprobs**.
- `/v1/embeddings` with `nomic-embed-text-v1.5` returns 768-dimensional vectors.
- The model reports `reasoning_tokens: 0` by default, so thinking mode is off.

### D-19. Surprise S from embedding novelty
- **Date:** 2026-11-04
- **Context:** The spec uses the mean per-token NLL of the user message. LM Studio has no
  prompt logprobs (see the spike results above).
- **Decision:** S is the novelty of the message embedding: 1 minus the max cosine
  similarity to the last ~50 messages, z-scored against the running stats of those same
  novelty values, clipped to [0, 3].
- **Consequence:** The perplexity part of T9 also stays out of reach. Generated-token
  logprobs are still journaled, since they are free.

### Spec decisions that no longer apply
From the spec's open list: the exact base model is now Qwen3.5-0.8B (D-03); probes versus
a separate classifier is settled by D-05; the reward source by D-06.
