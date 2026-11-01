# Contributing to Untrail

Thanks for helping. Bug reports, missed leftovers, false matches and pull requests are all welcome.

## Reporting a problem

- **Something was missed, or matched wrongly:** open a [leftover report](https://github.com/usmanmateen/untrail/issues/new?template=leftover_report.yml). Run `Untrail.exe --scan "<shortcut or exe>" --hide-user --out report.txt` and attach the report. `--hide-user` replaces your user folder with `%USERPROFILE%`.
- **A security problem:** follow [SECURITY.md](SECURITY.md). Please don't open a public issue.

## Building

Untrail is plain C# for .NET Framework 4.8, compiled with the `csc.exe` that ships with Windows. No SDK or Visual Studio is needed.

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

## Testing

```bash
powershell -ExecutionPolicy Bypass -File tests/integration.ps1 -Exe dist/Untrail.exe
```

The tests install throwaway fake apps and run real deletes against them. They stop before touching anything if a fixture path already exists. Some scenarios need admin rights; they're skipped locally without them and always run in CI.

**Never test against real installed apps or your own documents.** Add a fake-app scenario to `tests/integration.ps1` instead.

## Pull requests

1. Fork the repo and create a branch from `main`.
2. Keep each PR to one change. For new matching rules, add a test that proves the rule and a decoy that must *not* match.
3. Run the build and the integration tests.
4. Open the PR and fill in the template.

Every PR has to pass these checks before it can be merged:

| Check | What it does |
|---|---|
| Build and test | Builds Untrail and runs the integration suite on a clean Windows machine, admin scenarios included |
| Lint PowerShell | PSScriptAnalyzer on the build, test and tool scripts |
| Spelling | `typos` on code and docs |
| CodeQL | Static analysis of the C# code and the workflows |
| Review dependencies | Blocks newly added dependencies with known vulnerabilities |
| Audit workflows | `zizmor` checks the GitHub Actions workflows for injection and permission problems |

### The safety rules

Untrail's first rule is: when in doubt, leave it. Changes that make it delete more need to show why the extra items belong to the app. Changes that make it delete less are always welcome.

- A name match is never enough on its own for **Safe to remove**.
- Every delete goes through `Guard`, `AppScope` and the scan-time path check. Don't add a delete path around them.
- Links are never followed.

## Code style

Match the surrounding code: C# 5 syntax (the built-in compiler), short methods, and comments that explain *why*. `.editorconfig` holds the basics.

## Licence

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
