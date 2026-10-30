#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""批量适配本机 ComfyUI 原生工作流到 TapRelay 手机控制台。

扫描 source_dir(本机 ComfyUI 的 user/default/workflows),排除垃圾目录,按内容指纹去重,
转换每个候选为 <name>.api.json + <name>.ui.json,写入 out_dir(默认本地数据目录)。

用法:
  python adapt_comfy_workflows.py --dry-run            # 只打印去重结果,不写文件
  python adapt_comfy_workflows.py --out OUTDIR [--apply]
"""
import argparse
import hashlib
import json
import os
import sys
import urllib.request
import urllib.error
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from comfy_native_to_api import (
    fetch_object_info, convert_api, derive_ui, flatten_subgraphs,
    format_diagnostics)

JUNK_DIRS = {"杂七杂八的工作流", "工作流备份，不得直接使用修改", "插件示例工作流"}


def node_signature(nd):
    """结构化节点指纹(忽略节点 id)。"""
    ctype = nd.get("type")
    linked_inputs = tuple(sorted(i.get("name") for i in nd.get("inputs", [])
                                 if i.get("link") is not None))
    widgets = tuple(str(w) for w in (nd.get("widgets_values") or []))
    return (ctype, linked_inputs, widgets)


def graph_fingerprint(nodes, links):
    node_sigs = sorted(node_signature(nd) for nd in nodes)
    # 边指纹: (源type, 源slot, 目标type, 目标slot)
    link_map = {lnk[0]: (lnk[1], lnk[2]) for lnk in links if len(lnk) >= 5}
    id_to_type = {nd["id"]: nd.get("type") for nd in nodes}
    edge_sigs = []
    for nd in nodes:
        for inp in nd.get("inputs", []):
            lk = inp.get("link")
            if lk is not None and lk in link_map:
                origin, slot = link_map[lk]
                edge_sigs.append((id_to_type.get(origin), slot, nd.get("type"), inp.get("name")))
    edge_sigs.sort()
    raw = json.dumps({"n": node_sigs, "e": edge_sigs}, ensure_ascii=False, sort_keys=True)
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()


def guess_category(filename):
    n = filename.lower()
    f = filename
    # 优先按具体模型/用途细分,再回退大类
    if "视频" in f or "video" in n or "vhs" in n or "插帧" in f:
        return "视频处理"
    if "放大" in f or "超分" in f or "upscale" in n or "seedvr" in n or "修复" in f or "高清" in f:
        return "图像放大"
    if "电商" in f or "产品" in f or "换背景" in f or "换装" in f or "服装" in f or "迁移" in f:
        return "电商与换装"
    if "抠图" in f or "去水印" in f or "解码" in f or "混淆" in f or "深度" in f or "解析" in f:
        return "图像处理"
    if "姿态" in f or "pose" in n or "白膜" in f or "转白模" in f or "yolo" in n:
        return "姿态与控制"
    if "lora" in n or "Lora" in f:
        return "LoRA 工具"
    if "文生图" in f or "t2i" in n or "txt2img" in n or "文生" in f or "生图" in f:
        return "Qwen 文生图"
    if "图生图" in f or "i2i" in n or "img2img" in n or "图生" in f:
        return "Qwen 图生图"
    if "编辑" in f or "edit" in n or "edit" in f:
        return "Qwen 图像编辑"
    if "flux" in n or "klein" in n or "zimage" in n or "z-image" in n or "zimage" in n:
        return "Flux / Z-Image"
    return "我的工作流"


def scan(root):
    candidates = []  # (relpath, abspath, fp, category)
    for dp, dirs, files in os.walk(root):
        rel = os.path.relpath(dp, root)
        top = rel.split(os.sep)[0] if rel != "." else ""
        if top in JUNK_DIRS:
            dirs[:] = []
            continue
        for f in files:
            if not f.lower().endswith(".json") or f == ".index.json":
                continue
            ap = os.path.join(dp, f)
            try:
                d = json.load(open(ap, encoding="utf-8"))
            except Exception:
                continue
            if "nodes" not in d or "links" not in d:
                continue
            # 子图实例在指纹与去重前先内联,避免不同子图内容被误判为重复。
            flat = flatten_subgraphs(d)
            graph = flat if flat is not None else d
            nodes = graph.get("nodes", [])
            # 空壳图:无任何可执行节点(纯 Note/Reroute 等前端节点),
            # 转换后 api 图为空,手机端不可用,直接跳过。
            if not nodes:
                continue
            fp = graph_fingerprint(nodes, graph.get("links", []))
            candidates.append((rel, ap, fp, guess_category(f)))
    return candidates


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", default=r"W:\AIphotograph\ComfyUI_windows_portable\ComfyUI\user\default\workflows")
    ap.add_argument("--out", default=os.path.join(os.environ.get("LOCALAPPDATA", ""),
                                                   "TapRelay", "ComfyUIConsole", "api_workflows"))
    ap.add_argument("--base-url", default="http://127.0.0.1:8188")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args()

    print("扫描: %s" % args.source)
    cands = scan(args.source)
    print("候选工作流: %d" % len(cands))

    # 按指纹去重,保留第一个(按文件名排序稳定)
    cands.sort(key=lambda c: c[1])
    by_fp = {}
    for rel, ap_, fp, cat in cands:
        by_fp.setdefault(fp, []).append((rel, ap_, cat))
    print("去重后唯一工作流: %d" % len(by_fp))

    if args.dry_run or not args.apply:
        for fp, group in sorted(by_fp.items(), key=lambda kv: kv[1][0][0]):
            rel, src, cat = group[0]
            fname = os.path.basename(src)
            dups = [os.path.basename(g[1]) for g in group[1:]]
            print("◆ %s  [分类:%s]%s" % (fname, cat,
                                          ("  重复:%s" % ", ".join(dups)) if dups else ""))
        print("\n(dry-run 完成,未写入文件。加 --apply 执行。)")
        return

    # apply
    try:
        object_info = fetch_object_info(args.base_url)
    except urllib.error.URLError as e:
        print("无法获取 object_info: %s" % e, file=sys.stderr)
        sys.exit(1)
    os.makedirs(args.out, exist_ok=True)

    order = 50
    written = 0
    skipped_empty = 0
    warned = 0
    runnable = 0
    unresolved_total = 0
    suspicious_total = 0
    reason_counts = Counter()
    reason_files = defaultdict(set)
    plugin_refs = Counter()
    unsupported_total = Counter()
    unsupported_files = defaultdict(set)
    for fp, group in by_fp.items():
        rel, src, cat = group[0]
        base = os.path.splitext(os.path.basename(src))[0]
        api, titles, diag = convert_api(json.load(open(src, encoding="utf-8")), object_info)
        # 空壳图:转换后无可执行节点,手机端不可用,跳过并清理可能残留的旧文件。
        if not api:
            out_api = os.path.join(args.out, base + ".api.json")
            out_ui = os.path.join(args.out, base + ".ui.json")
            for old in (out_api, out_ui):
                if os.path.exists(old):
                    os.remove(old)
            skipped_empty += 1
            print("⊘ %s  (空壳图,跳过)" % base)
            continue
        ui = derive_ui(api, titles, cat, order, src)
        order += 1
        out_api = os.path.join(args.out, base + ".api.json")
        out_ui = os.path.join(args.out, base + ".ui.json")
        with open(out_api, "w", encoding="utf-8") as f:
            json.dump(api, f, ensure_ascii=False, indent=2)
        with open(out_ui, "w", encoding="utf-8") as f:
            json.dump(ui, f, ensure_ascii=False, indent=2)
        written += 1
        print("✓ %s  (nodes=%d, fields=%d)" % (base, len(api), len(ui["fields"])))
        # 自动诊断:新增节点/插件版本变化导致无法自动适配时,这里必须报警而不是静默产出坏图。
        summary = format_diagnostics(diag)
        unresolved = diag.get("unresolved") or []
        suspicious = diag.get("suspicious") or []
        if not unresolved and not suspicious:
            runnable += 1
        for _, _, _, reason in unresolved:
            key = reason.split(":", 1)[0]
            reason_counts[key] += 1
            reason_files[key].add(base)
            if key == "plugin-missing":
                plugin_refs[reason.split(":", 1)[1]] += 1
        for ctype, count in (diag.get("unsupported") or {}).items():
            unsupported_total[ctype] += count
            unsupported_files[ctype].add(base)
        if summary:
            warned += 1
            unresolved_total += len(unresolved)
            suspicious_total += len(suspicious)
            print("  ⚠ %s" % summary)
    print("\n写入 %d 个工作流到 %s (跳过空壳 %d, 诊断告警 %d)"
          % (written, args.out, skipped_empty, warned))
    print("可直接运行(无未解析输入/可疑值): %d / %d" % (runnable, written))
    print("诊断合计: 未解析输入 %d, 可疑值 %d" % (unresolved_total, suspicious_total))
    if reason_counts:
        labels = {
            "plugin-missing": "插件/节点未加载",
            "no-link": "源工作流未连线",
            "bypass-no-match": "旁路节点无同类输入可透传",
            "broken-link": "链接表断链",
            "missing-source-node": "链接指向不存在的节点",
            "muted-src": "上游节点被静音",
            "unknown-virtual": "未知前端虚拟节点",
            "get-missing-variable": "GetNode 变量无对应 SetNode",
            "get-slot-out-of-range": "GetNode 槽位越界",
            "no-native-input": "节点升级新增输入未保存",
        }
        print("未解析输入归因:")
        for key, count in reason_counts.most_common():
            label = labels.get(key, key)
            print("  %s: %d 条, 涉及 %d 个工作流"
                  % (label, count, len(reason_files[key])))
        if plugin_refs:
            top = ", ".join("%s×%d" % (t, c) for t, c in plugin_refs.most_common(8))
            print("  未加载节点类型(按影响条数): %s%s"
                  % (top, " ..." if len(plugin_refs) > 8 else ""))
    if unsupported_total:
        print("未加载节点类型 %d 类 / %d 个实例:"
              % (len(unsupported_total), sum(unsupported_total.values())))
        for ctype, count in unsupported_total.most_common(12):
            print("  %s ×%d (%d 个工作流)"
                  % (ctype, count, len(unsupported_files[ctype])))
        if len(unsupported_total) > 12:
            print("  ...")


if __name__ == "__main__":
    main()
