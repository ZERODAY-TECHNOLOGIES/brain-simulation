# Endocannabinoids (Phase 8)

The endocannabinoid system (eCB) is not in the spec. We add it because it is the brain's own
brake on stress and arousal, it ties together pieces the engine already has (the HPA axis,
reward, sleep pressure, memory and dreams), and it is the base the drugs phase needs.

Status: design approved in conversation on 2026-11-06 (two substances, a multiplicative
brake on release plus W terms, Phase 8 with drugs moved to Phase 9, a glow in the 3D brain).
This document awaits review. Decision: D-36.

## Why it needs its own mechanism

Every substance in the engine works the same way: a source releases it, it decays toward a
baseline, W couples it linearly to the others, and receptors turn it into channel output.
Endocannabinoids work differently:

- They are made **on demand** by a neuron that has just been strongly activated, not stored
  and released by a nucleus.
- They travel **backwards** across the synapse and act on CB1 receptors on the presynaptic
  terminal, where they **reduce release** of the transmitter that activated the neuron.

So the core of the model is a brake: a gain on release, driven by CB1 tone. W terms carry
only the slow, systemic links (the HPA loop, oxytocin).

## Goals

- Two substances with the behavior the literature reports:
  - **Anandamide (AEA)**: slow tone. Acute stress lowers it (CRF raises FAAH, which degrades
    it), oxytocin raises it, it peaks around waking.
  - **2-arachidonoylglycerol (2-AG)**: fast, made on demand by strong release, recruited by
    cortisol after stress, and it ends the cortisol response. Its daily rhythm peaks early in
    the afternoon, and sleep restriction makes that peak higher and later.
- CB1 tone brakes stress-driven release of NE and CORT and helps phasic DA.
- Less CB1 tone (low AEA) means less buffering: the same threat gives a bigger response.
- eCB weakens encoding, helps extinction (dreams detension faster), and adds to sleep pressure.
- CB1 tolerance with the existing rho mechanism.
- Everything visible: charts, receptors, Lab, the effectors panel and a glow in the 3D brain.

## Non-goals

CB2 and the immune side, TRPV1, appetite and pain (the agent has no body), synapse-level
detail (DSI time courses per synapse), and any exogenous cannabinoid. Exogenous agonists,
FAAH and MAGL inhibitors belong to Phase 9 (drugs), which builds on this.

## Substances

Two more entries in `Substance`, after the spec's ten, so the first ten keep their indexes:

| Substance | Abbreviation | Half-life | Character |
|---|---|---|---|
| Anandamide | `AEA` | 3000 ticks (ours) | Tone. Partial agonist at CB1 |
| 2-AG | `2-AG` | 600 ticks (ours) | Phasic, on demand. Full agonist at CB1 |

Both are normalized to baseline 1.0 like the others. The half-lives keep the order the
literature gives (2-AG is cleared fast by MAGL, AEA more slowly by FAAH) on the engine's tick
scale, which is not physiological for any substance.

## CB1 tone

One number, computed every tick after the receptors step:

```
cb1 = Delta(AEA term) + Delta(2-AG term)       zero at baseline with rho = 1
```

The terms use the existing Hill `Delta` (`ReceptorState.Delta`), so tolerance comes from the
existing per-substance rho. AEA has the smaller Emax because it is a partial agonist. cb1 is
not divided by the GABA damping the output channels use: it is a receptor tone, not an output.

`cb1` is positive when there is more eCB than at rest (more braking) and negative when there
is less (less buffering).

## The brake

Endogenous release is scaled by a gain per substance:

```
gain_i = clamp(1 + r_i * cb1, gain_min, gain_max)
```

| Substance | r_i | Why |
|---|---|---|
| NE | negative | CB1 on terminals in the stress circuits (locus coeruleus, amygdala) |
| CORT | negative | CB1 on glutamate terminals onto CRH neurons in the PVN |
| DA | positive | 2-AG from DA neurons silences their GABA input in the VTA, so phasic DA rises |

The gain applies in one place, `Appraiser.Release`, which is where every endogenous impulse
starts. Lab injections are not braked: they stand for the experimenter, not for the brain.
At baseline every gain is exactly 1, so nothing in the spec's behavior changes until eCB moves.

## On-demand 2-AG

Each appraisal release of NE, DA or CORT also releases 2-AG:

```
2-AG impulse = on_demand * sum of the positive NE, DA and CORT impulses of that message
```

This is the activity-driven part (the neuron that fires hard makes its own brake). It is an
ordinary impulse, spread over the next 10 ticks like the others, so the chemistry stays in one
code path and replay stays exact.

## W terms

