# Contributing to NX Hub

Thanks for helping! NX Hub aims to be clean, well-tested infrastructure, so a few ground rules
keep it that way.

## Before you start

- For anything bigger than a bug fix, open an issue first to agree the approach.
- Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). The layering (Protocol → Core → Server) and
  the "Core is game-agnostic" rule are deliberate.

## Licensing rules (important)

NX Hub is licensed under the Apache License 2.0 and must stay free of copyleft code. See [docs/LICENSING-RESEARCH.md](docs/LICENSING-RESEARCH.md).

- **Do not copy code** from sys-botbase (GPL-3.0), SysBot.NET (AGPL-3.0), PKHeX (GPL-3.0),
  LiveHeX or any of their forks, not even "small" snippets.
- Protocol behaviour may be studied and re-implemented. Describe it in your own words in
  `docs/PROTOCOL.md` with the upstream commit you read.
- New dependencies must be permissively licensed (MIT, BSD, Apache-2.0, ISC, zlib). Add them to
  `THIRD-PARTY.md` in the same PR.
- Contributions are accepted under the Apache License 2.0 (its section 5: anything you submit
  for inclusion is licensed on the same terms). No separate CLA is required.
- If you include MIT/BSD-licensed third-party code, keep its copyright notice and add it to
  `THIRD-PARTY.md` (and `NOTICE` if its licence requires attribution).

## Development setup

See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). In short:

```bash
(cd web && npm install && npm run build)
dotnet test NxHub.slnx
dotnet format NxHub.slnx --verify-no-changes
```

## Pull requests

- Keep PRs focused. Include tests for behaviour changes. Hardware-independent tests are expected
  (use `FakeSwitch` or the simulator).
- **Be explicit about hardware testing.** Say "tested on: OLED 19.0.1 / sys-botbase 2.5 / Pi 5"
  or "simulator only". Never imply hardware compatibility that was not tested.
- Never commit IP addresses, tokens, `data/` directories or personal configuration.
- Raw sys-botbase command strings belong only in `NxHub.Protocol`.
- Update docs (`docs/*.md`, `HANDOVER.md`) when behaviour or decisions change.

## Code style

- C#: `.editorconfig` is enforced in CI (`dotnet format`). Warnings are errors.
- TypeScript: strict mode, `npm run build` must pass.
- Prefer clear names and small types over comments. Comment the *why*, not the *what*.

## Reporting security issues

Please do not open public issues for vulnerabilities. See [SECURITY.md](SECURITY.md).
