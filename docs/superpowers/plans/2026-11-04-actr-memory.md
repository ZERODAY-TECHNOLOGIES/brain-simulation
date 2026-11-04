# ACT-R Memory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the agent an ACT-R declarative memory, driven by the chemistry through E4, that recalls facts into the prompt, lowers surprise for familiar messages, consolidates in NREM sleep and feeds the dreams.

**Architecture:** A new `BrainSim.Engine.Memory` namespace holds the chunk store and the ACT-R equations, a binary file format, and the LLM fact extractor. `Agent` calls it on every message (recall, familiarity) and after every reply (encode an episode, extract facts in the background and apply them at a tick boundary). The dreamer and the NREM replay read the same store. The WPF app gets a Memory panel.

**Tech Stack:** .NET 10, C#, xUnit v3 (Microsoft.Testing.Platform), WPF, LM Studio over HTTP. No new packages.

**Spec:** `docs/memory.md`

## Global Constraints

- No new NuGet dependencies.
- Code, comments, UI copy, commits in English; no emojis, no em dashes (`docs/conventions.md`).
- ACT-R time is seconds of the brain clock (`Brain.Now`), never ticks.
- Constants from the spec, in a new `memory:` section of `config.yaml`, marked "ours" where the spec gives none:
  `decay: 0.5`, `spreading: 2.0`, `cosine_floor: 0.5`, `noise: 0.25`, `threshold: -3.0`, `gain: 4`,
  `recall_count: 4`, `duplicate_cosine: 0.9`, `familiarity_damping: 0.5`, `replay_minutes: 5`,
  `replay_weight: 0.5`, `n2_replay_rate: 0.3`, `salience_decay: 0.7`, `archive_limit: 5000`,
  `extraction_temperature: 0.2`, `extraction_tokens: 150`, `facts_per_episode: 5`, `fact_min_chars: 3`, `fact_max_chars: 200`.
- E4 mappings, gain `g = 4`: `w = clip(1 + g*m_encoding, 0.2, 3)`, `s = 0.25*clip(1 + g*m_noise, 0.3, 3)`,
  `tau = -3.0 + g*m_retrieval_threshold`, replay weight `0.5 * clip(1 + g*m_consolidation, 0, 3)` (x0.3 in N2).
- `memory_encoding` channel gains `{ substance: CORT, emax: -0.5 }` in `config.yaml`.
- Extraction instruction, verbatim: `List the lasting facts in this exchange, one per line. Facts about the user or the world only. If there are none, write nothing.`
- Recall system message starts with `Things you remember:` followed by one `- fact` line per fact.
- The repository is public: no secrets in commits. UI runs during development use `BRAINSIM_DATA` pointing at the scratchpad.

## Review Focus

- **First message ever, empty memory:** recall returns nothing, familiarity is 0, no memory system message is sent. Test in Task 1 and Task 5.
- **LM Studio stops answering mid-session:** the message embedding fails; the reply must still be attempted without recall, and fact extraction failing must keep the episode. Test in Task 5.
- **The same fact restated with different wording or case:** strengthens the existing fact instead of adding a second one. Test in Task 4.
- **A Phase 6 checkpoint with dream fragments and no `memory.bin`:** fragments become episodes without embeddings; recall and familiarity skip them, dreams still use them. Test in Task 6.
- **Memory past the archive limit:** weakest chunks archive, nothing is deleted, archived chunks never recall. Test in Task 1.

---

### Task 1: Chunk store and ACT-R equations

**Files:**
- Create: `src/BrainSim.Engine/Memory/Chunk.cs`, `src/BrainSim.Engine/Memory/DeclarativeMemory.cs`
- Modify: `src/BrainSim.Engine/config.yaml` (new `memory:` section, CORT on `memory_encoding`), `src/BrainSim.Engine/Config/EngineConfig.cs` (`MemoryConfig` record, `EngineConfig.Memory`)
- Test: `tests/BrainSim.Engine.Tests/MemoryActivationTests.cs`

