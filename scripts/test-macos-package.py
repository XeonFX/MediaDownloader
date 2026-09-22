#!/usr/bin/env python3
"""Extract and launch the shipped archive using isolated data; exercise both host and tray startup."""
import argparse
import json
import os
from pathlib import Path
import plistlib
import subprocess
import sys
import tempfile
import time
import urllib.request


def check_launch(app: Path, scratch: Path, tray: bool) -> None:
    data = scratch / ("tray-data" if tray else "headless-data")
    log = scratch / ("tray.log" if tray else "headless.log")
    env = dict(os.environ, MD_DATA_DIRECTORY=str(data), MD_NO_TRAY="0" if tray else "1",
               ASPNETCORE_URLS="http://127.0.0.1:0", DOTNET_ENVIRONMENT="Production",
               ASPNETCORE_ENVIRONMENT="Production", Sentry__Dsn="")
    with log.open("w") as output:
        process = subprocess.Popen([str(app / "Contents/MacOS/MediaDownloader")],
                                   cwd="/", env=env, stdout=output, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 45
            endpoint = data / "endpoint.json"
            while not endpoint.exists():
                if process.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError("Packaged app failed to start")
                time.sleep(0.2)
            base = json.loads(endpoint.read_text())["baseUrl"]
            for route, expected in [("/health", b"Healthy"), ("/", b"MediaDownloader"),
                                    ("/settings", b"Start with macOS"),
                                    ("/_content/MudBlazor/MudBlazor.min.css", None),
                                    ("/_content/MudBlazor/MudBlazor.min.js", None),
                                    ("/_framework/blazor.web.js", None), ("/js/app.js", None)]:
                with urllib.request.urlopen(base + route, timeout=10) as response:
                    body = response.read()
                    assert response.status == 200 and body, f"Missing asset: {route}"
                    if expected:
                        assert expected in body, f"Unexpected response: {route}"
                    if route == "/settings":
                        assert b"Could not change login startup:" not in body, "Login startup status failed"
                        assert b"Available when running the installed macOS app." not in body, "Packaged login startup unavailable"
            # The host starts just before AppKit; allow native tray initialization to finish.
            time.sleep(2)
            assert process.poll() is None, "App exited after starting the host"
        except Exception:
            print(log.read_text(), file=sys.stderr)
            raise
        finally:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
    assert process.returncode == 0, f"Shutdown failed: {log.read_text()}"
    assert "Localization directory" not in log.read_text(), "Missing translations"
    print(f"Packaged app startup, assets and shutdown passed (tray={tray})")


def check_update_preparation(app: Path, scratch: Path) -> None:
    script = Path(__file__).resolve().parent.parent / "Resources/Updates/install-macos.sh"
    work = scratch / "update-test"
    work.mkdir()
    result = subprocess.run(["/bin/bash", str(script), "prepare", str(app), str(app), str(work)])
    assert result.returncode == 0, (work / "install.log").read_text()

    # Corrupt an existing sealed resource and ensure a second preparation refuses that copy.
    replacement = work / "Replacement.app"
    with (replacement / "Contents/Resources/AppIcon.icns").open("ab") as resource:
        resource.write(b"tampered")
    rejected = scratch / "rejected-update"
    rejected.mkdir()
    result = subprocess.run(["/bin/bash", str(script), "prepare", str(app), str(replacement), str(rejected)])
    assert result.returncode != 0, "Updater accepted a modified signed resource"
    print("Update preparation accepted valid signing and rejected a tampered replacement")


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("archive", type=Path, help="One ZIP matching the host architecture")
args = parser.parse_args()

with tempfile.TemporaryDirectory(prefix="md-package-test-") as temporary:
    scratch = Path(temporary)
    subprocess.run(["/usr/bin/ditto", "-x", "-k", str(args.archive.resolve()), str(scratch)], check=True)
    app = scratch / "MediaDownloader.app"
    with (app / "Contents/Info.plist").open("rb") as file:
        assert plistlib.load(file)["LSMinimumSystemVersion"] == "14.0"
    subprocess.run(["/usr/bin/codesign", "--verify", "--deep", "--strict", str(app)], check=True)
    check_update_preparation(app, scratch)
    check_launch(app, scratch, tray=False)
    check_launch(app, scratch, tray=True)
    subprocess.run(["/usr/bin/codesign", "--verify", "--deep", "--strict", str(app)], check=True)