| From | To | Sign | Source |
|---|---|---|---|
| CORT | 2-AG | + | Glucocorticoids recruit 2-AG (Di et al. 2003, Hill et al. 2011) |
| 2-AG | CORT | - | 2-AG in prefrontal cortex ends the HPA response (Hill et al. 2011) |
| OXT | AEA | + | Oxytocin mobilizes AEA for social reward (Wei et al. 2015) |

The CORT and 2-AG loop is negative feedback, which adds stability. AEA has no W output, so no
new loop closes through it.

**Sleep goes through P, not W.** AEA promotes sleep (in rats it raises basal forebrain
adenosine, Murillo-Rodriguez et al. 2003). A W term AEA to ADO would close a loop back to ADO
(ADO to NE to CORT to OXT to AEA to ADO, with a positive product). Awake ADO has no decay, so its
eigenvalue is exactly zero, and any loop back to it pushes that eigenvalue above zero. A slow
runaway, but the stability check would fail. So AEA adds to sleep pressure instead:
`P = (ADO - b) - C(phi) + s_aea * (AEA - 1)`.

## Stress, in order

What a hostile message does, which is also the first acceptance test:

1. The threat appraisal releases NE and CORT, each scaled by the brake, and **lowers AEA**
   (a negative threat gain, the FAAH effect). Less AEA means a smaller cb1 and a stronger
   response next time, until AEA recovers.
2. On-demand 2-AG follows the NE and CORT release within ticks.
3. CORT recruits more 2-AG over minutes (W). 2-AG pulls CORT back down (W) and raises cb1,
   which brakes further release.

With CB1 effectively blocked (2-AG and AEA held at baseline from the Lab), step 3 is missing
and CORT stays up longer, as in Hill et al. 2011.

## Daily rhythms

Targets set in `Brain.ApplyTargets`, next to the cortisol and melatonin ones. They follow the
clock, not the sleep phase:

- **2-AG:** `b = 1 + A(ADO) * cos(2 pi (h - h_peak(ADO)) / 24)`, with peak at 13:30. Sleep debt
  (ADO above rest after waking) raises the amplitude and moves the peak later. Hanlon et al.
  2016 report a later, longer peak after 4 nights of 4.5 hours of sleep.
- **AEA:** a smaller rhythm with its peak at waking (07:00), after Vaughn et al. 2010.

Both moved targets show up as modulation, the same way the cortisol rhythm does, because
receptors measure from the resting baseline.

## Effects outside chemistry

| Where | What | Source |
|---|---|---|
| `calm_anxious` channel | AEA and 2-AG terms, positive | Anxiolytic eCB tone |
| `memory_encoding` channel (E4) | 2-AG term, negative | CB1 impairs hippocampal encoding |
| Dream detension | `detension * (1 + extinction_gain * max(0, cb1))`, capped | CB1 is needed to extinguish aversive memories (Marsicano et al. 2002) |
| Sleep pressure P | `s_aea * (AEA - 1)` | See W terms |

## Tolerance

The existing rho per substance. Holding 2-AG high lowers rho for 2-AG, so cb1 and the brake
weaken over time, and recover with tau_rho once the level drops. A PET study in daily cannabis
users found CB1 about 20% lower, back to normal after about 4 weeks without use (Hirvonen et
al. 2012). That magnitude is the calibration target. CB1 is one receptor and rho is per
substance, so AEA and 2-AG get separate rho values; this is a simplification that holds while
gamma is the same everywhere.

## Configuration

New keys, all `# ours, not in spec`. The numbers are starting points. Calibration fixes them
against the acceptance tests and records the result in D-36.

```yaml
half_life:
  AEA: 3000
  2-AG: 600

interactions:
  - { from: CORT, to: 2-AG, w: 0.0004 }
  - { from: 2-AG, to: CORT, w: -0.0001 }
  - { from: OXT,  to: AEA,  w: 0.0003 }

appraisal:
  gains:
    threat: { CORT: 0.3, NE: 1.2, AEA: -0.5 }

modulation:
  channels:
    calm_anxious:    [ ..., { substance: AEA, emax: 0.3 }, { substance: 2-AG, emax: 0.2 } ]
    memory_encoding: [ ..., { substance: 2-AG, emax: -0.3 } ]

endocannabinoid:
  cb1:
    - { substance: AEA,  emax: 0.4 }
    - { substance: 2-AG, emax: 0.6 }
  release_gain: { NE: -2.0, CORT: -2.0, DA: 1.0 }
  gain_min: 0.25
  gain_max: 2.0
  on_demand: 0.3
  sleep_pressure: 0.5
  extinction_gain: 1.0
  detension_max: 0.5
  rhythm_2ag:  { amplitude: 0.3, peak_hour: 13.5, debt_amplitude: 0.5, debt_delay_hours: 1.5 }
  rhythm_aea:  { amplitude: 0.15, peak_hour: 7.0 }
```

## Data and old files

