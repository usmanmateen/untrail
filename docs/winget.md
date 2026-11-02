# winget: maintainer notes

Untrail is published to the Windows Package Manager as `UsmanMateen.Untrail`:

```bash
winget install UsmanMateen.Untrail
```

The manifests live in [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) under `manifests/u/UsmanMateen/Untrail/`.

## Every release

The `winget` job in `.github/workflows/release.yml` runs after a release is published. It uses [komac](https://github.com/russellbanks/Komac), pinned to a version and checked against its SHA-256, to open a pull request at winget-pkgs with the new version's installer URL and hash. Microsoft's validation pipeline then installs and checks it before merging.

The job needs one repository secret, `WINGET_TOKEN`: a **classic** GitHub token with only the `public_repo` scope, so komac can push to your fork of winget-pkgs and open the pull request. Fine-grained tokens can't open pull requests there. Without the secret, the job skips itself.

## Installer facts winget relies on

- `UntrailSetup.exe /S` installs silently for the current user; no admin is needed.
- The install registers under `HKCU\...\Uninstall\Untrail` with DisplayName `Untrail`, Publisher `Untrail` and DisplayVersion matching the release.
- Uninstalling runs `Untrail.exe --uninstall-self --quiet`.

If any of these change, update the manifest's `InstallerSwitches`, `AppsAndFeaturesEntries` or `ProductCode` to match.
