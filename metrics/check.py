"""Run the complete portable adapter and metrics-stack checks; stop at first failure."""

import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJECT = "metrics/adapter/Adapter.csproj"
TESTS = "metrics/adapter/tests/AdapterTests.csproj"


def main():
    commands = (
        ("dotnet", "build", PROJECT, "--nologo", "-p:FrameworkFlavor=Carbon"),
        (
            "dotnet",
            "build",
            PROJECT,
            "--nologo",
            "-p:FrameworkFlavor=Oxide",
            "-p:BaseIntermediateOutputPath=obj/Oxide/",
        ),
        ("dotnet", "run", "--project", TESTS, "--nologo"),
        (
            "dotnet",
            "format",
            PROJECT,
            "--verify-no-changes",
            "--severity",
            "info",
            "--no-restore",
        ),
        (
            "dotnet",
            "format",
            TESTS,
            "--verify-no-changes",
            "--severity",
            "info",
            "--no-restore",
        ),
        ("uvx", "ruff", "check", "metrics", "--exclude", "private"),
        ("uvx", "ruff", "format", "--check", "metrics", "--exclude", "private"),
        (
            "bun",
            "x",
            "prettier@3.8.4",
            "--check",
            "metrics/grafana/dashboards/*.json",
            "metrics/compose.yaml",
            *(("README.md",) if (ROOT / "README.md").is_file() else ()),
        ),
    )
    for command in commands:
        executable = shutil.which(command[0])
        if executable is None:
            raise RuntimeError(f"Required check tool is missing: {command[0]}")
        print("CHECK:", " ".join(command), flush=True)
        subprocess.run([executable, *command[1:]], cwd=ROOT, check=True)
    print(
        "PASS: both framework builds, adapter tests, full analyzers and stack lint/format"
    )


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as error:
        sys.exit(error.returncode)
    except (OSError, RuntimeError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)