**Interfaces:**
- Produces:
  - `enum ChunkKind { Episode, Fact }`
  - `readonly record struct Presentation(DateTime Time, double Weight)`
  - `sealed class Chunk` with `int Id`, `ChunkKind Kind`, `string Text`, `float[]? Embedding`, `DateTime Created`, `List<Presentation> Presentations`, `double Tag`, `double Salience`, `int? EpisodeId`, `bool Archived`, `DateTime? LastRecalled`, `int Recalls`
  - `readonly record struct MemoryModulation(double Encoding, double Noise, double Threshold, double Consolidation)` with `static MemoryModulation From(Modulation m)` reading the four `memory_*` channels
  - `sealed record Recall(Chunk Chunk, double Activation)`
  - `sealed class DeclarativeMemory(MemoryConfig config, int seed = 11)` with:
    `IReadOnlyList<Chunk> Snapshot()` (copy under a lock, for the UI thread),
    `double EncodingWeight(MemoryModulation m)`, `double NoiseScale(MemoryModulation m)`, `double Threshold(MemoryModulation m)`,
    `double BaseLevel(Chunk c, DateTime now)`, `double Spreading(float[] query, Chunk c)`,
    `Chunk AddEpisode(string text, float[]? embedding, IReadOnlyList<double> c, MemoryModulation m, DateTime now)`,
    `IReadOnlyList<Recall> RecallFacts(float[] query, MemoryModulation m, DateTime now)`,
    `double Familiarity(float[] query, MemoryModulation m, DateTime now)`

- [ ] **Step 1: Write the failing tests** in `MemoryActivationTests.cs`:
  - `Base_level_decays_as_inverse_square_root`: one presentation of weight 1; `BaseLevel` at 100 s minus at 400 s equals `0.5 * ln(4)` within 1e-9.
  - `More_presentations_raise_base_level`: two presentations score higher than one at the same age.
  - `Spreading_is_zero_below_the_cosine_floor_and_two_at_identity`: unit vectors with cosine 0.4 give 0; identical vectors give 2.0.
  - `Encoding_weight_follows_dopamine_and_cortisol`: `EncodingWeight` with `Encoding = +0.12` is about 1.48; with `-0.12` about 0.52; clipped to [0.2, 3].
  - `Noise_rises_with_NE_and_threshold_with_CORT`: `NoiseScale(Noise = 0.12) > 0.25`, `Threshold(Threshold = 0.12)` is -2.52.
  - `Recall_returns_at_most_k_facts_above_threshold_highest_first_and_strengthens_them`: 6 relevant facts, recall returns 4 in descending activation, each recalled fact gains one presentation and `Recalls == 1`; episodes are never returned.
  - `Empty_memory_recalls_nothing_and_is_unfamiliar` (Review Focus): `RecallFacts` empty, `Familiarity` 0.
  - `Noise_is_reproducible_with_a_seed`: two memories with seed 11 return identical activations.
  - `Archive_keeps_chunks_but_drops_them_from_recall` (Review Focus): with `archive_limit = 10`, adding 15 facts leaves 15 chunks, 5 `Archived`, the weakest by base level, and none of them recalled.

- [ ] **Step 2: Run the tests to make sure they fail**
  Run: `dotnet test --project tests/BrainSim.Engine.Tests -- --filter-class "*MemoryActivationTests"`
  Expected: build failure, `DeclarativeMemory` does not exist.

- [ ] **Step 3: Implement** the types above. `BaseLevel = ln(sum w * max(age, 0.05)^-decay)`. `Spreading = spreading * max(0, (cos - floor) / (1 - floor))`, 0 when either embedding is null. Noise is logistic, `s * ln(u / (1 - u))` from a seeded `Random`. `Familiarity = clip((max noise-free A over non-archived chunks with embeddings - tau) / 2, 0, 1)`. `AddEpisode` sets `Tag = 0.05 + (NE-1)+ + (CORT-1)+` and `Salience = 0.05 + (DA-1)+ + (NE-1)+`. Archiving runs inside every add.

