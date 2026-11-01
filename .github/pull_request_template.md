## What this changes

<!-- One or two sentences. Link the issue: Fixes #123 -->

## Why it's safe

<!-- For changes to scanning, matching or deleting: what stops this from touching something that isn't the app's? -->

## Checklist

- [ ] The build and integration tests pass locally (`build.ps1`, then `tests/integration.ps1`)
- [ ] New matching rules come with a test, and a decoy that must not match
- [ ] Nothing was tested against real installed apps or personal files
- [ ] README updated if behaviour changed
