# Implementation progress

## Step 1 — typed core

- Separate application repository and UI-independent domain project.
- Typed ports, collection cardinality, stable node/port IDs, DAG validation, and required-input checks.
- Shared graph commands with validated transactions, isolated revisions, and undo/redo.
- Artifact lineage, immutable run contracts, job states, executor/store interfaces, and exclusive GPU leases.
- Tests cover rejected connections/cycles, immutable revisions, undo/redo, and cancelled GPU waiters.

## Step 2 — project persistence and execution

- SQLite project/revision/run/job/artifact storage, write transactions, and a project ownership lock.
- Saved execution snapshots and idempotent submission IDs; text artifacts are published as files.
- Asynchronous execution, queue pause/resume, cancellation, retry, upstream failure blocking, and human selection checkpoints.
- Reopening recovers interrupted jobs and retains completed work; resource leases never span human checkpoints.
- Tests cover restart/resume, snapshot isolation, duplicate submissions, published artifacts, and downstream failure recovery.

## Step 3 — resource telemetry

- Background sampling of physical RAM, system commit, total CPU, per-logical-core CPU, NVIDIA utilisation/VRAM, and supported temperature/power/clocks.
- Separate working-set/private-memory and normalised CPU readings for the workbench and external inference processes.
- Bounded timestamped histories and job markers, with unavailable metrics retained as missing values.
- Tests cover history bounds and actual Windows CPU/RAM sampling.

## Step 4 — native desktop shell

- WPF/AvalonDock workspace with graph, inspector, text preparation, results/selection, queue, and independent floatable RAM/CPU/GPU/VRAM/process panels.
- Canvas-space zoom/pan, curved typed connections, minimap navigation, node dragging, connection removal, and undo/redo.
- New/open/autosaved SQLite projects; execution stays attached to saved graph revisions.
- Live history charts, per-core meters, shared time windows, job-event markers, and selected-job interval highlights.
- Actual desktop smoke run reached a selection checkpoint, read all 12 CPU cores plus RAM/NVIDIA metrics, and completed all four jobs after selecting a favourite.
- Chat is explicitly disconnected; no inference runtime is started or stopped in this milestone.

## Step 5 — milestone verification and recovery

- Validate executor input/output cardinality, artifact types, lineage, and immutable output IDs before publication.
- Reject unsupported database schemas and prevent overwriting another project's metadata.
- Exercise active-job cancellation and retry with GPU lease release, failed telemetry-provider recovery, and project ownership conflicts.
- Repeatable verification script includes the actual desktop workflow, graph undo/reconnect/rejected links, supported telemetry, and floating/redocking a resource panel.
- Fixed synchronous completion during window closing by scheduling the final close after the closing event returns.
- Added launch, workflow, resource-panel, storage, and architecture documentation.

Validation: Release build has zero warnings/errors; all 15 automated tests pass. The off-screen desktop check verifies graph edits, selection execution, real RAM/CPU/NVIDIA samples, and floating/redocking RAM. Screenshots and machine-readable reports are retained under ignored `local-data/`.

The first implementation milestone is complete.

## Phase 2 — Qwen and image loop

- Added Generate Text, Variations, Prepare Image, Image Batch, and Branch from Selection nodes with typed collection ports. Shared round commands apply one validated graph transaction and support undo.
- Native streamed Qwen tool calls are assembled by index; arguments are validated, tool replies are matched, conversations persist, and command receipts prevent duplicate graph edits/submissions. Turns are bounded to eight completions/three argument failures and repair interrupted tool conversations. A schema-constrained action envelope is available through the text profile.
- Connected the installed Prism llama-server and sd-server native async image API. Workers use private ports, health/load deadlines, process exit checks, Windows kill-on-parent-exit ownership, measured memory admission, and one shared GPU lease. External processes are reported and preserved.
- Persisted each render's input specification, profile, resolved seed, settings, attempt, state, timestamps, and image identity. Publish PNGs only after dimensions/chunks/checksums validate. Retries retain successful items, and SQLite schema 1 projects migrate to schema 2.
- Prepared target-specific prompts with Qwen while retaining source text. Saved text-model/image-profile identities and file size/modification fingerprints; these fingerprints are lightweight provenance, not cryptographic hashes or a promise of bit-identical replay.
- Added streaming chat with saved tool activity, an image-round editor and matrix preview, model library refresh, image contact sheets/full-size opening, render-item queue, source-path highlighting, feedback, and next-round controls.
- Branch execution reuses explicitly chosen artifacts; it submits only the new reachable branch. Source revision identity remains attached to the execution projection. Earlier images, jobs, and conversations survive reopening.
- Queue pause and interactive chat yield at image boundaries without holding a GPU lease while waiting. Throughput preference lets the current image batch finish first. Selection checkpoints unload the managed model.

Local contract evidence: Prism build 10685 (`7dffb158d`), Qwen3.6-35B-A3B-MTP-UD-Q4_K_M weights, and sd.cpp `9ec5ceb`. Native streamed function calling plus assistant/tool/result continuation passed on the installed Qwen template. The first real round produced six 512×512 klein images and the second round produced six more from two saved selections; the project was closed/reopened between rounds. Images and machine-readable reports are retained under ignored `local-data/`.

