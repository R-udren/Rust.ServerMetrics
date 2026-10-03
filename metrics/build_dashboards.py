"""Generate plugin impact panels and attach bounded event annotations to local dashboards."""

import copy
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent / "grafana/dashboards"
DATASOURCE = {"type": "influxdb", "uid": "$datasource"}
SERVER = '"server" =~ /^${server:regex}$/'
PLUGIN = '"plugin" =~ /^${plugin:regex}$/ AND "framework" =~ /^${framework:regex}$/'
SCOPE = f"({SERVER}) AND ({PLUGIN})"
OTHER_LOG_FILTER = '"severity" =~ /^${other_logs:raw}$/'
ANNOTATION = {
    "name": "Server events",
    "enable": True,
    "hide": False,
    "iconColor": "#FFB357",
    "datasource": DATASOURCE,
    "target": {
        "refId": "Anno",
        "rawQuery": True,
        "fromAnnotations": True,
        "query": f'SELECT "title", "text", "tags" FROM "server_events" WHERE ({SERVER}) AND $timeFilter ORDER BY time DESC LIMIT 1000',
        "titleColumn": "title",
        "textColumn": "text",
        "tagsColumn": "tags",
    },
}


LOG_ANNOTATION = {
    "name": "Warnings & errors",
    "enable": True,
    "hide": False,
    "iconColor": "#FF637D",
    "datasource": DATASOURCE,
    "target": {
        "refId": "LogAnno",
        "rawQuery": True,
        "fromAnnotations": True,
        "query": f'SELECT "title", "text", "severity" FROM "server_logs" WHERE ({SERVER}) AND "severity" =~ /^(warning|error|exception|assert)$/ AND $timeFilter ORDER BY time DESC LIMIT 300',
        "titleColumn": "title",
        "textColumn": "text",
        "tagsColumn": "severity",
    },
}

OTHER_LOG_ANNOTATION = copy.deepcopy(LOG_ANNOTATION)
OTHER_LOG_ANNOTATION.update(name="Other server logs", enable=False, iconColor="#73BF69")
OTHER_LOG_ANNOTATION["target"]["refId"] = "OtherLogAnno"
OTHER_LOG_ANNOTATION["target"]["query"] = (
    f'SELECT "title", "text", "severity" FROM "server_logs" WHERE ({SERVER}) AND "severity" = \'info\' AND $timeFilter ORDER BY time DESC LIMIT 300'
)


def target(query, table=False, alias="$tag_plugin · $tag_framework"):
    return {
        "refId": "A",
        "datasource": DATASOURCE,
        "rawQuery": True,
        "query": query,
        "resultFormat": "table" if table else "time_series",
        "alias": alias,
    }


def panel(identifier, title, query, x, y, width=12, height=8, unit="none", table=False):
    return {
        "id": identifier,
        "title": title,
        "type": "table" if table else "timeseries",
        "datasource": DATASOURCE,
        "gridPos": {"x": x, "y": y, "w": width, "h": height},
        "targets": [target(query, table)],
        "fieldConfig": {
            "defaults": {
                "unit": unit,
                "decimals": 2,
                "noValue": "Unavailable",
                "custom": {} if table else {"spanNulls": False, "lineWidth": 2},
            },
            "overrides": [],
        },
        "options": {"showHeader": True, "cellHeight": "sm", "footer": {"show": False}}
        if table
        else {
            "tooltip": {"mode": "multi"},
            "legend": {
                "showLegend": True,
                "displayMode": "list",
                "placement": "bottom",
            },
        },
    }


def events(identifier, y):
    value = panel(
        identifier,
        "Server events · select an annotation to inspect nearby spikes",
        f'SELECT "event", "plugin", "text" FROM "server_events" WHERE ({SERVER}) AND $timeFilter ORDER BY time DESC LIMIT 300',
        0,
        y,
        24,
        8,
        table=True,
    )
    value["description"] = (
        "UTC event timestamps, displayed in your dashboard timezone. Save callbacks mark initiation, not completion. Exception events mark detection during a sampling interval. Coincidence with a spike does not establish causation. At most the newest 300 events are listed (1,000 graph annotations)."
    )
    value["fieldConfig"]["overrides"] = [
        {
            "matcher": {"id": "byName", "options": "Time"},
            "properties": [{"id": "unit", "value": "dateTimeAsIso"}],
        }
    ]
    return value