- [ ] **Step 4: Run the tests to make sure they pass**
  Run: same command. Expected: all pass; full suite still green with `dotnet test --solution BrainSim.slnx`.

- [ ] **Step 5: Commit** `feat(memory): add the ACT-R chunk store and activation equations`

### Task 2: NREM replay

**Files:**
- Modify: `src/BrainSim.Engine/Memory/DeclarativeMemory.cs`
- Test: `tests/BrainSim.Engine.Tests/MemorySleepTests.cs`

**Interfaces:**
- Consumes: Task 1 types; `Brain.Phase`, `Brain.Now`, `Brain.Modulation`.
- Produces: `void OnTick(Brain brain)` (call after every brain tick), `event Action<Chunk>? Replayed`.

- [ ] **Step 1: Write the failing tests:**
  - `A_night_of_sleep_strengthens_salient_episodes`: two memories with the same three episodes; one brain is forced asleep at 01:00 (ADO at its limit) and advanced 6 h with `OnTick` hooked to `Ticked`, the other stays awake for the same hours; the most salient episode's `BaseLevel` at 07:00 is higher in the slept memory.
  - `Replay_follows_salience_and_lowers_it`: the first replayed episode is the most salient; its salience becomes x0.7 after replay.
  - `N2_replays_at_a_third_of_the_weight`: a replay presentation in N2 weighs 0.3 times one in N3 under the same chemistry.
  - `Dopamine_strengthens_consolidation`: replay weight with `Consolidation = +0.12` is greater than with 0.

- [ ] **Step 2: Run them to make sure they fail.** Run: `dotnet test --project tests/BrainSim.Engine.Tests -- --filter-class "*MemorySleepTests"`. Expected: build failure.

- [ ] **Step 3: Implement `OnTick`:** while the phase is N2 or N3, every `replay_minutes` of brain time replay the highest-salience non-archived episode (most recent on a tie): add a presentation of `replay_weight * clip(1 + g*m_consolidation, 0, 3)`, times `n2_replay_rate` in N2, to it and to its facts; multiply its salience by `salience_decay`; raise `Replayed`.

- [ ] **Step 4: Run them to make sure they pass.** Expected: pass; full suite green.

- [ ] **Step 5: Commit** `feat(memory): consolidate salient episodes in NREM sleep`

### Task 3: memory.bin

**Files:**
- Create: `src/BrainSim.Engine/Memory/MemoryFile.cs`
- Test: `tests/BrainSim.Engine.Tests/MemoryFileTests.cs`

**Interfaces:**
- Produces: `static class MemoryFile` with `void Save(string path, IReadOnlyList<Chunk> chunks)` (write to `path + ".tmp"`, then move over `path`) and `IReadOnlyList<Chunk> Load(string path)` (empty list when the file is missing; on a corrupt file, rename it to `path + ".bad"`, return empty). `DeclarativeMemory` gains `void Load(IEnumerable<Chunk> chunks)`.

- [ ] **Step 1: Write the failing tests:**
  - `Chunks_round_trip_bit_for_bit`: episodes and facts with embeddings, null embedding, presentations, tag, salience, episode id, archived and recall fields survive `Save` then `Load` exactly.
  - `A_corrupt_file_is_set_aside`: garbage bytes at `path`; `Load` returns empty, `path + ".bad"` exists, `path` does not.
  - `A_missing_file_is_an_empty_memory`.

- [ ] **Step 2: Run them to make sure they fail.** Expected: build failure.

- [ ] **Step 3: Implement** with `BinaryWriter`, a `"MEM1"` header and a chunk count; an embedding is written as its length (0 for null) followed by the floats.

- [ ] **Step 4: Run them to make sure they pass.**

- [ ] **Step 5: Commit** `feat(memory): save and load memory.bin`

### Task 4: Fact extraction