- **Checkpoint:** `C`, the impulse buffer, `MeanC` and `Rho` grow from 10 to 12 entries. An
  older checkpoint loads with AEA and 2-AG at baseline (C 1, pending 0, C-bar 1, rho 1).
- **Tick journal:** new sessions get `c_AEA`, `c_2-AG` and the other per-substance columns,
  plus `m_cb1` and the brake gains. Reading an older journal for replay fills missing columns
  with baseline, so old sessions replay with flat eCB lines.
- **memory.bin, settings.json:** unchanged.

## UI

- **Colors:** one hue for the system, so the two read as a pair. AEA light lime `#D9F99D`
  (tone), 2-AG lime `#84CC16` (phasic). Every series is labeled, as with the other ten.
- **Chemistry, Receptors, Lab:** the two substances appear like the others. The Lab heatmap
  becomes 12 x 12. Holding AEA and 2-AG at baseline stands in for a CB1 block.
- **Effectors:** a CB1 row with the tone and the current release gains for NE, CORT and DA.
- **3D brain:** eCB has no source nucleus. The regions dense in CB1 (hippocampus, amygdala,
  caudate, putamen, cerebellum, frontal cortex) get a lime glow mixed into their current
  emissive color, proportional to positive cb1, and dim slightly below rest. No new meshes and
  no particles, so the 58 to 60 fps budget holds. Hover info on those regions lists AEA, 2-AG
  and the CB1 tone.

## Testing

Engine tests, headless, with fixed seeds (`EndocannabinoidTests`):

1. **Neutral at rest:** at baseline cb1 is 0, every gain is exactly 1, detension and P are
   unchanged, and T1, T6, T8 and the stability tests stay green.
2. **Stress ends sooner:** after the same threat, the CORT excess halves at least 20% sooner
   with eCB than with AEA and 2-AG held at baseline.
3. **Buffering:** with 2-AG held at 2.0 before a threat, the NE and CORT peaks are lower; with
   AEA held at 0.5, they are higher.
4. **Order after stress:** AEA dips below baseline, and 2-AG peaks after NE does.
5. **Sleep restriction:** over a simulated day after a short night, the 2-AG peak is higher and
   at least an hour later than after a full night.
6. **Tolerance:** 2-AG held at 1.4 brings its rho to about 0.8, and the brake weakens with it;
   it recovers after the hold ends.
7. **Extinction:** with cb1 above 0, an episode's emotional tag falls faster across dreams.
8. **Old files:** a 10-substance checkpoint and journal load, with eCB at baseline.

Real app, on a scratch `BRAINSIM_DATA`: a hostile message, then watch CORT, AEA and 2-AG in the
charts and the lime glow in 3D; the same with eCB held at baseline from the Lab.

## Sources

- Di S, Malcher-Lopes R, Halmos KC, Tasker JG (2003). Nongenomic glucocorticoid inhibition via
  endocannabinoid release in the hypothalamus. J Neurosci 23(12):4850-4857.
- Hill MN, McLaughlin RJ, Pan B, et al. (2011). Recruitment of prefrontal cortical
  endocannabinoid signaling by glucocorticoids contributes to termination of the stress
  response. J Neurosci 31(29):10506-10515. https://pmc.ncbi.nlm.nih.gov/articles/PMC3179266
- Hanlon EC, Tasali E, Leproult R, et al. (2016). Sleep restriction enhances the daily rhythm
  of circulating levels of endocannabinoid 2-arachidonoylglycerol. Sleep 39(3):653-664.
  https://pmc.ncbi.nlm.nih.gov/articles/PMC4763355
- Vaughn LK, Denning G, Stuhr KL, et al. (2010). Endocannabinoid signalling: has it got rhythm?
  Br J Pharmacol 160(3):530-543.
- Murillo-Rodriguez E, Blanco-Centurion C, Sanchez C, Piomelli D, Shiromani PJ (2003).
  Anandamide enhances extracellular levels of adenosine and induces sleep. Sleep 26(8):943-947.
  https://pubmed.ncbi.nlm.nih.gov/14746372/
- Wei D, Lee D, Cox CD, et al. (2015). Endocannabinoid signaling mediates oxytocin-driven
  social reward. PNAS 112(45):14084-14089.
- Marsicano G, Wotjak CT, Azad SC, et al. (2002). The endogenous cannabinoid system controls
  extinction of aversive memories. Nature 418:530-534.
- Hirvonen J, Goodwin RS, Li CT, et al. (2012). Reversible and regionally selective
  downregulation of brain cannabinoid CB1 receptors in chronic daily cannabis smokers.
  Mol Psychiatry 17:642-649.
- Oleson EB, Cheer JF (2012). A brain on cannabinoids: the role of dopamine release in reward
  seeking. Cold Spring Harb Perspect Med 2(8):a012229.
