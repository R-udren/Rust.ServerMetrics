"""Install the same read-only metrics plugin source on Carbon or Oxide (hot-loadable)."""

import argparse
import json
import shutil
import sys
from datetime import datetime, timezone
from pathlib import Path

from verify import credentials

ROOT = Path(__file__).resolve().parent
NAME = "ServerMetricsAdapter"


def framework_directory(server, requested):
    available = []
    if (server / "carbon/managed/Carbon.Common.dll").is_file():
        available.append("carbon")
    if (server / "RustDedicated_Data/Managed/Oxide.Rust.dll").is_file():
        available.append("oxide")
    if requested == "auto":
        if "carbon" in available:
            return server / "carbon"
        if available == ["oxide"]:
            return server / "oxide"
        raise ValueError("Cannot detect Carbon/Oxide; use --framework explicitly")
    if requested not in available:
        raise ValueError(f"Missing installed {requested} framework assemblies")
    return server / requested


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--server-dir", type=Path, default=ROOT.parents[1] / "rust-game-server/Server"
    )
    parser.add_argument(
        "--framework", choices=("auto", "carbon", "oxide"), default="auto"
    )
    args = parser.parse_args()
    server = args.server_dir.resolve(strict=True)
    if not (server / "RustDedicated.exe").is_file():
        raise ValueError("Server directory must contain RustDedicated.exe")
    if (server / "HarmonyMods/RustServerMetrics.dll").exists():
        raise ValueError(
            "Remove the quarantined Harmony collector while Rust is stopped first"
        )
    framework = framework_directory(server, args.framework)
    values = credentials()
    configuration = {
        "Endpoint": "http://127.0.0.1:18086",
        "Database": "rust-server-metrics",
        "RetentionPolicy": "local_14d",
        "Username": "rust_writer",
        "Password": values["INFLUXDB_WRITE_USER_PASSWORD"],
        "Server": "local-dev",
        "SampleSeconds": 5,
        "QueueLimit": 1000,
        "CaptureLogs": True,
        "CaptureInfoLogs": False,
        "LogLimitPerMinute": 60,
        "NativeInvokeDetails": True,
        "NativePacketDetails": True,
    }
    config_folder = "configs" if framework.name == "carbon" else "config"
    config_path = framework / f"{config_folder}/{NAME}.json"
    plugin_path = framework / f"plugins/{NAME}.cs"
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    backup = ROOT / f"private/adapter-backups/{stamp}"
    for path in (config_path, plugin_path):
        if path.exists():
            backup.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, backup / path.name)
        path.parent.mkdir(parents=True, exist_ok=True)
    # Write config before plugin source so file-watcher compilation sees credentials.
    config_path.write_text(json.dumps(configuration, indent=2) + "\n", encoding="utf-8")
    shutil.copyfile(ROOT / f"adapter/{NAME}.cs", plugin_path)
    print(
        f"Installed {NAME} source/config for {framework.name}; credentials kept private."
    )
    print(
        "Framework watcher may hot-load it; otherwise use c.load / oxide.load ServerMetricsAdapter."
    )
    print("No Rust restart or Harmony DLL installation performed.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)