def build_logs(overview, freshness):
    logs = {
        key: copy.deepcopy(overview[key])
        for key in (
            "timezone",
            "schemaVersion",
            "refresh",
            "time",
            "timepicker",
            "templating",
        )
    }
    logs.update(
        {
            "id": None,
            "uid": "rust-server-logs",
            "title": "Rust Logs & Exceptions",
            "version": 1,
            "editable": True,
            "description": "Live Unity/framework logs and optional server log files with matching server performance. Routine logs are hidden by default.",
            "tags": ["rust", "logs", "exceptions"],
            "annotations": {"list": []},
            "panels": [],
            "links": [
                {
                    "title": "Server overview",
                    "type": "link",
                    "url": "/d/rust-local-overview",
                    "includeVars": True,
                    "keepTime": True,
                }
            ],
        }
    )
    logs["templating"]["list"].extend(
        [
            {
                "name": "other_logs",
                "label": "Show other logs",
                "type": "custom",
                "query": "Off : warning|error|exception|assert,On : .*",
                "current": {
                    "text": "Off",
                    "value": "warning|error|exception|assert",
                },
            },
            {
                "name": "severity",
                "label": "Severity",
                "type": "custom",
                "query": "warning,error,exception,assert,info",
                "includeAll": True,
                "allValue": ".*",
                "multi": True,
                "current": {"text": "All", "value": "$__all"},
            },
            {
                "name": "log_search",
                "label": "Contains text",
                "type": "textbox",
                "query": "",
                "current": {"text": "", "value": ""},
            },
        ]
    )
    for identifier, source_id, x in [(2, 6, 0), (3, 7, 12)]:
        value = copy.deepcopy(
            next(item for item in overview["panels"] if item["id"] == source_id)
        )
        value["id"] = identifier
        value["gridPos"] = {"x": x, "y": 0, "w": 12, "h": 7}
        logs["panels"].append(value)
    log_table = panel(
        4,
        "Server logs · newest 300 matching entries",
        f'SELECT "severity", "message" AS "Message", "origin" AS "Log stream", "source" AS "Source", "details_json" AS "Details" FROM "server_logs" WHERE ({SERVER}) AND ({OTHER_LOG_FILTER}) AND "severity" =~ /^${{severity:regex}}$/ AND "message" =~ /(?i)${{log_search:regex}}/ AND $timeFilter ORDER BY time DESC LIMIT 300',
        0,
        7,
        24,
        12,
        table=True,
    )
    log_table["fieldConfig"]["defaults"]["noValue"] = "—"
    log_table["fieldConfig"]["defaults"]["custom"] = {
        "cellOptions": {"type": "auto", "wrapText": False}
    }
    log_table["fieldConfig"]["overrides"] = [
        {
            "matcher": {"id": "byName", "options": "Time"},
            "properties": [{"id": "custom.width", "value": 170}],
        },
        {
            "matcher": {"id": "byName", "options": "severity"},
            "properties": [{"id": "custom.width", "value": 90}],
        },
        {
            "matcher": {"id": "byName", "options": "Source"},
            "properties": [{"id": "custom.width", "value": 220}],
        },
        {
            "matcher": {"id": "byName", "options": "Message"},
            "properties": [{"id": "custom.inspect", "value": True}],
        },
        {
            "matcher": {"id": "byName", "options": "Details"},
            "properties": [
                {"id": "custom.width", "value": 120},
                {"id": "custom.cellOptions", "value": {"type": "json-view"}},
                {"id": "custom.inspect", "value": True},
            ],
        },
    ]
    log_table["description"] = (
        "Warnings/errors by default; Show other logs includes captured routine output. Enable collection separately with metricsadapter.logs all on. Hover Details for cleaned frames. File entries use collection time and may have no severity metadata. Startup history is skipped; only configured files are tailed."
    )
    logs["panels"].append(log_table)
    value = panel(
        5,
        "Captured messages by severity · per minute",
        f'SELECT COUNT("message") FROM "server_logs" WHERE ({SERVER}) AND ({OTHER_LOG_FILTER}) AND $timeFilter GROUP BY time(1m), "severity" fill(null)',
        0,
        19,
        unit="short",
    )
    value["targets"][0]["alias"] = "$tag_severity"
    logs["panels"].append(value)
    logs["panels"].append(
        panel(
            6,
            "Log capture drops · cumulative this adapter instance",
            f'SELECT LAST("logs_dropped") FROM "adapter_health" WHERE ({SERVER}) AND $timeFilter GROUP BY time($__interval) fill(null)',
            12,
            19,
            unit="short",
        )
    )
    fresh_logs = copy.deepcopy(freshness)
    fresh_logs["id"] = 7
    fresh_logs["gridPos"] = {"x": 0, "y": 27, "w": 8, "h": 4}
    logs["panels"].append(fresh_logs)
    capture = panel(
        8,
        "Recent capture state · past minute",
        f'SELECT LAST("log_capture") AS "Warnings/errors", LAST("all_log_capture") AS "Other logs", LAST("logs_dropped") AS "Dropped warnings/errors", LAST("info_logs_dropped") AS "Dropped other logs", LAST("log_file_errors") AS "File read failures" FROM "adapter_health" WHERE ({SERVER}) AND time > now()-1m AND time <= now()',
        8,
        27,
        16,
        4,
        table=True,
    )
    capture["transformations"] = [
        {"id": "organize", "options": {"excludeByName": {"Time": True}}}
    ]
    logs["panels"].append(capture)
    return logs


