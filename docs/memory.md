# ACT-R memory (Phase 7)

The declarative memory the spec leaves for "the next stage": an ACT-R module wired to the
neuromodulator through E4. It is what the agent keeps beyond the 20 messages that fit in
its context, and how well it keeps them depends on its chemistry.

Status: design approved in conversation on 2026-11-04, this spec awaiting review.

## Goals

- The agent remembers across conversations and sessions.
- What gets encoded, how noisy recall is, how hard recall is, and what sleep consolidates
  all follow the chemistry, through the four E4 channels the engine already computes.
- Memory changes behavior two ways: recalled facts go into the prompt, and familiar
  messages are less surprising (lower S).
- The spec's T7 becomes testable in reduced mode, with memory instead of LoRA.

## Non-goals

ACT-R retrieval latency, partial matching, procedural memory, moving facts into LoRA, and
editing memory from the Lab. Each can come later if it is needed.

## Chunks

Two kinds, both stored with an embedding (nomic-embed, 768 floats):

| Kind | Content | Used for |
|---|---|---|
| **Episode** | One turn: the user message and the reply | Dreams, NREM replay, familiarity |
| **Fact** | One short sentence the LLM extracted from an episode | Recall into the prompt, familiarity |

Every chunk keeps:
- `id`, `kind`, `text`, `embedding`, `created` (brain time);
- `presentations`: a list of `(time, weight)`; one is added on encoding, on every recall,
  and on every sleep replay;
- `archived`: out of recall, kept on disk.

Episodes also keep:
- `tag`, the emotional tag for dreams: `0.05 + (NE - 1)+ + (CORT - 1)+` at encoding (D-30);
- `salience`, the replay priority: `0.05 + (DA - 1)+ + (NE - 1)+` at encoding (spec: replay
  "in order of salience, after DA and NE at encoding").

Facts keep the `id` of the episode they came from.

## Activation

Standard ACT-R, with time in seconds of the brain clock (real or simulated), so a simulated
night consolidates and forgets like a real one.

- **Base level:** `B = ln( sum_j w_j * max(t - t_j, 0.05)^(-d) )`, `d = 0.5`.
  Fresh and often-presented chunks are strong; old ones fade but never reach zero.
- **Spreading** from the current message: `S = W * max(0, (cos - c0) / (1 - c0))`, where `cos`
  is the cosine similarity of the message and chunk embeddings, `W = 2.0`, `c0 = 0.7`.
  The embeddings carry nomic's `classification: ` prefix (the probes need it), which lifts
  every cosine: unrelated texts measured 0.54 to 0.68, related ones 0.77 to 0.87 (D-33).
- **Noise:** logistic with scale `s`, drawn per chunk per recall, from a seeded generator.
- **Activation:** `A = B + S + noise`. A chunk can be recalled when `A > tau`.

## E4: chemistry into memory

The four channels already exist in `config.yaml` (`memory_*`), computed every tick as m_k.
Each enters through a gain `g = 4` (ours), because m_k is small (about +-0.12 for a
substance at twice its baseline).

| Channel | Substances | Effect |
|---|---|---|
| `memory_encoding` | ACh, DA, NE (+); **CORT (-), added by us** | weight of the encoding presentation: `w = clip(1 + g m, 0.2, 3)` |
| `memory_noise` | NE (+) | `s = 0.25 * clip(1 + g m, 0.3, 3)` |
| `memory_retrieval_threshold` | CORT (+) | `tau = -3.0 + g m` |
| `memory_consolidation` | DA (+) | weight of a sleep replay: `rate * clip(1 + g m, 0, 3)` |

CORT at encoding is our addition: the spec puts CORT only on recall, but T7 expects a fact
learned under stress to be lost, and E3 already says chronic stress freezes plasticity.

## Flows

### Encoding, after every reply

1. The episode is stored at once. Its embedding is the user message's embedding, which the
   appraisal already computed. Its presentation weight comes from `memory_encoding` now.
2. Fact extraction runs in the background so the chat does not wait. LM Studio gets the
   exchange labeled by speaker, `User: <message>` and `Assistant: <the first 200 characters
   of the reply>`, and a short instruction at temperature 0.2, at most 150 tokens:
   "List the lasting facts in this exchange, one per line. Facts about the user or the
   world only. If there are none, write nothing."
3. Each line becomes a candidate: list marks and numbering stripped, kept if it is 3 to 200
   characters, has no question mark, and does not end in a colon. At most 5 per episode.
