"""Start the local metrics stack and optionally install the Carbon/Oxide source plugin."""

import argparse
import secrets
import subprocess
import sys
from pathlib import Path

from verify import credentials

ROOT = Path(__file__).resolve().parent
PASSWORD_NAMES = (
    "INFLUXDB_ADMIN_PASSWORD",
    "INFLUXDB_WRITE_USER_PASSWORD",
    "INFLUXDB_READ_USER_PASSWORD",
    "GRAFANA_ADMIN_PASSWORD",
)


def create_credentials(path):
    if path.exists():
        return
    lines = ["# Local credentials. Do not commit or share this file."]
    lines.extend(f"{name}={secrets.token_urlsafe(32)}" for name in PASSWORD_NAMES)
    with path.open("x", encoding="utf-8") as output:
        output.write("\n".join(lines) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--server-dir", type=Path, default=ROOT.parents[1] / "rust-game-server/Server"
    )
    parser.add_argument(
        "--framework", choices=("auto", "carbon", "oxide"), default="auto"
    )
    parser.add_argument("--stack-only", action="store_true")
    args = parser.parse_args()
    if not args.stack_only and not (args.server_dir / "RustDedicated.exe").is_file():
        raise ValueError(
            "Use --server-dir to select a Rust server, or --stack-only for dashboards"
        )
    create_credentials(ROOT / ".env")
    credentials()
    subprocess.run(
        ["docker", "compose", "up", "-d", "--wait", "--wait-timeout", "180"],
        cwd=ROOT,
        check=True,
    )
    if not args.stack_only:
        subprocess.run(
            [
                sys.executable,
                "-B",
                str(ROOT / "install_adapter.py"),
                "--server-dir",
                str(args.server_dir.resolve()),
                "--framework",
                args.framework,
            ],
            cwd=ROOT,
            check=True,
        )
    print("Grafana: http://127.0.0.1:13001 (localadmin; password in metrics/.env).")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)
