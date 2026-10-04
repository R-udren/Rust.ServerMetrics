"""Build the script-free installation ZIP from an explicit set of public files."""

import argparse
import re
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def build(output):
    source = ROOT / "adapter/ServerMetricsAdapter.cs"
    match = re.search(
        r'\[Info\("Server Metrics Adapter", "[^"]+", "([^"]+)"\)\]',
        source.read_text(encoding="utf-8"),
    )
    if not match or not re.fullmatch(r"\d+\.\d+\.\d+", match[1]):
        raise ValueError("Adapter version must contain three numeric components")
    files = {
        "LICENSE": ROOT.parent / "LICENSE",
        ".env.example": ROOT / ".env.example",
        "compose.yaml": ROOT / "compose.yaml",
        "adapter/ServerMetricsAdapter.cs": source,
        "influxdb/init/01-retention.iql": ROOT / "influxdb/init/01-retention.iql",
    }
    for name in (
        "grafana/dashboards/plugin-impact.json",
        "grafana/dashboards/server-logs.json",
        "grafana/dashboards/server-metrics.json",
        "grafana/dashboards/server-overview.json",
        "grafana/provisioning/dashboards/default.yaml",
        "grafana/provisioning/datasources/influxdb.yaml",
    ):
        files[name] = ROOT / name
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"Rust.ServerMetrics-{match[1]}.zip"
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as package:
        for name, path in sorted(files.items()):
            if not path.is_file():
                raise FileNotFoundError(path)
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            package.writestr(entry, path.read_bytes())
    return archive


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "private/releases")
    args = parser.parse_args()
    archive = build(args.output)
    print(f"Built {archive} ({archive.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
