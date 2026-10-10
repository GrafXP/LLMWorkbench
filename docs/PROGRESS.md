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

The first implementation milestone is complete. The next milestone connects Qwen tools and the existing image runtime to the same graph and scheduler.