def save(path, dashboard):
    path.write_text(
        json.dumps(dashboard, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )


def main():
    overview_path = ROOT / "server-overview.json"
    overview = json.loads(overview_path.read_text(encoding="utf-8"))
    dashboard = {
        key: copy.deepcopy(overview[key])
        for key in (
            "timezone",
            "schemaVersion",
            "refresh",
            "time",
            "timepicker",
            "templating",
        )
    }
    dashboard.update(
        {
            "id": None,
            "uid": "rust-plugin-impact",
            "title": "Rust Plugin Impact",
            "version": 1,
            "editable": True,
            "graphTooltip": 1,
            "tags": ["rust", "plugins", "carbon", "oxide"],
            "description": "Framework tracked elapsed time, limited by each framework's instrumentation. Includes event context and collector health; not total CPU or attributable FPS loss.",
            "annotations": {"list": [copy.deepcopy(ANNOTATION)]},
            "panels": [],
        }
    )
    variables = dashboard["templating"]["list"]
    variables.extend(
        [
            {
                "name": "framework",
                "label": "Framework",
                "type": "query",
                "datasource": DATASOURCE,
                "query": f'SHOW TAG VALUES FROM "plugin_runtime" WITH KEY = "framework" WHERE ({SERVER})',
                "refresh": 1,
                "includeAll": True,
                "allValue": ".*",
                "multi": False,
                "current": {"text": "All", "value": "$__all"},
            },
            {
                "name": "plugin",
                "label": "Plugin",
                "type": "query",
                "datasource": DATASOURCE,
                "query": f'SHOW TAG VALUES FROM "plugin_runtime" WITH KEY = "plugin" WHERE ({SERVER}) AND "framework" =~ /^${{framework:regex}}$/',
                "refresh": 1,
                "includeAll": True,
                "allValue": ".*",
                "multi": True,
                "current": {"text": "All", "value": "$__all"},
            },
        ]
    )
    dashboard["panels"].append(
        {
            "id": 1,
            "type": "text",
            "title": "What these numbers cover",
            "gridPos": {"x": 0, "y": 0, "w": 24, "h": 5},
            "options": {
                "mode": "markdown",
                "content": "**Carbon:** synchronous dispatched hook elapsed time; calls, framework lag spikes, and exceptions where counted. Timers, plugin commands, and background work are outside this timing counter.\n\n**Oxide:** framework tracked hooks, timers, web-request callbacks, and plugin commands. Hook counts / lag spikes / exceptions are **unavailable**, not zero.\n\nThese are **wall-clock elapsed times**, with framework-specific nesting behavior; they are not exact CPU time, allocation counts, all plugin work, or attributable FPS loss. Do not sum plugins into server CPU utilization. Harmony mods (including RChecker) are outside this adapter. First samples and counter resets have no rate. Core plugins and this adapter are excluded. Events provide context, not proof of causation.",
            },
        }
    )
    latest = panel(
        2,
        "Latest per-plugin readings · past minute",
        f'SELECT LAST("tracked_ms_per_second") AS "Tracked ms/s", LAST("calls_per_second") AS "Hook calls/s", LAST("has_calls") AS "Calls supported", LAST("has_exceptions") AS "Exceptions supported", LAST("interval_valid") AS "Interval valid" FROM "plugin_runtime" WHERE {SCOPE} AND time > now() - 1m AND time <= now() GROUP BY "plugin", "framework"',
        0,
        5,
        24,
        7,
        table=True,
    )
    latest["transformations"] = [
        {"id": "merge", "options": {}},
        {"id": "organize", "options": {"excludeByName": {"Time": True}}},
    ]
    dashboard["panels"].append(latest)
    for identifier, title, field, x, y, unit in [
        (
            3,
            "Tracked elapsed time per plugin · ms / second",
            "tracked_ms_per_second",
            0,
            12,
            "suffix:ms/s",
        ),
        (4, "Hook calls per second · Carbon only", "calls_per_second", 12, 12, "cps"),
        (
            5,
            "Hook exceptions per sample · Carbon only",
            "exceptions_delta",
            0,
            20,
            "short",
        ),
        (
            6,
            "Framework hook lag spikes per sample · Carbon only",
            "lag_spikes_delta",
            12,
            20,
            "short",
        ),
    ]:
        # Rates use means; event counts use sums, avoiding double differentiation of interval values.
        aggregate = "SUM" if field.endswith("_delta") else "MEAN"
        value = panel(
            identifier,
            title,
            f'SELECT {aggregate}("{field}") FROM "plugin_runtime" WHERE {SCOPE} AND $timeFilter GROUP BY time($__interval), "plugin", "framework" fill(null)',
            x,
            y,
            unit=unit,
        )
        value["description"] = (
            "Based on finite, reset-aware changes between native framework counters over the actual elapsed interval. Missing data and unsupported counters remain gaps. Carbon tracks hook dispatch; Oxide tracks framework-wrapped callbacks. This is elapsed time within that coverage, not total plugin CPU."
        )
        dashboard["panels"].append(value)
    dashboard["panels"].append(events(7, 28))
    for identifier, title, expression, x, unit in [
        (8, "Adapter polling elapsed time", 'MEAN("poll_ms")', 0, "ms"),
        (
            9,
            "Collector backlog / cumulative dropped points",
            'LAST("queued") AS "Queued", LAST("dropped") AS "Dropped total"',
            12,
            "short",
        ),
    ]:
        value = panel(
            identifier,
            title,
            f'SELECT {expression} FROM "adapter_health" WHERE ({SERVER}) AND $timeFilter GROUP BY time($__interval) fill(null)',
            x,
            36,
            unit=unit,
        )
        value["description"] = (
            "Collector work and bounded transport health are measured separately; the collector is excluded from per-plugin rankings. Dropped points are cumulative for this adapter instance and reset on reload."
        )
        dashboard["panels"].append(value)
    freshness = copy.deepcopy(overview["panels"][4])
    freshness.update(
        {
            "id": 10,
            "title": "Last adapter sample",
            "description": "Timestamp of the most recent adapter health sample. An old timestamp means this dashboard is not receiving current telemetry.",
            "gridPos": {"x": 0, "y": 44, "w": 8, "h": 4},
        }
    )
    freshness["targets"] = [
        target(
            f'SELECT "queued" FROM "adapter_health" WHERE ({SERVER}) AND time <= now() ORDER BY time DESC LIMIT 1',
            True,
        )
    ]
    dashboard["panels"].append(freshness)
    dashboard["links"] = [
        {
            "title": "Server overview",
            "type": "link",
            "url": "/d/rust-local-overview",
            "includeVars": True,
            "keepTime": True,
        }
    ]
    save(ROOT / "plugin-impact.json", dashboard)
    save(ROOT / "server-logs.json", build_logs(overview, freshness))
    for path in (
        overview_path,
        ROOT / "server-metrics.json",
        ROOT / "plugin-impact.json",
        ROOT / "server-logs.json",
    ):
        value = json.loads(path.read_text(encoding="utf-8"))
        annotations = value.setdefault("annotations", {}).setdefault("list", [])
        annotations[:] = [
            item
            for item in annotations
            if item.get("name")
            not in ("Server events", "Errors & exceptions", "Warnings & errors")
        ]
        annotations.append(copy.deepcopy(ANNOTATION))
        annotations.append(copy.deepcopy(LOG_ANNOTATION))
        annotations[:] = [
            item for item in annotations if item.get("name") != "Other server logs"
        ]
        annotations.append(copy.deepcopy(OTHER_LOG_ANNOTATION))
        links = value.setdefault("links", [])
        links[:] = [
            item
            for item in links
            if item.get("title") not in ("Plugin impact & events", "Logs & exceptions")
        ]
        links.append(
            {
                "title": "Plugin impact & events",
                "type": "link",
                "url": "/d/rust-plugin-impact",
                "includeVars": True,
                "keepTime": True,
            }
        )
        if path.name == "plugin-impact.json":
            links.pop()
        if path.name != "server-logs.json":
            links.append(
                {
                    "title": "Logs & exceptions",
                    "type": "link",
                    "url": "/d/rust-server-logs",
                    "includeVars": True,
                    "keepTime": True,
                }
            )
        if path == overview_path:
            value["panels"] = [item for item in value["panels"] if item["id"] != 14]
            value["panels"].append(events(14, 36))
            value["description"] = (
                "Recent native server readings, passive player ping, network traffic, event annotations, and trends. Plugin impact has a separate focused dashboard. Client FPS reports need a separately compatible collector."
            )
            for item in value["panels"]:
                if item["id"] in (10, 11, 12):
                    note = " The separate adapter does not collect this stream; historical data may be present. A gap is not zero traffic or zero latency."
                    item["description"] = item["description"].removesuffix(note)
                    if "client_fps" in json.dumps(item["targets"]):
                        item["description"] += note
        if path.name == "server-metrics.json":
            descriptions = {
                38: "Native invoke callback elapsed time in milliseconds over each collection interval. Update/fixed-update totals with the same callback name are combined. Failed callbacks and intervals spanning native counter resets may be absent.",
                41: "Native total elapsed milliseconds in each Update, LateUpdate, FixedUpdate and PhysicsUpdate phase over the game's report window. Phase totals replace the old per-method patches; these are not individual-frame durations.",
                42: "Native work queue cycle elapsed time in milliseconds over each collection interval, including queue bookkeeping. This differs from the old RunJob-only patches. First/reset intervals are omitted.",
                34: "Cumulative framework tracked hook time in seconds. Carbon covers dispatched hooks; Oxide covers framework-wrapped callbacks. Use Plugin impact for reset-aware interval rates and coverage flags.",
                44: "Derivative of cumulative framework tracked hook time. Plugin reloads can reset counters; use Plugin impact for reset-aware interval rates. Carbon covers dispatched hooks; Oxide covers framework-wrapped callbacks.",
                8: "Native process memory and managed heap occupancy in MiB. The legacy allocations field is managed heap occupancy, not a count of allocation events. GC pressure derives from cumulative generation-zero collections.",
                37: "Net change in entity population between graph buckets; this is not gross spawn/kill activity.",
            }
            deferred = (39, 40, 43, 36, 30, 45, 32)
            for item in value["panels"]:
                if item["id"] in descriptions:
                    item["description"] = descriptions[item["id"]]
                if item["id"] in deferred:
                    item["description"] = (
                        "Original panel and stored history preserved. Fresh data for this stream requires additional compatible instrumentation; the unsafe legacy collector remains disabled. A gap is unavailable data."
                    )
        text_panels = [item for item in value["panels"] if item.get("type") == "text"]
        for block in text_panels:
            value["description"] += " " + block["options"]["content"]
            for item in value["panels"]:
                if item["gridPos"]["y"] > block["gridPos"]["y"]:
                    item["gridPos"]["y"] -= block["gridPos"]["h"]
        value["panels"] = [
            item for item in value["panels"] if item.get("type") != "text"
        ]
        save(path, value)


if __name__ == "__main__":
    main()