**Files:**
- Create: `src/BrainSim.Engine/Memory/FactExtractor.cs`
- Modify: `src/BrainSim.Engine/Memory/DeclarativeMemory.cs`, `tests/BrainSim.Engine.Tests/FakeLmStudio.cs`
- Test: `tests/BrainSim.Engine.Tests/FactExtractorTests.cs`

**Interfaces:**
- Consumes: `LmStudioClient.StreamChatAsync`, `LmStudioClient.EmbedAsync`.
- Produces:
  - `static class FactExtractor` with `const string Instruction` (verbatim from Global Constraints), `IReadOnlyList<string> Parse(string output, MemoryConfig config)`, `Task<IReadOnlyList<string>> ExtractAsync(LmStudioClient llm, string episodeText, MemoryConfig config, CancellationToken ct)`.
  - `DeclarativeMemory.AddOrStrengthenFact(string text, float[] embedding, int episodeId, MemoryModulation m, DateTime now) -> Chunk`: cosine above `duplicate_cosine` to an existing fact adds a presentation to it and returns it.
  - `FakeLmStudio` gains `Func<string, float[]>? Embed` (one vector per input, all inputs answered) and `Func<JsonDocument, string>? Reply` (the whole chat reply for a request; extraction requests are recognized by `FactExtractor.Instruction` in the system message).

- [ ] **Step 1: Write the failing tests:**
  - `Parse_keeps_clean_facts_and_drops_junk`: output `"- The user has a black cat.\n2. Octopuses have three hearts\nWhat else?\nFacts:\n\n" + new string('x', 300)` gives exactly `["The user has a black cat.", "Octopuses have three hearts"]`.
  - `Parse_caps_at_facts_per_episode`: 8 valid lines give 5.
  - `Empty_output_gives_no_facts`.
  - `A_restated_fact_strengthens_the_existing_one` (Review Focus): two facts whose embeddings have cosine 0.95 end as one chunk with two presentations; cosine 0.8 gives two chunks.
  - `ExtractAsync_sends_the_instruction_at_low_temperature`: through `FakeLmStudio`, the request has `temperature` 0.2, `max_tokens` 150 and the instruction as the system message.

- [ ] **Step 2: Run them to make sure they fail.**

- [ ] **Step 3: Implement.** `Parse` strips leading `-`, `*`, `+` and `N.`/`N)`, trims, keeps a line when its length is within [`fact_min_chars`, `fact_max_chars`], it has no `?` and does not end with `:`. `ExtractAsync` sends `[System(Instruction), User(episodeText)]` and concatenates the stream.

- [ ] **Step 4: Run them to make sure they pass.**

- [ ] **Step 5: Commit** `feat(memory): extract facts from turns and merge restated ones`

### Task 5: Agent integration and T7

**Files:**
- Modify: `src/BrainSim.Engine/Agent.cs`
- Test: `tests/BrainSim.Engine.Tests/T7MemoryTests.cs`, `tests/BrainSim.Engine.Tests/AgentTests.cs` (signature change)

**Interfaces:**
- Consumes: Tasks 1, 2, 4.
- Produces:
  - `Agent.Memory` (`DeclarativeMemory`), `Agent.Extraction` (`Task?`, the latest background extraction, for tests).
  - `AgentReply` gains `IReadOnlyList<string> Recalled` and `double Familiarity`.
  - `SendAsync`'s `onAppraised` becomes `Action<AppraisalSignals, SamplingParameters, IReadOnlyList<string>>?` (the third argument is the recalled facts).
  - `event Action<IReadOnlyList<Chunk>>? FactsLearned` on `Agent`.

