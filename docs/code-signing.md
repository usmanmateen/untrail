# Code signing: maintainer setup

Untrail's releases are signed through the [SignPath Foundation](https://signpath.org) program for open-source projects. The public policy is at [usmanmateen.github.io/untrail/code-signing.html](https://usmanmateen.github.io/untrail/code-signing.html).

The release workflow (`.github/workflows/release.yml`) signs in two passes, because the installer carries a copy of the app:

1. `build.ps1 -Stage app` builds `Untrail.exe`, which is uploaded and signed.
2. `build.ps1 -Stage installer` builds `UntrailSetup.exe` around the signed app, which is uploaded and signed.
3. `build.ps1 -Stage package` makes the zips and `SHA256SUMS.txt` from the signed files.

The workflow checks both signatures, runs the integration tests, attests build provenance and publishes the release. Each signing request waits up to an hour for an approver.

Signing switches on when the repository variable `SIGNPATH_ORGANIZATION_ID` is set. Until then, releases are built unsigned.

## One-time setup

1. **Apply** at [signpath.org/apply](https://signpath.org/apply) with the repository URL. Make sure two-factor authentication is on for your GitHub account; SignPath requires it.
2. **Once approved**, in SignPath:
   - Create a project with the slug `untrail`, linked to this GitHub repository as a trusted build system (GitHub.com).
   - Create a release signing policy with the slug `release-signing`, using the SignPath Foundation certificate. Make yourself the approver.
   - Set the project's default artifact configuration to the one below. Each GitHub artifact is a zip holding one `.exe`.
   - Create an API token for a CI user with permission to submit to that signing policy.
3. **In this repository**, under Settings, then Secrets and variables, then Actions:
   - Secret `SIGNPATH_API_TOKEN`: the API token.
   - Variable `SIGNPATH_ORGANIZATION_ID`: your SignPath organization ID.
   - Variable `SIGNPATH_PROJECT_SLUG`: `untrail`.
   - Variable `SIGNPATH_POLICY_SLUG`: `release-signing`.
4. Tag the next release. Approve the two signing requests in SignPath when the workflow asks. The release page then serves signed files.

## Artifact configuration

```xml
<?xml version="1.0" encoding="utf-8"?>
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <zip-file>
    <pe-file path="*.exe" max-matches="1" product-name="Untrail">
      <authenticode-sign />
    </pe-file>
  </zip-file>
</artifact-configuration>
```

`product-name="Untrail"` is the metadata restriction SignPath Foundation asks for. `build.ps1` sets the product name of both executables to `Untrail`.

## After signing is live

- The website and README don't need changes. The policy page already carries the attribution line.
- SmartScreen reputation still builds per file, so signed releases warn less over time rather than immediately. Fewer, larger releases build reputation faster.
