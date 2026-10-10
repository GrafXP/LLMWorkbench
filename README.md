# LLM Workbench v2

Native Windows workbench for text-first visual generation workflows. C#/.NET 10, WPF, a UI-independent core, and SQLite project storage.

Implementation is progressing through the durable core and visual-shell milestone. Qwen and media generation adapters follow in the next milestone.

See `docs/PLAN.md` for the agreed architecture and `docs/PROGRESS.md` for implementation steps and validation.

Build and test from this directory:

```powershell
dotnet build LLMWorkbench.slnx -c Release
dotnet test LLMWorkbench.slnx -c Release
```