Runtime findings: Windows can enumerate a just-exited process; external-worker detection now checks `HasExited`. The installed Qwen CUDA runtime repeatedly crashed on a specific next-round prompt. Changing expert placement or disabling MTP did not resolve it, and flash attention off alone also failed. A conservative combination (CPU experts, 16K context, f16 KV, flash attention off, `-b 512 -ub 1`, no MTP) completed those exact saved requests and the next image round. Prompt ingestion is slower. [Upstream's input-dependent Qwen CUDA report](https://github.com/ggml-org/llama.cpp/issues/26609) informed diagnostics; it does not establish the root cause of this local failure.

Automated coverage now includes the six-image/select-two/next-six lineage loop across restart, retrying only unfinished images with saved random seeds, partial-batch cancellation and GPU release, pausing at image boundaries, idempotent agent graph edits/submissions, invalid tool connections, streamed argument fragments/truncation, and releasing the chat GPU lease before queued generation.

Final validation: Release build has zero warnings/errors; all 24 automated tests pass, including schema 1 migration and stopping malformed tool sequences before a fourth mutation. The original desktop smoke check passes. A fresh live run with the final conservative profile completed native tools, six first-round images, reopening/selection, and six second-round images without retries (`local-data/phase2-live-20261010-154450`). Detailed timing and runtime logs are retained there. The phase 2 desktop check verifies saved chat/history, six real thumbnails, source lineage, comparison preview, and undo.

## Phase 2 follow-up — Qwen selection and reported chat failure

- Registered both installed text models, stock Qwen3.6 and Genesis/Hermes, with their manual launchers' settings. Genesis also includes its custom template and CPU vision projector. Added a chat dropdown that applies to chat, variations and prompt preparation; its choice persists per project. Selection is disabled during an active chat or GPU operation, and backend retries retain the original request profile.
- The user's latest exception log (`%TEMP%/LLMWorkbench-error.log`, 2026-10-10 14:16–14:17 UTC) and saved conversation identify `JsonNode` object indexing on the array returned by `list_models`. This stopped the agent and repeatedly threw from `ChatItem.Content`/WPF accessibility. Both now check the JSON shape; `get_jobs` arrays are handled as well. These latest logs do not show a Qwen CUDA crash. The earlier CUDA failures above are a separate observation.
- The earlier lower VRAM use came from the reduced context, disabled MTP and changed expert placement, not a memory cap. The restored profiles use 128K/q8/flash attention/MTP; measured total VRAM was about 7.15 GiB for stock Qwen and 6.52 GiB for Genesis, including desktop/other GPU use. The model admission estimate does not allocate or cap VRAM.
- Runtime logs now include UTC timestamps, launch arguments, stdout/stderr, lifecycle transitions and unexpected exit codes. Reader tasks drain before disposing a worker; concurrent desktop log writes are serialized.

Validation: 25 automated tests pass, including array-returning tools continuing the agent turn and saved text-model selection. Desktop checks cover selecting both models, displaying their labels, and array tool-result rendering. Both real models completed native array tool calls and matched continuation, constrained variations, and read-only replay of the user's saved chat (`local-data/qwen-manual-profiles`). Both workers unloaded cleanly.

A fresh stock-Qwen run with the restored manual profile completed the full six-image → reopen/select two → six-new-image loop without retries or runtime exits (`local-data/phase2-live-20261010-164650`, 16:46:50–16:52:42 local time). GPU leases and managed workers were released at both selection checkpoints. The earlier conservative profile is no longer the default.

## Phase 2 follow-up — native viewer and manual text edits

- Added a dockable/floatable image and text viewer. Images load at original resolution with cursor-centred wheel zoom, captured drag pan, Fit/100%/+/- controls, double-click fit, keyboard navigation and previous/next result controls. Images expose their actual rendered prompt and source text, with actions to edit either.
- Text drafts, generated texts, variations, and prepared image prompts are editable in the viewer. Draft edits stay synchronised with the inspector and survive navigation. Save, Ctrl+S, F5 and downstream execution commit pending edits; unfinished blank drafts are retained across closing/reopening.
- Manual result edits create immutable text/specification artifacts with lineage to their originals. The originating node retains the complete saved output set, including untouched siblings, for subsequent runs. Prepared-prompt editing changes only the prompt and preserves model/negative prompt/dimensions/settings/seeds. Overrides persist through project restart and can be cleared or undone/redone.
- Downstream execution skips generation of saved texts, includes dependent branches, and retains other ancestors required by joins. Full graph runs use the same saved output overrides. Historical runs and their original output identities stay intact; image retries and later selection branches retain manual-prompt lineage.
- Automated checks cover text revision persistence across restart, reuse of sibling outputs, full/downstream execution, exact edited prompts and preserved settings through image retries and next-round branching, and joined downstream dependencies with rejected invalid overrides.

Validation: Release build has zero warnings/errors; all 28 automated tests pass. The desktop text check edits the actual viewer, retains edits across node/result navigation, saves two candidates, verifies override undo/redo and unchanged historical outputs, and completes a downstream run using human text. The image desktop check reopens the completed live project, confirms 512×512 originals rather than 256-pixel thumbnails, exercises zoom anchoring/pan/100%/Fit, and floats/redocks the viewer. Screenshots and reports remain under ignored `local-data/`.

## Dark theme follow-up

- Shared dark templates now cover menus/context menus, submenus/checkmarks/separators, tooltips, and vertical/horizontal scrollbars with hover/drag states. Built-in text-editor menus use internal WPF subclasses; their open events apply the same menu resources.
- Release desktop build has zero warnings/errors. Visual checks cover graph menus, docking menus, native Cut/Copy/Paste, tooltips and editor scrollbars. The desktop image-workflow check passes; its capture comes from the updated Workbench executable and saved workflow.
