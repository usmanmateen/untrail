# Security policy

Untrail deletes files and registry entries, and can ask for admin rights. Security reports are taken seriously.

## Supported versions

Only the [latest release](https://github.com/usmanmateen/untrail/releases/latest) receives fixes.

## Reporting a vulnerability

Please **don't open a public issue**. Report it privately through
[GitHub's private vulnerability reporting](https://github.com/usmanmateen/untrail/security/advisories/new).

Include:

- what an attacker can do, for example delete files outside the app being removed, or run code as admin through the elevated helper;
- the steps to reproduce it, and the Untrail version;
- any proof of concept. Use throwaway files and folders, never real data.

You'll get a reply within 7 days. Once a fix is released, the advisory is published with credit to you, unless you'd rather stay anonymous.

## In scope

- Anything that makes Untrail delete, or offer as **Safe to remove**, something that doesn't belong to the app being removed.
- Bypasses of the delete-time checks: the allowlist, the app-identity check, link handling, or the scan-time path check.
- The elevated helper: its named pipe, the parent-process verification, and which items it accepts.
- The installer and the self-uninstall.
- The updater: anything that makes Untrail download or run an installer that isn't the one published with this repo's release, or skip the checksum check.

## Verifying downloads

Each release includes `SHA256SUMS.txt`, and from v1.1.2 the release files carry a signed build provenance attestation that proves they were built by this repository's release workflow:

```bash
gh attestation verify UntrailSetup.exe -R usmanmateen/untrail
```

The same attestation is attached to each release as `untrail-<version>.intoto.jsonl`, so you can also check it offline:

```bash
gh attestation verify UntrailSetup.exe -R usmanmateen/untrail --bundle untrail-v1.1.4.intoto.jsonl
```
