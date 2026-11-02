# ADR-000 — Overview tech stack (LOCKED)
Status: Accepted, Nov 2 2026.

- **Backend:** ASP.NET Core Minimal API (.NET 10) at `/workspace/dsp/dev-overview/src/Dsp.DevOverview.Api`. Uses `Azure.Messaging.ServiceBus` (+ `ServiceBusAdministrationClient`) and `Azure.Monitor.Query` (`LogsQueryClient`) for App Insights. Listens on `http://localhost:5090`.
- **Frontend:** Vite + React SPA at `/workspace/dsp/dev-overview/ui/app`. UI/UX owns presentation. The UI talks **only** to the overview API (`/api/...`). It never talks to SB, AI or the service repos directly.
- **Local Service Bus:** the shared-stack emulator (`/workspace/dsp/shared-stack`, namespace `sbemulatorns`, entities from `Config.json`).
- **Sample catalog:** mirrored from `local-dev/customerproject/seed-messages/Program.cs` into `dev-overview/catalog/customerproject.samples.json`. The service repo is never read at runtime.
- **Store:** JSON files under `dev-overview/data/` (registrations, flow positions). No DB in v1.