- [ ] **Step 1: Write the failing tests** in `T7MemoryTests.cs`, with a `FakeLmStudio` whose `Embed` maps the fact sentence and the later question about it to vectors with cosine 0.8, and everything else to orthogonal vectors, and whose `Reply` answers extraction requests with the fact:
  - `A_fact_told_with_high_dopamine_is_recalled_after_it_leaves_the_context`: three agents tell `"My sister's name is Ilinca."`; before that message, one gets DA held at 2.0, one DA held at 0.5, one CORT held at 2.0 (Lab `Hold`), released after the reply; then 25 filler turns one minute apart (out of the 20-message window) with chemistry back at baseline; then the question. Only the high-DA agent's request contains `Things you remember:` with the fact.
  - `The_first_message_sends_no_memory` (Review Focus): the very first request has no `Things you remember:` message and `Familiarity == 0`.
  - `Familiar_messages_are_less_surprising`: repeating an encoded message lowers `Signals.Surprise` compared with the same appraisal and zero familiarity (`S * (1 - 0.5 f)`).
  - `Losing_LM_Studio_keeps_the_conversation_going` (Review Focus): an embedding call that throws skips recall and the reply still streams; an extraction that throws leaves the episode in memory.

- [ ] **Step 2: Run them to make sure they fail.**

- [ ] **Step 3: Implement in `SendAsync`:** after the embedding, compute `MemoryModulation.From(Brain.Modulation)`, `Familiarity`, damp the surprise before `Appraiser.Release`, call `RecallFacts`, and insert the recall system message right after the main system prompt when there is at least one fact (never into `_history`). After the reply, `AddEpisode` with the message embedding and start `FactExtractor.ExtractAsync` plus one `EmbedAsync` for the facts on the thread pool; queue the result and apply it with `AddOrStrengthenFact` from a `Brain.Ticked` handler, then raise `FactsLearned`. An embedding failure sets the embedding to null for this turn and skips appraisal of novelty, familiarity and recall. Hook `Memory.OnTick` to `Brain.Ticked`.

- [ ] **Step 4: Run them to make sure they pass**, then fix `AgentTests` for the new `onAppraised` signature. Full suite green.

- [ ] **Step 5: Commit** `feat(memory): recall facts into the prompt and encode every turn`

### Task 6: Dreams from memory

**Files:**
- Modify: `src/BrainSim.Engine/Dreams/Dreamer.cs`, `src/BrainSim.Engine/Agent.cs`, `tests/BrainSim.Engine.Tests/T9DreamTests.cs`
- Test: `tests/BrainSim.Engine.Tests/T9DreamTests.cs`

**Interfaces:**
- Consumes: `DeclarativeMemory`, `Chunk.Tag`.
- Produces: `Dreamer(EngineConfig config, LmStudioClient llm, DeclarativeMemory memory)`; `Dreamer.Remember` and `Dreamer.Fragments` are removed; `DreamState` keeps `List<Fragment>? Fragments` only to migrate old checkpoints; `DeclarativeMemory.Import(IEnumerable<Fragment> fragments)` turns them into episodes without embeddings.

- [ ] **Step 1: Change the T9 tests** to seed memories with `agent.Memory.AddEpisode(text, null, chemistry, default, time)` and read tags from `agent.Memory.Snapshot()`. Add `An_old_checkpoint_keeps_its_dream_fragments` (Review Focus): restoring a `DreamState` with three fragments gives three episodes with null embeddings; a REM dream seeds from them; `RecallFacts` and `Familiarity` ignore them.

- [ ] **Step 2: Run them to make sure they fail.**

- [ ] **Step 3: Implement:** seeds are drawn from non-archived episodes weighted by `Tag`; detensioning multiplies the chosen episode's `Tag` by 0.8; `Restore` imports fragments when present.

- [ ] **Step 4: Run them to make sure they pass.** All three existing T9 tests still pass.

- [ ] **Step 5: Commit** `refactor(dreams): seed dreams from memory episodes`

### Task 7: Host wiring, persistence and events

**Files:**
- Modify: `src/BrainSim.Engine/Hosting/EngineHost.cs`, `src/BrainSim.Engine/Journal/EventLog.cs`
- Test: `tests/BrainSim.Engine.Tests/HostingTests.cs`

