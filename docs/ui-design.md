# UI design

## Principles

1. **The brain is the hero, the numbers are the truth.** The 3D view is there for intuition.
   Every value it shows can also be read exactly in a panel.
2. **Baseline is calm.** At baseline (all C = 1.0) the scene is quiet: dim, steady glows, no
   particles. Anything that moves means something changed.
3. **One color per substance, everywhere.** The same color in the 3D scene, the charts, the
   Lab and the chat annotations.
4. **Live and replay look the same.** Replay is the live view driven by the journal instead
   of the engine.
5. **Responsive on every screen.** The app must work from a 1366 x 768 laptop at 125% scaling
   up to large 4K monitors. Panels split the window in proportions, not fixed pixels; lists
   and forms scroll instead of clipping; the toolbar wraps onto a second line when it runs
   out of room. Minimum window size is 1024 x 640. Every layout change is checked at both a
   small window and full screen.

## Layout

Decided in D-07. 3D in the center, dockable panels around it (AvalonDock), timeline at the
bottom. Every panel can be moved, tabbed, floated or hidden. The layout is saved on exit.

```
+- Toolbar: LIVE | Replay | 23:14 Night | Phase: Wake | LM Studio: connected -----------+
| +- Layers ----+ +------------ Brain 3D --------------+ +- Chat ---------------------+ |
| | [x] Cortex  | |                                    | | user: ...                  | |
| | [x] Nuclei  | |        holographic brain           | | agent: ...                 | |
| | [x] Paths   | |     particles NE, DA, 5-HT ...     | |   S 0.4  A 0.1  W 0.7 ...  | |
| | [ ] HPA     | |                                    | | > _                        | |
| +-------------+ +------------------------------------+ +----------------------------+ |
| +- Chemistry -+- Receptors -+- Effectors -+- Clock -+- Sleep -+- Dreams -+- Lab ----+ |
| | NE ####__  DA ###__  CORT ##___  ...   (time series, baseline at 1.0)            | |
| +---------------------------------------------------------------------------------+ |
| |<< > >> --------------o----------------  tick 48,213   speed x1   events: | | |    |
+---------------------------------------------------------------------------------------+
```

## Panels

