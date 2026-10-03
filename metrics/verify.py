"""Verify the local metrics stack without third-party Python dependencies."""

import argparse
import base64
import json
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent
DATABASE = "rust-server-metrics"
INFLUX = "http://127.0.0.1:18086"
GRAFANA = "http://127.0.0.1:13001"


def credentials():
    values = {}
    for line in (ROOT / ".env").read_text(encoding="utf-8-sig").splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        name, separator, value = line.partition("=")
        if not separator or name in values or not value:
            raise ValueError("Invalid or duplicate credential entry in .env")
        values[name] = value
    for name in (
        "INFLUXDB_ADMIN_PASSWORD",
        "INFLUXDB_WRITE_USER_PASSWORD",
        "INFLUXDB_READ_USER_PASSWORD",
        "GRAFANA_ADMIN_PASSWORD",
    ):
        if not values.get(name):
            raise ValueError(f"Missing {name} in .env")
    return values


def request(url, user=None, password=None, data=None):
    headers = {}
    if user is not None:
        token = base64.b64encode(f"{user}:{password}".encode()).decode()
        headers["Authorization"] = f"Basic {token}"
    if isinstance(data, dict):
        data = urllib.parse.urlencode(data).encode()
        headers["Content-Type"] = "application/x-www-form-urlencoded"
    response = urllib.request.urlopen(
        urllib.request.Request(url, data=data, headers=headers), timeout=20
    )
    with response:
        raw = response.read()
        return json.loads(raw) if raw else None


def query(user, password, statement):
    result = request(
        f"{INFLUX}/query",
        user,
        password,
        {"db": DATABASE, "q": statement},
    )
    if result.get("error"):
        raise RuntimeError(result["error"])
    for item in result["results"]:
        if item.get("error"):
            raise RuntimeError(item["error"])
    return result["results"][0].get("series", [])