**Interfaces:**
- Consumes: Tasks 3, 5, 6.
- Produces: `EventKinds.Memory = "memory"`; `EngineHost.MemoryPath` (`memory.bin` in the data folder); `Appraised` becomes `Action<string, AppraisalSignals, SamplingParameters, IReadOnlyList<string>>`; the user event values gain `"M"` (recalled count); memory events: `"Learned: <fact>"`, `"Recalled <n> facts"` with the facts in the text, `"Replayed episode <id>"`.

- [ ] **Step 1: Extend `Host_answers_logs_journals_applies_the_lab_and_replays`:** after the reply and extraction, `memory.bin` exists after dispose and loads back the episode; the events include a `memory` event.

- [ ] **Step 2: Run it to make sure it fails.**

- [ ] **Step 3: Implement:** load `memory.bin` in `StartAsync` after the checkpoint (an empty memory plus an error event when the file was set aside); save it after every reply (after extraction is applied) and in `DisposeAsync`; log the events above.

- [ ] **Step 4: Run the suite.** All green.

- [ ] **Step 5: Commit** `feat(memory): persist memory and journal its events`

### Task 8: Memory panel, chat count, hippocampus

**Files:**
- Create: `src/BrainSim.App/Views/MemoryView.xaml`, `src/BrainSim.App/Views/MemoryView.xaml.cs`
- Modify: `src/BrainSim.App/MainViewModel.cs`, `src/BrainSim.App/MainWindow.xaml`, `src/BrainSim.App/MainWindow.xaml.cs` (panel list, `layout-v4.xml`), `src/BrainSim.App/Views/BrainView.xaml.cs`

**Interfaces:**
- Consumes: `DeclarativeMemory.Snapshot()`, `BaseLevel`, `Threshold`, `NoiseScale`, the events of Task 7.
- Produces: `MainViewModel.Facts`, `MainViewModel.Episodes` (`ObservableCollection` of display records), `MainViewModel.MemoryHeader` (counts, tau, s, last familiarity), `MainViewModel.MemoryPulse()` (1 right after an encode or recall, fading over 2 s).

- [ ] **Step 1: Implement the panel** as a new "Memory" tab beside Dreams: header line; facts sorted by activation with a `LevelBar` against tau, last recalled and recall count, recalled-for-last-message rows highlighted; episodes with tag and salience. Live data refreshes every 500 ms from `Snapshot()`; a saved session shows learned and recalled facts from its events. The chat appraisal line adds `M n`. The hippocampus emissive adds `MemoryPulse()`.

- [ ] **Step 2: Build.** Run: `dotnet build src/BrainSim.App`. Expected: success.

- [ ] **Step 3: Verify in the running app** with `BRAINSIM_DATA` set to a scratchpad folder and the UI Automation driver: tell a fact, chat 3 more turns, ask about it; the panel lists the fact, the chat line shows `M 1` or more, and the reply uses it. Check the maximized window and 1280 x 800.

- [ ] **Step 4: Commit** `feat(ui): add the memory panel and show recalls in the chat`

### Task 9: Real-model check and docs

**Files:**
- Create: `tests/BrainSim.Engine.Tests/MemoryScenarioTests.cs` (trait `LmStudio`, skipped without `BRAINSIM_LMSTUDIO=1`)
- Modify: `docs/README.md` (Phase 7 status), `docs/decisions.md` (D-33: memory design pointers, values that calibration changed), `docs/architecture.md` (memory in the loop and in Persistence)

- [ ] **Step 1: Write `A_fact_told_early_is_recalled_25_turns_later`** against the real LM Studio and probes: tell a distinctive fact, wait for extraction, 25 unrelated turns, ask; the request contains the fact under `Things you remember:`.

- [ ] **Step 2: Run it** with `BRAINSIM_LMSTUDIO=1 dotnet test --project tests/BrainSim.Engine.Tests -- --filter-class "*MemoryScenarioTests"`. Expected: pass. If calibration needs `threshold`, `spreading` or `cosine_floor` changed, change `config.yaml`, rerun the whole suite, and record the new values in D-33.

- [ ] **Step 3: Update the docs** and commit `docs: record Phase 7 and the memory calibration`.
