"""Collect SSM patching evidence for Drata DCF-152 (Automated Security Updates).

Read-only. For every region with SSM maintenance windows it records:
  - each window, its schedule, targets and the patch operation it runs
  - window executions over the last N days and their status
  - per-instance patch state (last operation, missing / failed / critical counts)
  - managed instances with no patch state at all

It writes <out>/ssm-patching-<date>.json (raw) and .md (summary for the auditor),
and lists findings that would weaken the evidence.

Usage:
  python ssm_patching.py --profile saas-prod --out ./evidence
  python ssm_patching.py --regions us-east-1 eu-central-1 --days 30
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
from concurrent.futures import ThreadPoolExecutor

import boto3

PATCH_DOCS = {"AWS-RunPatchBaseline", "AWS-RunPatchBaselineWithHooks"}
STALE_DAYS = 14  # patch state older than this is a finding


def _paginate(client, op: str, key: str, **kw) -> list:
    return [x for page in client.get_paginator(op).paginate(**kw) for x in page.get(key, [])]


def _iso(v) -> str | None:
    return v.isoformat() if isinstance(v, dt.datetime) else v


def _run_command_params(task: dict) -> dict:
    """Run Command parameters, with keys normalised ('Reboot Option' -> 'RebootOption')."""
    raw = task.get("TaskInvocationParameters", {}).get("RunCommand", {}).get("Parameters", {}) or {}
    return {k.replace(" ", ""): v for k, v in raw.items()}


def collect_region(session: boto3.Session, region: str, days: int) -> dict | None:
    ssm = session.client("ssm", region_name=region)
    ec2 = session.client("ec2", region_name=region)
    since = (dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=days)).strftime("%Y-%m-%dT%H:%M:%SZ")

    windows = _paginate(ssm, "describe_maintenance_windows", "WindowIdentities")
    instances = _paginate(ssm, "describe_instance_information", "InstanceInformationList")
    if not windows and not instances:
        return None

    # EC2 tags, to match window targets to instances.
    ec2_ids = [i["InstanceId"] for i in instances if i["InstanceId"].startswith("i-")]
    tags: dict[str, dict] = {}
    for i in range(0, len(ec2_ids), 100):
        for r in ec2.describe_instances(InstanceIds=ec2_ids[i:i + 100])["Reservations"]:
            for inst in r["Instances"]:
                tags[inst["InstanceId"]] = {t["Key"]: t["Value"] for t in inst.get("Tags", [])}

    out_windows = []
    for w in windows:
        wid = w["WindowId"]
        targets = _paginate(ssm, "describe_maintenance_window_targets", "Targets", WindowId=wid)
        tasks = []
        for t in _paginate(ssm, "describe_maintenance_window_tasks", "Tasks", WindowId=wid):
            full = ssm.get_maintenance_window_task(WindowId=wid, WindowTaskId=t["WindowTaskId"])
            raw_keys = list((full.get("TaskInvocationParameters", {}).get("RunCommand", {}).get("Parameters") or {}).keys())
            tasks.append({
                "document": t.get("TaskArn"),
                "type": t.get("Type"),
                "parameters": _run_command_params(full),
                "parameter_keys_as_configured": raw_keys,
                "max_errors": full.get("MaxErrors"),
            })
        execs = _paginate(
            ssm, "describe_maintenance_window_executions", "WindowExecutions",
            WindowId=wid, Filters=[{"Key": "ExecutedAfter", "Values": [since]}],
        )
        # Instances whose tags match the window's tag targets.
        matched = set()
        for tg in targets:
            for cond in tg.get("Targets", []):
                if cond["Key"].startswith("tag:"):
                    k = cond["Key"][4:]
                    matched |= {iid for iid, tg_ in tags.items() if tg_.get(k) in cond["Values"]}
        out_windows.append({
            "id": wid,
            "name": w.get("Name"),
            "enabled": w.get("Enabled"),
            "schedule": w.get("Schedule"),
            "targets": [tg.get("Targets") for tg in targets],
            "matched_instances": sorted(matched),
            "tasks": tasks,
            "is_patch_window": any(t["document"] in PATCH_DOCS for t in tasks),
            "executions": [
                {"start": _iso(e.get("StartTime")), "end": _iso(e.get("EndTime")),
                 "status": e.get("Status"), "details": e.get("StatusDetails")}
                for e in execs
            ],
        })

    ids = [i["InstanceId"] for i in instances]
    states = []
    for i in range(0, len(ids), 50):
        states += _paginate(ssm, "describe_instance_patch_states", "InstancePatchStates", InstanceIds=ids[i:i + 50])
    by_id = {s["InstanceId"]: s for s in states}

    out_instances = []
    for i in instances:
        s = by_id.get(i["InstanceId"], {})
        out_instances.append({
            "id": i["InstanceId"],
            "name": tags.get(i["InstanceId"], {}).get("Name"),
            "platform": i.get("PlatformName"),
            "ping": i.get("PingStatus"),
            "patch_group": s.get("PatchGroup"),
            "baseline": s.get("BaselineId"),
            "last_operation": s.get("Operation"),
            "last_operation_end": _iso(s.get("OperationEndTime")),
            "installed": s.get("InstalledCount"),
            "missing": s.get("MissingCount"),
            "failed": s.get("FailedCount"),
            "pending_reboot": s.get("InstalledPendingRebootCount"),
            "critical_noncompliant": s.get("CriticalNonCompliantCount"),
            "security_noncompliant": s.get("SecurityNonCompliantCount"),
            "has_patch_state": bool(s),
        })

    return {"region": region, "windows": out_windows, "instances": out_instances}


def findings(regions: list[dict], now: dt.datetime) -> list[str]:
    f = []
    for r in regions:
        rg = r["region"]
        for w in r["windows"]:
            if not w["is_patch_window"] or not w["enabled"]:
                continue
            label = f"{rg} window '{w['name']}' ({w['id']})"
            failed = [e for e in w["executions"] if e["status"] != "SUCCESS"]
            if failed:
                f.append(f"{label}: {len(failed)} of {len(w['executions'])} executions not successful.")
            if not w["executions"]:
                f.append(f"{label}: no executions in the collection period.")
            if not w["matched_instances"]:
                f.append(f"{label}: target {w['targets']} matches no managed instance by tag.")
            for t in w["tasks"]:
                if any(" " in k for k in t["parameter_keys_as_configured"]):
                    f.append(f"{label}: task parameter names contain spaces {t['parameter_keys_as_configured']}.")
                if t["parameters"].get("Operation") != ["Install"]:
                    f.append(f"{label}: task operation is {t['parameters'].get('Operation')}, not Install.")
        for i in r["instances"]:
            label = f"{rg} {i['id']} ({i['name'] or i['platform']})"
            if not i["has_patch_state"]:
                f.append(f"{label}: no patch state reported.")
                continue
            end = i["last_operation_end"]
            if end and (now - dt.datetime.fromisoformat(end)).days > STALE_DAYS:
                f.append(f"{label}: last patch operation {end[:10]}, older than {STALE_DAYS} days.")
            for k in ("missing", "failed", "critical_noncompliant", "security_noncompliant"):
                if i[k]:
                    f.append(f"{label}: {k.replace('_', ' ')} = {i[k]}.")
    return f


def render_markdown(data: dict) -> str:
    L = [
        "# SSM Patching evidence",
        "",
        f"- Account: {data['account']}" + (f" ({data['account_alias']})" if data["account_alias"] else ""),
        f"- Collected: {data['collected_at']} UTC",
        f"- Period: last {data['days']} days",
        "- Control: DCF-152 Automated Security Updates",
        "",
        "## Patch windows",
        "",
        "| Region | Window | Schedule | Operation | Targets matched | Runs | Successful |",
        "| --- | --- | --- | --- | --- | --- | --- |",
    ]
    for r in data["regions"]:
        for w in r["windows"]:
            if not w["is_patch_window"]:
                continue
            op = ", ".join(sum((t["parameters"].get("Operation", []) for t in w["tasks"]), []))
            ok = sum(e["status"] == "SUCCESS" for e in w["executions"])
            sched = w["schedule"] + ("" if w["enabled"] else " (disabled)")
            L.append(f"| {r['region']} | {w['name']} | `{sched}` | {op} | {len(w['matched_instances'])} "
                     f"| {len(w['executions'])} | {ok} |")
    L += ["", "## Instance patch state", "",
          "| Region | Instance | Name | Platform | Last operation | Date | Missing | Failed | Critical |",
          "| --- | --- | --- | --- | --- | --- | --- | --- | --- |"]
    for r in data["regions"]:
        for i in r["instances"]:
            d = (i["last_operation_end"] or "")[:10]
            L.append(f"| {r['region']} | {i['id']} | {i['name'] or ''} | {i['platform'] or ''} "
                     f"| {i['last_operation'] or 'none'} | {d} | {i['missing'] if i['missing'] is not None else ''} "
                     f"| {i['failed'] if i['failed'] is not None else ''} "
                     f"| {i['critical_noncompliant'] if i['critical_noncompliant'] is not None else ''} |")
    L += ["", "## Findings", ""]
    L += [f"- {x}" for x in data["findings"]] or ["- None."]
    return "\n".join(L) + "\n"


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--profile")
    ap.add_argument("--regions", nargs="*", help="default: every enabled region")
    ap.add_argument("--days", type=int, default=45)
    ap.add_argument("--out", default="evidence")
    a = ap.parse_args()

    session = boto3.Session(profile_name=a.profile)
    account = session.client("sts").get_caller_identity()["Account"]
    aliases = session.client("iam").list_account_aliases().get("AccountAliases", [])
    regions = a.regions or [r["RegionName"] for r in session.client("ec2", region_name="us-east-1").describe_regions()["Regions"]]

    with ThreadPoolExecutor(max_workers=8) as pool:
        results = [r for r in pool.map(lambda rg: collect_region(session, rg, a.days), regions) if r]
    results.sort(key=lambda r: r["region"])

    now = dt.datetime.now(dt.timezone.utc)
    data = {
        "account": account,
        "account_alias": aliases[0] if aliases else None,
        "collected_at": now.strftime("%Y-%m-%d %H:%M"),
        "days": a.days,
        "regions": results,
    }
    data["findings"] = findings(results, now)

    out = pathlib.Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    stem = out / f"ssm-patching-{account}-{now:%Y%m%d}"
    stem.with_suffix(".json").write_text(json.dumps(data, indent=2, default=str))
    stem.with_suffix(".md").write_text(render_markdown(data))
    print(f"Wrote {stem}.json and {stem}.md ({len(data['findings'])} findings)")


if __name__ == "__main__":
    main()
