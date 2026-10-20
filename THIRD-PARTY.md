# Third-party components

Every runtime dependency shipped in NX Hub release packages. Licences were checked from package
metadata on 2026-10-19. All are permissive and compatible with NX Hub's Apache-2.0 licence.

## Server (.NET)

| Component | Version | Licence | Use |
| --- | --- | --- | --- |
| .NET runtime and ASP.NET Core | 10.0.x | MIT | Runtime (bundled in self-contained packages) |
| Microsoft.AspNetCore.OpenApi | 10.0.13 | MIT | OpenAPI document generation |
| Microsoft.OpenApi | 2.12.x | MIT | OpenAPI object model |
| Microsoft.Data.Sqlite | 10.0.13 | MIT | SQLite access |
| SQLitePCLRaw (bundle_e_sqlite3, core, provider, lib) | 2.1.13 | Apache-2.0 | Native SQLite binding (bundles the public-domain SQLite library) |
| Scalar.AspNetCore | 2.17.14 | MIT | Interactive API reference at `/docs` (assets served locally) |

## Web UI (bundled into `wwwroot`)

| Component | Version | Licence |
| --- | --- | --- |
| react, react-dom | 19.3.1 | MIT |

## Build and test only (not shipped)

| Component | Licence |
| --- | --- |
| Vite, @vitejs/plugin-react, Prettier | MIT |
| TypeScript | Apache-2.0 |
| xUnit, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk, Microsoft.AspNetCore.Mvc.Testing, Microsoft.Extensions.TimeProvider.Testing | MIT |

## Interoperability (not included)

NX Hub communicates over the network with **sys-botbase** (GPL-3.0) or **sys-botbase-cpp**
(GPL-3.0) running on the user's Switch. Neither is included in or linked into NX Hub. See
[docs/LICENSING-RESEARCH.md](docs/LICENSING-RESEARCH.md).
