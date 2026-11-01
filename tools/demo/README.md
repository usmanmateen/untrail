# Demo recording and screenshots

These scripts produce the README media from the real Untrail running on Windows. Nothing is mocked up: the
recording captures the screen while a script operates the actual app.

The demo uses a disposable fake app, **Northwind Notes** (about 2 GB of placeholder files). It comes with its own
small uninstaller window. The scripts create it, let Untrail remove it, then delete anything left. They stop
without touching anything if any demo path already exists.

## Animated GIF (`docs/untrail-demo.gif`)

The recording needs an **unlocked, interactive desktop at 100% display scaling**, with a primary screen of at
least 1280×720. A locked screen or a remote session without a visible desktop won't work.

1. Build: `powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.1.1`
2. Record (about a minute; don't touch the mouse or keyboard meanwhile):
   ```bash
   powershell -ExecutionPolicy Bypass -File tools/demo/record-demo.ps1 -Exe dist/Untrail.exe
   ```
   - The script shows a dark "desktop" strip holding the demo app's shortcut, with Untrail (in `--hide-user` mode) placed beside it.
   - It drags the real shortcut into Untrail, reviews the list, ticks *Delete permanently* and clicks **Uninstall & Clean**, then confirms.
   - The demo app's own uninstaller runs. Untrail then cleans up, re-scans and shows the results.
   - Before every click it checks that the window under the cursor belongs to Untrail or the demo, and aborts otherwise.
   - It prints the folder holding the recorded frames.
3. Build the GIF (needs Python with Pillow):
   ```bash
   python tools/demo/make_gif.py "<frames folder>" docs/untrail-demo.gif
   ```
   Frames keep their real timing. Unchanged stretches (waiting on a scan) are shortened to 1.4 s at most.
   Expect around 20–30 seconds at 1280×720, under 10 MB.
4. Watch the GIF before committing it. Then add it to the README under the introduction:
   ```markdown
   ## See Untrail in action

   ![Untrail uninstalling an application and cleaning up its leftover files](docs/untrail-demo.gif)
   ```

## Screenshots (`docs/screenshot.png`, `docs/results.png`)

```bash
powershell -ExecutionPolicy Bypass -File tools/demo/record-demo.ps1 -Exe dist/Untrail.exe -Screenshots docs/shots
```

This mode uses no mouse. It operates Untrail through Windows UI Automation and saves window images with
`PrintWindow`, so it also works when the screen is locked. It saves:

- `screenshot.png`: the scan;
- `waiting.png`: waiting for the uninstaller;
- `results.png`: the results;
- `after.png`: the window after the removal.