4. Candidates are embedded in one call. A candidate with cosine above 0.97 to an existing
   fact adds a presentation to that fact instead of creating a new one.
5. The result is applied on the engine loop at a tick boundary and written to the events
   journal, so replays stay exact.

### Recall, on every user message

1. Activation is computed for the facts that are not archived and that the message points
   at (`S > 0`). Retrieval is cue driven: without a cue, a fresh fact passes on base level
   alone, is recalled for every message, and each recall strengthens it again.
2. Up to `k = 4` facts above `tau`, highest first, go into the request as a "Things you
   remember:" block inside the one leading system message (chat templates like Qwen's
   reject a system message anywhere else, D-34). It is not added to the history, so recalled
   facts do not pile up in the context.
3. Each recalled fact gets a presentation: recall strengthens it.
4. **Familiarity:** `f = clip((A_max - tau) / 2, 0, 1)` over the facts and episodes the
   message points at, and the
   surprise from appraisal becomes `S * (1 - 0.5 f)`.

### Sleep replay, in NREM

Every 5 minutes of N3 or N2, the episode with the highest salience (the most recent on a
tie) is replayed: it and its facts get a presentation of weight
`0.5 * clip(1 + g m_consolidation, 0, 3)`, multiplied by 0.3 in N2 (the spec's "0.3x" rate).
A replayed episode's salience is multiplied by 0.7, so the others get a turn.

### Dreams

Dreams seed from the memory's episodes, weighted by tag, instead of the dreamer's own
fragment list. Emotional detensioning (-20% per seed) lowers the episode's tag. Fragments
in older checkpoints are converted to episodes without embeddings on load, which dreams can
still use.

### Archiving

Nothing is deleted. When there are more than 5,000 chunks, the weakest by base level are
archived: out of recall and familiarity, still saved.

## Persistence

`memory.bin` next to the checkpoint, written by a `BinaryWriter` after every turn and on
exit, through a temporary file and a rename. A file that fails to load is renamed to
`memory.bin.bad` and the agent starts with an empty memory. The JSON checkpoint does not
carry memory: 5,000 chunks with 768 floats each would make it tens of megabytes.

## UI

- **Memory panel** (new tab): facts sorted by activation, each with a bar against `tau`,
  when it was last recalled and how often; facts recalled for the last message are
  highlighted. Episodes below, with tag and salience. A header line with chunk counts,
  current `tau` and `s`, and the last familiarity. In replay of a saved session the panel
  shows facts learned and recalls from the events journal, without live activation bars.
- **Chat:** the appraisal line under a user message adds `M n`, the number of facts
  recalled; the tooltip lists them.
- **3D:** the hippocampus pulses on encoding and on recall, not only in dreams.

## Errors

| What fails | What happens |
|---|---|
| Fact extraction (LM Studio down, junk output) | The episode stays; an error event is logged; nothing in the chat |
| The embedding of a message | Recall is skipped for that message; the reply goes ahead |
| `memory.bin` does not load | Renamed to `.bad`, empty memory, an error event |

## Configuration

A new `memory:` section in `config.yaml` with every constant above (`d`, `W`, `c0`, `s0`,
`tau0`, `gain`, `k`, dedupe cosine, replay interval and weight, salience decay, archive
limit, extraction settings), each marked "ours" where the spec gives no value. CORT is
added to the `memory_encoding` channel.

## Testing

- **Equations:** base level decays as `t^-0.5`; repeated presentations raise it; spreading
  rises with similarity; noise is reproducible with a seed; recall respects `tau` and `k`.
- **E4 directions:** NE raises `s`, CORT raises `tau` and lowers `w`, DA raises the replay weight.
- **T7, ACT-R version:** the same fact told with DA high, with DA low, and with CORT high;
  after 25 more turns (out of the 20-message context) and back at baseline chemistry, it
  is recalled only in the first case. Fake LM Studio, embeddings built to known cosines.
- **Sleep:** after a night, replayed episodes have higher base level than the same memory
  without sleep.
- **Extraction:** junk output (no facts, questions, overlong lines, headings) is dropped;
  a near-duplicate strengthens the existing fact.
- **Persistence:** save and load round-trip bit for bit; a corrupt file is set aside.
- **Real model**, with `BRAINSIM_LMSTUDIO=1`: a fact told early is recalled 25 turns later.
