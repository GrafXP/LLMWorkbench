# Implementation progress

## Step 1 — typed core

- Separate application repository and UI-independent domain project.
- Typed ports, collection cardinality, stable node/port IDs, DAG validation, and required-input checks.
- Shared graph commands with validated transactions, isolated revisions, and undo/redo.
- Artifact lineage, immutable run contracts, job states, executor/store interfaces, and exclusive GPU leases.
- Tests cover rejected connections/cycles, immutable revisions, undo/redo, and cancelled GPU waiters.