def expect_denied(action, name):
    try:
        action()
    except urllib.error.HTTPError as error:
        if error.code not in (401, 403):
            raise
        print(f"PASS: {name}")
        return
    except RuntimeError as error:
        if "not authorized" not in str(error).lower():
            raise
        print(f"PASS: {name}")
        return
    raise RuntimeError(f"Permission check failed: {name}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--require-metrics", action="store_true")
    args = parser.parse_args()
    values = credentials()
    admin = values["INFLUXDB_ADMIN_PASSWORD"]
    reader = values["INFLUXDB_READ_USER_PASSWORD"]
    writer = values["INFLUXDB_WRITE_USER_PASSWORD"]
    grafana = values["GRAFANA_ADMIN_PASSWORD"]

    retention = query(
        "metrics_admin", admin, f'SHOW RETENTION POLICIES ON "{DATABASE}"'
    )
    rows = [
        dict(zip(series["columns"], row))
        for series in retention
        for row in series["values"]
    ]
    if not any(
        row["name"] == "local_14d"
        and row["duration"] == "336h0m0s"
        and row["shardGroupDuration"] == "24h0m0s"
        and row["default"]
        for row in rows
    ):
        raise RuntimeError(
            "Missing expected default 14-day retention with 24-hour shards"
        )
    print("PASS: database retention")

    expect_denied(
        lambda: request(
            f"{INFLUX}/query?" + urllib.parse.urlencode({"q": "SHOW DATABASES"})
        ),
        "InfluxDB rejects anonymous queries",
    )
    expect_denied(
        lambda: query("rust_writer", writer, "SHOW MEASUREMENTS"),
        "Rust writer cannot read metrics",
    )
    expect_denied(
        lambda: request(
            f"{INFLUX}/write?db={DATABASE}",
            "grafana_reader",
            reader,
            b"permission_probe value=1",
        ),
        "Grafana reader cannot write metrics",
    )
    expect_denied(
        lambda: request(f"{GRAFANA}/api/datasources"),
        "Grafana rejects anonymous datasource access",
    )

    health = request(f"{GRAFANA}/api/health")
    if health["database"] != "ok":
        raise RuntimeError("Grafana database is unhealthy")
    datasource = request(
        f"{GRAFANA}/api/datasources/uid/rust-local-influxdb", "localadmin", grafana
    )
    if (
        datasource["url"] != "http://influxdb:8086"
        or datasource["user"] != "grafana_reader"
    ):
        raise RuntimeError(
            "Grafana datasource points at an unexpected endpoint or user"
        )
    datasource_health = request(
        f"{GRAFANA}/api/datasources/uid/rust-local-influxdb/health",
        "localadmin",
        grafana,
    )
    if datasource_health["status"] != "OK":
        raise RuntimeError("Grafana datasource health check failed")
    dashboard = request(
        f"{GRAFANA}/api/dashboards/uid/nvZajLJGk", "localadmin", grafana
    )["dashboard"]
    if len(dashboard["panels"]) != 27:
        raise RuntimeError("Expected the complete upstream dashboard")
    print("PASS: Grafana login, datasource, and all 27 dashboard panels/rows")

    overview = request(
        f"{GRAFANA}/api/dashboards/uid/rust-local-overview", "localadmin", grafana
    )["dashboard"]
    if overview["title"] != "Rust Server Overview" or not overview["panels"]:
        raise RuntimeError("Missing the server overview dashboard")
    if overview.get("refresh") != "10s":
        raise RuntimeError("The overview must refresh every 10 seconds")
    print("PASS: simpler server overview dashboard")
    impact = request(
        f"{GRAFANA}/api/dashboards/uid/rust-plugin-impact", "localadmin", grafana
    )["dashboard"]
    if impact.get("title") != "Rust Plugin Impact" or len(impact.get("panels", [])) < 9:
        raise RuntimeError("Missing plugin impact dashboard")
    logs = request(
        f"{GRAFANA}/api/dashboards/uid/rust-server-logs", "localadmin", grafana
    )["dashboard"]
    if logs.get("title") != "Rust Logs & Exceptions":
        raise RuntimeError("Missing logs and exceptions dashboard")
    for item in (dashboard, overview, impact, logs):
        if not any(
            annotation.get("name") == "Server events"
            and annotation.get("enable")
            and annotation.get("target", {}).get("textColumn") == "text"
            for annotation in item.get("annotations", {}).get("list", [])
        ):
            raise RuntimeError("Missing enabled timestamped server event annotations")
        if not any(
            annotation.get("name") == "Errors & exceptions" and annotation.get("enable")
            for annotation in item.get("annotations", {}).get("list", [])
        ):
            raise RuntimeError("Missing enabled error log annotations")
    print(
        "PASS: plugin/log dashboards and event/error annotations on all four dashboards"
    )

    proxy_query = urllib.parse.urlencode({"db": DATABASE, "q": "SHOW MEASUREMENTS"})
    proxy = request(
        f"{GRAFANA}/api/datasources/proxy/uid/rust-local-influxdb/query?{proxy_query}",
        "localadmin",
        grafana,
    )
    if any(result.get("error") for result in proxy["results"]):
        raise RuntimeError("Grafana cannot query InfluxDB")
    print("PASS: queries through Grafana's datasource proxy")

    server_tags = query("grafana_reader", reader, 'SHOW TAG VALUES WITH KEY = "server"')
    has_server = any(
        row[-1] == "local-dev" for series in server_tags for row in series["values"]
    )
    if not has_server:
        if args.require_metrics:
            raise RuntimeError(
                "No local-dev metrics yet; check Rust startup and metricsadapter.status"
            )
        print("WAITING: local-dev metrics (start the Rust server)")
        return
    measurements = query("grafana_reader", reader, "SHOW MEASUREMENTS")
    names = [row[0] for series in measurements for row in series["values"]]
    fresh = query(
        "grafana_reader",
        reader,
        'SELECT * FROM "framerate" WHERE "server" = \'local-dev\' AND time > now() - 2m ORDER BY time DESC LIMIT 1',
    )
    if not fresh or not any(series.get("values") for series in fresh):
        if args.require_metrics:
            raise RuntimeError("No fresh server samples in the last two minutes")
        print("WAITING: fresh local-dev metrics (start the Rust server)")
        return
    print(f"PASS: fresh local-dev server metrics; {len(names)} measurements")
    plugin_count = None
    for measurement in ("adapter_health", "plugin_runtime"):
        if measurement == "plugin_runtime" and plugin_count == 0:
            print("PASS: no user plugins loaded; plugin counters are unavailable")
            continue
        fresh = query(
            "grafana_reader",
            reader,
            f'SELECT * FROM "{measurement}" WHERE "server" = \'local-dev\' AND time > now() - 2m ORDER BY time DESC LIMIT 1',
        )
        if not fresh:
            if args.require_metrics:
                raise RuntimeError(
                    f"No fresh {measurement}; check metricsadapter.status"
                )
            print(
                f"WAITING: fresh {measurement} (adapter may be inactive or no user plugins loaded)"
            )
            return
        if measurement == "adapter_health":
            latest = dict(zip(fresh[0]["columns"], fresh[0]["values"][0]))
            plugin_count = latest.get("plugins")
            if (
                latest.get("observer_errors", 0)
                or latest.get("upload_failures", 0)
                or latest.get("dropped", 0)
            ):
                raise RuntimeError(
                    "Adapter reports observer/upload errors or dropped points; inspect metricsadapter.status"
                )
    if not query(
        "grafana_reader",
        reader,
        'SELECT * FROM "server_events" WHERE "server" = \'local-dev\' ORDER BY time DESC LIMIT 1',
    ):
        raise RuntimeError("No adapter event annotations stored")
    print(
        "PASS: fresh adapter/plugin readings, error-free collector health, stored event annotations"
    )
    for measurement in ("entities", "tasks", "network", "server_update"):
        if not query(
            "grafana_reader",
            reader,
            f'SELECT * FROM "{measurement}" WHERE "server" = \'local-dev\' AND time > now() - 2m ORDER BY time DESC LIMIT 1',
        ):
            raise RuntimeError(f"Missing fresh native {measurement} data")
    print("PASS: fresh detailed entity, task, network and native phase measurements")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, RuntimeError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)