| Panel | Contents |
|---|---|
| **Toolbar** | Live or Replay, clock time and mode (Real, or Simulated with factor f), sleep phase, LM Studio status and model name |
| **Layers** | Toggles: cortex, subcortical structures, nuclei, pathways, HPA axis, labels. Cortex opacity slider |
| **Brain 3D** | See [3D scene](#3d-scene). Hover a structure for its substances and values. Click a nucleus to open its substance in the Lab |
| **Chat** | The conversation, tokens streaming in. Under each user message a small appraisal line: S, A, W, r, delta. Under each agent reply: T, top-p, max length used. No reward buttons (D-06) |
| **Chemistry** | Strip charts of all 10 substances with the baseline line at 1.0 (or the current phase target during sleep). Window: 1k, 10k, 100k ticks or a full day. Current values as bars beside the charts |
| **Receptors** | rho per substance over time. Hill curve per substance with markers for the current C and the baseline. Status per substance: normal, tolerant, withdrawal, sensitized |
| **Effectors** | Applied: T, top-p, max length. Computed but not applied (marked as such): the six steering directions as bipolar bars, eta. E4 exports: encoding strength, activation noise, retrieval threshold, consolidation boost |
| **Clock** | 24-hour dial with phi, the dark window arc and light periods. Plots of MEL, circadian alertness C(phi) and sleep pressure P with the 2.0 and 0.5 thresholds |
| **Sleep** | Live hypnogram (same style as the spec), cycle number, phase targets table, NREM replay list |
| **Dreams** | Dream journal: time, seed fragments, chemistry snapshot, text. The dream in progress is revealed at 1 token per 2 s |
| **Lab** | One row per substance: value, Inject (amount), Hold at level, edit b, T-half and gains. W as an editable heatmap. Stability indicator for -K + W. Reset to config, save as new config. Every action shows up as a marker on the timeline |
| **Timeline** | Play, pause, step, speed (x1, x10, x60, x600), scrub. Markers for messages, Lab actions, phase changes, sleep and wake |

The memory panel arrives with Phase 7.

## Visual language

Dark theme only, since the scene is lit from within. A light theme can come later if
someone asks.

### Base colors

| Token | Hex | Use |
|---|---|---|
| `bg` | `#0B0F14` | Window and 3D background |
| `surface` | `#121821` | Panels |
| `border` | `#1F2A36` | Panel edges, chart grid |
| `text` | `#E6EDF3` | Primary text |
| `text-muted` | `#8B98A5` | Labels, units |
| `warn` | `#F59E0B` | Instability, LM Studio disconnected |

### Substance colors

Ten colors cannot all be distinct for every reader, so color is never the only cue: every
series and every glowing structure has a label.

| Substance | Hex | Why this color |
|---|---|---|
| NE | `#F5A524` | Amber, alert |
| ACh | `#22D3EE` | Cyan, attention to the new |
| DA | `#D946EF` | Magenta, reward |
| GABA | `#3B82F6` | Blue, calm and inhibition |
| OXT | `#FDA4AF` | Soft pink, warmth |
| 5-HT | `#A78BFA` | Violet, stability |
| CORT | `#EF4444` | Red, stress |
| GH | `#34D399` | Green, repair |
| MEL | `#C7D2FE` | Pale moonlight |
| ADO | `#A3826B` | Muted brown, fatigue |

### Type and icons

- UI text: Segoe UI Variable (ships with Windows 11).
- Numbers: Cascadia Mono, tabular, so changing values do not jitter.
- Icons: Segoe Fluent Icons (ships with Windows 11).
- Concentrations with 2 decimals, ticks with thousands separators, units always shown.

### Copy

Sentence case. Short labels. Plain words over jargon where a plain word exists, with the
spec symbol next to it (for example "Sleep pressure (P)"). No emojis, no em dashes (D-10).

## 3D scene

### Style

Hybrid holographic (D-08):
- **Cortex:** fresnel shading, transparent in the middle and bright at the edges, so the
  inside is visible. A faint ripple runs across it with each generated token.
- **Subcortical structures:** a little more opaque, low emissive tint.
- **Nuclei:** small and bright. Emissive intensity follows the concentration of their
  substance. Below baseline they desaturate and dim below the resting glow.
- **Pathways:** thin curves from each nucleus to its targets. Particles flow along them
  while there is release, with rate proportional to the impulse.
- **Bloom** on emissive parts, subtle.

### Anatomical mapping

| Substance | Source (glows) | Targets (particles flow to) |
|---|---|---|
| NE | Locus coeruleus (pons) | Whole cortex, thalamus, cerebellum |
| ACh | Basal forebrain (nucleus basalis) | Cortex, hippocampus |
| DA | VTA and substantia nigra | Striatum (caudate, putamen), prefrontal cortex |
| 5-HT | Raphe nuclei (brainstem) | Cortex, hippocampus, amygdala |
| GABA | No single source: cortical interneurons | Shown as a blue haze over the cortex that dims everything else as it rises |
| OXT | Hypothalamus (PVN, SON) | Posterior pituitary, amygdala |
| CORT | HPA axis: hypothalamus, pituitary, adrenal (inset outside the brain) | Back to hippocampus and prefrontal cortex |
| GH | Anterior pituitary | Release pulses at the pituitary |
| MEL | Pineal gland, driven by the SCN | SCN to pineal pathway lit at night, release pulses at the pineal |
| ADO | Extracellular, no source | A slowly thickening fog, denser around the basal forebrain |

Other structures with a role:
- **Amygdala** lights up with threat A on each message.
- **Hippocampus** lights up when dream seeds are picked. Later it hosts the ACT-R memory.
- **SCN** pulses with the clock and is the source of the pineal pathway.

### Scene states

| State | Look |
|---|---|
| Wake | Neutral lighting |
| N1, N2 | Lights dim, cooler tint |
| N3 | Dark deep blue, slow GH pulses at the pituitary |
| REM | Cortex flickers, hippocampus active, dream text in the Dreams panel |
| Forced wake | Lights come back gradually, matching sleep inertia in the chemistry |

### Camera

Orbit, pan and zoom with the mouse. Presets: lateral, medial (sagittal cut), top, front.
Optional slow auto-rotate when idle.

### Performance budget (MX550, 2 GB)

- At most 300k triangles in the scene.
- At most 20k live particles.
- One order-independent transparency pass for the cortex.
- 60 fps target at 1080p. Phase 0 spike checks this on the real hardware.

### Model pipeline

1. Source meshes from Z-Anatomy (D-16).
2. In Blender: keep the structures from the mapping table, decimate to the budget, put
   everything in one coordinate frame, name meshes `region.<name>` (for example
   `region.locus_coeruleus`).
3. Add proxy shapes for nuclei too small to have meshes (locus coeruleus, raphe, VTA,
   nucleus basalis, SCN).
4. Export one `brain.glb` to `assets/models/`, with credits in `assets/models/ATTRIBUTION.md`.
5. Pathways are not modeled. The app builds them as splines from the source centroid to
   each target centroid, with per-path bend offsets in a small JSON file.
