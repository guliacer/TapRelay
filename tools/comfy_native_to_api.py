#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 ComfyUI 原生工作流(.json, 含 nodes/links/widgets_values)转换为 TapRelay 控制台所需的
API prompt 格式(<name>.api.json)及配套 <name>.ui.json。

转换必须还原 ComfyUI 前端的图语义,而不是只按节点类型做静态映射:
- Reroute / GetNode / SetNode: 前端中继与无线连线,沿真实来源解析。
- PrimitiveNode: 输出一个字面量值。
- GGOptionConfirm: 前端虚拟节点,按输出槽位输出 widgets_values 中的字面量。
- 静音(mode=2): 不进入 API 图,下游连线断开。
- 旁路(mode=4): 不进入 API 图,下游连线按类型重连到被旁路节点的输入。
- 其他未知虚拟节点: 仅当输出类型为通配符 `*` 时按输入直通,否则记为未解析。
- 不在 object_info 中、但仍作为真实数据源出现的节点(插件未加载)记为不支持。

控件槽位判定以原生节点自身序列化为准:
- 原生 inputs 中带 `widget` 标记 => 控件,占 1 槽;带 control_after_generate 额外占 1 槽。
- 原生 inputs 中存在但没有 widget 标记 => 连线输入,不占槽(即使未连线)。
- 原生 inputs 中没有该输入 => 按 object_info 类型判断;基本类型/COMBO/选项型为控件。
- 控件缺少存储值时用 object_info 默认值补齐,避免 API 图缺必需输入。

转换后自动产出诊断:
- unresolved: 必需输入无法解析(断链、静音、未知虚拟节点)。
- suspicious: 控件值类型与声明不符(槽位错位的直接证据)。
- unsupported: 依赖了本机未加载的节点类型。

用法:
  python comfy_native_to_api.py <native.json> [--base-url URL] [--category 分类]
输出两个文件到同目录(同基名 .api.json / .ui.json)。也可作为模块 import 使用。
"""
import copy
import json
import os
import sys
import urllib.request
import urllib.error
from collections import OrderedDict

# 常见英文输入名 -> 中文标签后缀
INPUT_LABELS = {
    "image": "上传原图", "prompt": "提示词", "value": "提示词", "text": "文本",
    "negative_prompt": "反向提示词", "steps": "采样步数", "cfg": "CFG",
    "sampler_name": "采样器", "scheduler": "调度器", "denoise": "降噪强度",
    "seed": "随机种子", "megapixels": "处理分辨率(百万像素)", "upscale_method": "放大方式",
    "unet_name": "UNet 名称", "vae_name": "VAE 名称", "clip_name": "CLIP 名称",
    "positive": "提示词", "文本": "提示词", "步数": "采样步数",
    "模型": "模型", "LoRA数量": "LoRA 加载",
}

# 前端虚拟节点类型
REROUTE_TYPES = {"Reroute"}
GET_TYPES = {"GetNode", "easy getNode"}
SET_TYPES = {"SetNode", "easy setNode"}
MULTISET_TYPES = {"QQMultiSet", "WyslMultiSet"}
PRIMITIVE_TYPES = {"PrimitiveNode"}
OPTION_VALUE_TYPES = {"GGOptionConfirm"}


def is_set_node_type(ctype):
    """无线变量存放节点。单变量 SetNode 与多变量 *MultiSet 都归于此类。"""
    return ctype in SET_TYPES or ctype in MULTISET_TYPES or (
        isinstance(ctype, str) and ctype.lower().endswith("multiset"))

# 已知的纯前端节点:没有后端执行语义,也不需要解析连线。
FRONTEND_ONLY_TYPES = {
    "Note", "MarkdownNote", "Label (rgthree)", "Fast Groups Bypasser (rgthree)",
    "Fast Groups Muter (rgthree)",
} | REROUTE_TYPES | GET_TYPES | SET_TYPES | MULTISET_TYPES | PRIMITIVE_TYPES | OPTION_VALUE_TYPES

# 无法从选项判断、但确定是前端控件的类型。
# 注意: default/min/max 等选项不能作为控件判据,IMAGE/MASK/LATENT/DICT 等 socket
# 同样会声明 default,误判会让 widgets_values 槽位整体错位。
EXTRA_WIDGET_TYPES = {
    "INT", "FLOAT", "STRING", "BOOLEAN", "COMBO",
    "IMAGEUPLOAD", "EASY_COMBO", "EASY_MULTI_ANGLE", "EASY_PROMPT_AWAIT_BAR",
    "EASY_PROMPT_STYLES", "EASY_TABLE_EDITOR", "COMFY_DYNAMICCOMBO_V3",
    "COLOR", "COLORCODE", "KEY", "IMAGECOMPARE",
}

# V3 自定义控件类型: socketless 只表示“没有输入插槽”,不改变控件身份。
V3_WIDGET_TYPES = {
    "COLORS", "BOUNDING_BOX", "BOUNDING_BOXES", "CURVE", "COMPOSITOR",
    "VIDEO_EDIT", "RESOLUTION_PREVIEW", "CAMERA_INFO_STATE",
}

# 纯前端展示/编辑状态: 占用控件槽位,但不提交给后端。
UI_ONLY_TYPES = {
    "RESOLUTION_PREVIEW", "IMAGECOMPARE", "CAMERA_INFO_STATE", "PREVIEW_3D",
}

# 以分组/动态形式出现、不占单个 widgets_values 槽位的类型
GROUP_INPUT_TYPES = {"COMFY_AUTOGROW_V3", "COMFY_DYNAMICGROUP_V3"}
DYNAMIC_COMBO_TYPE = "COMFY_DYNAMICCOMBO_V3"

# 前端会给 seed 类 INT 控件自动附加控制值(即使 object_info 未声明)。
CONTROL_VALUES = {"fixed", "increment", "decrement", "randomize"}

# 按输入名判断职责,不依赖节点 class_type(自定义节点类型五花八门,类型白名单覆盖不全)。
STEPS_INPUT_PATTERNS = ("steps", "step", "sampling_steps", "sampling_step", "步数")
RES_INPUT_PATTERNS = ("megapixels", "width", "height", "resolution", "latent", "pixel",
                      "size", "分辨率")
LORA_INPUT_PATTERNS = ("lora", "loras", "LoRA")
PROMPT_INPUT_NAMES = ("prompt", "value", "text", "positive_prompt", "positive", "文本")
# 提示词增强/优化/反推、列表拼接、风格选择与 LoRA 搜索等节点:即使带 prompt 输入,
# 也不是用户常规编辑的主提示词,不进手机常用区。
PROMPT_ACCESSORY_PATTERNS = (
    "promptenhancer", "promptoptimizer", "promptlist", "promptconcat",
    "stylesselector", "imageinfer", "llama", "objectdetector", "lorafile",
    "stacker", "promptreverse",
)

_MISSING = object()


def is_steps_input(name):
    """采样步数:必须是末级字段名,避免把 resolution_steps 等精度参数当成步数。"""
    leaf = name.lower().rsplit(".", 1)[-1]
    if leaf in STEPS_INPUT_PATTERNS:
        return True
    return (leaf.endswith("_steps") and "resolution" not in leaf
            and "step_cache" not in leaf)


def is_resolution_input(name):
    """处理分辨率:排除 resolution_steps/step_cache 与预览画布高度等非处理参数。"""
    n = name.lower()
    leaf = n.rsplit(".", 1)[-1]
    if any(x in leaf for x in ("step", "cache", "reel")):
        return False
    return any(p in leaf for p in RES_INPUT_PATTERNS) and "size" not in leaf


def _normalize_class_name(ctype):
    return "".join(ch for ch in str(ctype or "").lower() if ch.isalnum())


def is_prompt_accessory(ctype, title=""):
    """提示词增强/优化/反推或辅助节点,不作为手机常用区的正向提示词。"""
    norm = _normalize_class_name(ctype)
    if any(p in norm for p in PROMPT_ACCESSORY_PATTERNS):
        return True
    t = str(title or "")
    return any(h in t for h in ("反向", "负向", "负面", "提示词增强", "提示词优化", "提示词反推"))


def is_lora_input(name):
    return any(p in name for p in LORA_INPUT_PATTERNS)


def is_sampler_or_scheduler(name):
    """采样器/调度器:用户通常不调整,强制进入折叠区,绝不默认展示。"""
    n = name.lower()
    return "sampler" in n or "scheduler" in n


def fetch_object_info(base_url):
    url = base_url.rstrip("/") + "/object_info"
    req = urllib.request.Request(url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=20) as resp:
        return json.loads(resp.read().decode("utf-8"))


def load_real_types(object_info):
    return set(object_info.keys())


def ordered_inputs(object_info, ctype):
    """按 object_info 的 input_order 返回输入定义列表 [(name, spec)]。
    widgets_values 的顺序取决于节点定义的声明顺序(input_order)。
    """
    meta = object_info.get(ctype) or {}
    result = []
    order = meta.get("input_order")
    if not order:
        inputs = meta.get("input", {})
        for sec in ("required", "optional"):
            for name in inputs.get(sec, {}):
                result.append((name, inputs[sec][name]))
        return result
    declared = meta.get("input", {})
    for sec in ("required", "optional"):
        for name in order.get(sec, []):
            spec = declared.get(sec, {}).get(name)
            if spec is not None:
                result.append((name, spec))
    return result


def required_input_names(object_info, ctype):
    return set(((object_info.get(ctype) or {}).get("input", {}).get("required", {}) or {}).keys())


def _spec_opts(spec):
    if not isinstance(spec, (list, tuple)) or len(spec) < 2:
        return {}
    return spec[1] if isinstance(spec[1], dict) else {}


def is_widget_spec(spec):
    """无原生序列化信息时,按 object_info 判断是否控件。

    只认前端确实会渲染成控件的类型,不能仅凭 default/min 等选项判定:
    IMAGE/MASK/LATENT/DICT 等 socket 也会声明 default,误判会导致槽位错位。
    socketless 只表示不创建输入插槽,仍是控件;forceInput 才强制为 socket。
    """
    if not isinstance(spec, (list, tuple)) or not spec:
        return False
    opts = _spec_opts(spec)
    if opts.get("forceInput"):
        return False
    if isinstance(spec[0], list):
        return True
    ts = spec[0]
    if not isinstance(ts, str):
        return True
    if ts in GROUP_INPUT_TYPES:
        return False
    if opts.get("widgetType"):
        return True
    if ts in EXTRA_WIDGET_TYPES or ts in V3_WIDGET_TYPES:
        return True
    return ts in ("INT", "FLOAT", "STRING", "BOOLEAN", "COMBO")


def widget_slot_count(spec, native_inp):
    """该输入在 widgets_values 中占用的槽位数量。

    原生 nodes[].inputs 是前端序列化结果,是控件/连线的权威信号:
    - 带 widget 标记 => 控件,占槽(即使同时有连线)。
    - 出现在 inputs 但没有 widget 标记 => 连线 socket,不占槽(未连线也不占)。
    - 完全没出现在 inputs => 按 object_info 类型推断;分组型输入不占单槽。
    """
    opts = _spec_opts(spec)
    if native_inp is not None:
        if native_inp.get("widget") is not None:
            return 1 + (1 if "control_after_generate" in opts else 0)
        return 0
    if not is_widget_spec(spec):
        return 0
    return 1 + (1 if "control_after_generate" in opts else 0)


def default_value(spec):
    """控件缺值时的默认值;无法确定时返回 _MISSING。"""
    opts = _spec_opts(spec)
    if "default" in opts:
        return opts["default"]
    if not isinstance(spec, (list, tuple)) or not spec:
        return _MISSING
    if isinstance(spec[0], list):
        return spec[0][0] if spec[0] else _MISSING
    ts = spec[0]
    if ts == "COMBO":
        options = opts.get("options") or []
        return options[0] if options else _MISSING
    if ts == DYNAMIC_COMBO_TYPE:
        options = opts.get("options") or []
        return options[0].get("key") if options else _MISSING
    if ts == "INT":
        return 0
    if ts == "FLOAT":
        return 0.0
    if ts == "STRING":
        return ""
    if ts == "BOOLEAN":
        return False
    return _MISSING


def _value_matches_type(spec, value):
    """控件值是否与声明类型一致。用于自动发现槽位错位。

    COMBO 不做选项成员校验: 节点升级后工作流可能保存旧选项值,这类情况不属于
    槽位错位; 但 BOOLEAN 落到 COMBO 槽位(旧版节点改名后常见)必须判为错位,
    否则会把 true/false 提交给只接受字符串选项的后端。
    """
    if not isinstance(spec, (list, tuple)) or not spec:
        return True
    if isinstance(spec[0], list):
        return not isinstance(value, bool) and (
            isinstance(value, (str, int, float, list)) or value is None)
    ts = spec[0]
    if ts == "BOOLEAN":
        return isinstance(value, bool)
    if ts == "INT":
        return isinstance(value, int) and not isinstance(value, bool)
    if ts == "FLOAT":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if ts == "STRING":
        return isinstance(value, str)
    if ts in ("COMBO", DYNAMIC_COMBO_TYPE):
        return not isinstance(value, bool) and (
            isinstance(value, (str, int, float, list)) or value is None)
    if ts in ("COLOR", "COLORCODE"):
        return isinstance(value, str)
    if ts in ("COLORS", "BOUNDING_BOXES"):
        return isinstance(value, list)
    if ts in ("BOUNDING_BOX", "CURVE", "COMPOSITOR", "VIDEO_EDIT"):
        return isinstance(value, (dict, list))
    return True


def _seed_like_name(widget_id):
    """seed 类控件:前端会自动附加 fixed/randomize 控制槽。"""
    leaf = widget_id.rsplit(".", 1)[-1].lower()
    return leaf.endswith("seed") or leaf in ("种子", "随机种子")


def _is_dynamic_combo_spec(spec):
    return (isinstance(spec, (list, tuple)) and bool(spec)
            and isinstance(spec[0], str) and spec[0] == DYNAMIC_COMBO_TYPE)


def _dynamic_options(spec):
    return [o for o in (_spec_opts(spec).get("options") or []) if isinstance(o, dict)]


def _dynamic_option_specs(spec, selected_key):
    """选中项的有序子控件 {sub_name: sub_spec}。"""
    result = OrderedDict()
    for option in _dynamic_options(spec):
        if option.get("key") != selected_key:
            continue
        for section in ("required", "optional"):
            for sub_name, sub_spec in ((option.get("inputs") or {}).get(section) or {}).items():
                result[sub_name] = sub_spec
        break
    return result


def _expand_dynamic_specs(root_id, spec, selected_key, out):
    """展开 COMFY_DYNAMICCOMBO_V3 选中项的子控件 spec,键为带前缀的完整 id。"""
    for sub_name, sub_spec in _dynamic_option_specs(spec, selected_key).items():
        out["%s.%s" % (root_id, sub_name)] = sub_spec


def _spec_type_name(spec):
    if not isinstance(spec, (list, tuple)) or not spec:
        return None
    ts = spec[0]
    if isinstance(ts, list):
        return "COMBO"
    return ts if isinstance(ts, str) else None


def _native_type_matches_spec(native_type, spec):
    """原生 widget/socket 的 type 是否能落到当前声明的输入上。"""
    st = _spec_type_name(spec)
    if st is None or not isinstance(native_type, str) or not native_type:
        return True
    nt = native_type
    if nt == st or st == "*" or nt == "*":
        return True
    nset = set(x.strip() for x in nt.split(",") if x.strip())
    sset = set(x.strip() for x in st.split(",") if x.strip())
    if "*" in nset or "*" in sset or nset & sset:
        return True
    if st == "FLOAT" and nt == "INT":
        return True
    return False


def _is_native_ui_only(inp):
    """原生 inputs 里的纯前端控件:上传按钮/预览面板,不进入后端 API。"""
    t = inp.get("type")
    if not isinstance(t, str):
        return False
    return t in ("IMAGEUPLOAD", "VIDEOUPLOAD", "AUDIOUPLOAD", "PREVIEW_3D",
                 "IMAGECOMPARE") or t.endswith("UPLOAD")


def _looks_dynamic_kwarg(name, declared_names):
    """anything2/any_02 这类同前缀编号输入是节点动态 kwargs,不能改名。"""
    for dn in declared_names:
        if name != dn and name.startswith(dn) and name[len(dn):].strip("_0123456789") == "":
            return True
    return False


def _is_ui_only_socket(spec):
    """必填但没有后端语义的输入(UI 预览/上传按钮/动态分组),缺失不算未解析。

    socketless 不能作为判据: COLOR/BOUNDING_BOX/CURVE 等仍是需要提交的控件;
    只有明确的展示/编辑状态类型才跳过提交。
    """
    if not isinstance(spec, (list, tuple)) or not spec:
        return False
    ts = _spec_type_name(spec)
    if not isinstance(ts, str):
        return False
    if ts.endswith("UPLOAD"):
        return True
    return ts in UI_ONLY_TYPES or ts in GROUP_INPUT_TYPES


class _SubgraphFlattener(object):
    """把前端子图实例内联成扁平节点图。

    新版 ComfyUI 前端支持把一组节点封装为子图,序列化为
    nodes[].type == definitions.subgraphs[].id。后端 API 没有子图概念,
    官方导出也会内联,这里还原同样的语义:
    - 内部节点 id 加 "实例id:" 前缀,避免与外层冲突;
    - 内部连线 origin_id=-10 表示子图输入,引用实例输入(外层连线或控件值);
    - 内部连线 target_id=-20 表示子图输出,外层消费者直连内部真实输出;
    - 实例控件值按实例 inputs 中 widget 标记的顺序消费,兼容 seed 控制槽;
    - 静音(mode=2)实例不展开;旁路(mode=4)按类型把输出接到对应输入。
    """

    def __init__(self, native):
        defs = (native.get("definitions") or {}).get("subgraphs") or {}
        if isinstance(defs, dict):
            defs = list(defs.values())
        self.defs = {sg.get("id"): sg for sg in defs if sg.get("id")}
        self.nodes = []
        self.links = []
        self.next_link = 1
        for lnk in native.get("links") or []:
            lid = lnk.get("id") if isinstance(lnk, dict) else (lnk[0] if lnk else None)
            if isinstance(lid, int):
                self.next_link = max(self.next_link, lid + 1)
        self.lit_count = 0
        self.instance_cache = {}
        self.expanding = set()

    def expand_all(self, top_nodes, top_links):
        self._expand_container(top_nodes, top_links, "", [])
        return {"nodes": self.nodes, "links": self.links}

    # -- 容器(顶层或子图定义)展开 --
    def _expand_container(self, nodes, raw_links, prefix, input_bindings):
        link_list = _normalize_links(raw_links)
        link_by_id = {lnk["id"]: lnk for lnk in link_list}
        by_id = {nd.get("id"): nd for nd in nodes}
        clones = {}

        for nd in nodes:
            if nd.get("type") in self.defs:
                continue
            clone = copy.deepcopy(nd)
            clone["id"] = "%s%s" % (prefix, nd.get("id"))
            for inp in clone.get("inputs") or []:
                if isinstance(inp, dict) and "link" in inp:
                    inp["link"] = None
            clones[nd.get("id")] = clone
            self.nodes.append(clone)

        def resolve_origin(origin_id, origin_slot):
            if origin_id == -10:
                if 0 <= origin_slot < len(input_bindings):
                    return input_bindings[origin_slot]
                return None
            nd = by_id.get(origin_id)
            if nd is None:
                return None
            if nd.get("type") in self.defs:
                if nd.get("mode", 0) == 2:
                    return None
                if nd.get("mode", 0) == 4:
                    return self._bypass_binding(nd, origin_slot, link_by_id,
                                                resolve_origin)
                outs = self._expand_instance(nd, prefix, link_by_id,
                                             input_bindings, resolve_origin)
                return outs[origin_slot] if 0 <= origin_slot < len(outs) else None
            return {"kind": "ref", "id": "%s%s" % (prefix, origin_id),
                    "slot": origin_slot}

        for lnk in link_list:
            target_id = lnk["target_id"]
            if target_id == -20:
                continue
            target = by_id.get(target_id)
            if target is None or target.get("type") in self.defs:
                continue  # 子图实例的输入由实例展开时消费
            clone = clones.get(target_id)
            if clone is None:
                continue
            binding = resolve_origin(lnk["origin_id"], lnk["origin_slot"])
            self._connect(binding, clone, lnk["target_slot"])

        # 未被外层连线引用的子图也可能包含输出节点,主动展开所有启用的实例。
        for nd in nodes:
            if nd.get("type") in self.defs and nd.get("mode", 0) == 0:
                self._expand_instance(nd, prefix, link_by_id, input_bindings,
                                      resolve_origin)

        out_bindings = []
        # 输出端口按 target_id == -20 的连线还原。
        outs_by_slot = {}
        for lnk in link_list:
            if lnk["target_id"] == -20:
                outs_by_slot[lnk["target_slot"]] = lnk
        if outs_by_slot:
            for slot in range(max(outs_by_slot) + 1):
                lnk = outs_by_slot.get(slot)
                out_bindings.append(
                    resolve_origin(lnk["origin_id"], lnk["origin_slot"]) if lnk else None)
        return out_bindings

    def _expand_instance(self, inst, parent_prefix, parent_links, parent_bindings,
                         parent_resolve):
        cached = self.instance_cache.get(id(inst))
        if cached is not None:
            return cached
        sg = self.defs.get(inst.get("type"))
        if sg is None or id(inst) in self.expanding:
            return []
        self.expanding.add(id(inst))
        try:
            prefix = "%s%s:" % (parent_prefix, inst.get("id"))
            bindings = self._instance_input_bindings(
                inst, sg, parent_links, parent_resolve)
            outs = self._expand_container(sg.get("nodes") or [], sg.get("links") or [],
                                          prefix, bindings)
            self.instance_cache[id(inst)] = outs
            return outs
        finally:
            self.expanding.discard(id(inst))

    def _instance_input_bindings(self, inst, sg, parent_links, parent_resolve):
        inst_inputs = [i for i in (inst.get("inputs") or []) if isinstance(i, dict)]
        by_name = {i.get("name"): i for i in inst_inputs}
        wv = inst.get("widgets_values")
        if not isinstance(wv, list):
            wv = []
        bindings = []
        wi = 0
        for idx, sgin in enumerate(sg.get("inputs") or []):
            inp = inst_inputs[idx] if idx < len(inst_inputs) else None
            if inp is None or inp.get("name") != sgin.get("name"):
                inp = by_name.get(sgin.get("name"), inp)
            link = inp.get("link") if inp is not None else None
            value = _MISSING
            if inp is not None and inp.get("widget") is not None and wi < len(wv):
                value = wv[wi]
                wi += 1
                wid = (inp.get("widget") or {}).get("name") or inp.get("name")
                if (_seed_like_name(wid) and wi < len(wv)
                        and wv[wi] in CONTROL_VALUES):
                    wi += 1
            if link is not None:
                lnk = parent_links.get(link)
                bindings.append(
                    parent_resolve(lnk["origin_id"], lnk["origin_slot"])
                    if lnk is not None else None)
            elif value is not _MISSING:
                bindings.append({"kind": "value", "value": value})
            else:
                bindings.append(None)
        return bindings

    def _bypass_binding(self, inst, out_slot, parent_links, parent_resolve):
        """旁路子图:按输出类型找到同类型输入,把消费者重连到该输入。"""
        sg = self.defs.get(inst.get("type")) or {}
        outs = sg.get("outputs") or []
        out_type = outs[out_slot].get("type") if 0 <= out_slot < len(outs) else None
        bindings = self._instance_input_bindings(
            inst, sg, parent_links, parent_resolve)
        for idx, sgin in enumerate(sg.get("inputs") or []):
            it = sgin.get("type")
            if it == out_type or it == "*" or out_type == "*":
                return bindings[idx] if idx < len(bindings) else None
        return None

    def _connect(self, binding, target_node, target_slot):
        inputs = target_node.get("inputs") or []
        if not isinstance(target_slot, int) or not (0 <= target_slot < len(inputs)):
            return
        if binding is None:
            inputs[target_slot]["link"] = None
            return
        if binding.get("kind") == "ref":
            origin_id, origin_slot = binding["id"], binding["slot"]
        elif binding.get("kind") == "value":
            prim_id = binding.get("prim_id")
            if prim_id is None:
                prim_id = "__lit_%d" % self.lit_count
                self.lit_count += 1
                self.nodes.append({
                    "id": prim_id, "type": "PrimitiveNode", "mode": 0,
                    "inputs": [], "widgets_values": [binding.get("value")],
                    "outputs": [{"name": "value", "type": "*", "links": []}],
                })
                binding["prim_id"] = prim_id
            origin_id, origin_slot = prim_id, 0
        else:
            inputs[target_slot]["link"] = None
            return
        lid = self.next_link
        self.next_link += 1
        self.links.append([lid, origin_id, origin_slot,
                           target_node.get("id"), target_slot, "*"])
        inputs[target_slot]["link"] = lid


def _normalize_links(raw_links):
    result = []
    for lnk in raw_links or []:
        if isinstance(lnk, dict):
            result.append({
                "id": lnk.get("id"), "origin_id": lnk.get("origin_id"),
                "origin_slot": lnk.get("origin_slot"),
                "target_id": lnk.get("target_id"),
                "target_slot": lnk.get("target_slot"),
            })
        elif isinstance(lnk, (list, tuple)) and len(lnk) >= 5:
            result.append({"id": lnk[0], "origin_id": lnk[1],
                           "origin_slot": lnk[2], "target_id": lnk[3],
                           "target_slot": lnk[4]})
    return result


def flatten_subgraphs(native):
    """如果工作流含子图定义,返回内联后的 native 结构;否则返回 None。"""
    flattener = _SubgraphFlattener(native)
    if not flattener.defs:
        return None
    return flattener.expand_all(native.get("nodes") or [], native.get("links") or [])


class GraphResolver(object):
    """按 ComfyUI 前端语义解析连线来源。"""

    def __init__(self, native, object_info):
        self.node_map = {nd["id"]: nd for nd in native.get("nodes", [])}
        self.link_map = {}
        for lnk in native.get("links", []):
            if len(lnk) >= 5:
                self.link_map[lnk[0]] = (lnk[1], lnk[2])
        self.real_types = set(object_info.keys())
        self.set_nodes = {}  # 变量名 -> (节点, 输入槽位)
        for nd in self.node_map.values():
            ctype = nd.get("type")
            if not is_set_node_type(ctype):
                continue
            wv = nd.get("widgets_values") or []
            for slot, var in enumerate(wv):
                if isinstance(var, str) and var:
                    self.set_nodes.setdefault(var, (nd, slot))
                if ctype in SET_TYPES:
                    break  # 单变量节点只取第一个值

    def resolve(self, link_id, seen=None, trace=None):
        """返回 ('ref', node_id, slot) / ('value', literal) / None。

        None 表示无法解析(断链、静音、插件缺失或未知虚拟节点)。
        trace 非空列表时,在失败的最深一层追加根因(仅保留首个,避免被上层覆盖)。
        """
        def note(reason):
            if trace is not None and not trace:
                trace.append(reason)
            return None

        if link_id is None:
            return note("no-link")
        if seen is None:
            seen = set()
        if link_id in seen:
            return note("cycle")
        seen.add(link_id)
        entry = self.link_map.get(link_id)
        if entry is None:
            return note("broken-link")
        src, slot = entry
        nd = self.node_map.get(src)
        if nd is None:
            return note("missing-source-node")
        ctype = nd.get("type")
        mode = nd.get("mode", 0)

        if ctype in REROUTE_TYPES:
            ins = nd.get("inputs") or []
            return self.resolve(ins[0].get("link"), seen, trace) if ins else note("reroute-no-input")

        if is_set_node_type(ctype):
            ins = nd.get("inputs") or []
            # 多变量 Set 的每个输出槽对应同序号输入;单变量 Set 只有槽 0。
            idx = 0 if ctype in SET_TYPES else slot
            if idx >= len(ins):
                return note("set-no-input")
            return self.resolve(ins[idx].get("link"), seen, trace)

        if ctype in GET_TYPES:
            wv = nd.get("widgets_values") or []
            target = self.set_nodes.get(str(wv[0])) if wv else None
            if target is None:
                return note("get-missing-variable")
            setter, slot = target
            ins = setter.get("inputs") or []
            if slot >= len(ins):
                return note("get-slot-out-of-range")
            return self.resolve(ins[slot].get("link"), seen, trace)

        if ctype in PRIMITIVE_TYPES:
            wv = nd.get("widgets_values") or []
            return ("value", wv[0]) if wv else note("primitive-no-value")

        if ctype in OPTION_VALUE_TYPES:
            wv = nd.get("widgets_values") or []
            return ("value", wv[slot]) if 0 <= slot < len(wv) else note("option-slot-out-of-range")

        if ctype in self.real_types:
            if mode == 2:  # 静音:不执行,输出断开
                return note("muted-src")
            if mode == 4:  # 旁路:按输出类型重连到匹配的输入
                outs = nd.get("outputs") or []
                out_type = outs[slot].get("type") if 0 <= slot < len(outs) else None
                used = set()
                for inp in nd.get("inputs") or []:
                    lk = inp.get("link")
                    if lk is None or lk in used:
                        continue
                    it = inp.get("type")
                    if it == out_type or it == "*" or out_type == "*":
                        used.add(lk)
                        return self.resolve(lk, seen, trace)
                return note("bypass-no-match:%s" % ctype)
            return ("ref", src, slot)

        # 未知虚拟节点:仅当输出为通配符时按类型直通(Reroute 类语义)
        outs = nd.get("outputs") or []
        out_type = outs[slot].get("type") if 0 <= slot < len(outs) else None
        if out_type not in ("*", None):
            return note("plugin-missing:%s" % ctype)
        for inp in nd.get("inputs") or []:
            lk = inp.get("link")
            if lk is None:
                continue
            it = inp.get("type")
            if out_type == "*" or it == out_type or it == "*":
                return self.resolve(lk, seen, trace)
        return note("unknown-virtual:%s" % ctype)

    def unsupported_types(self):
        """仍带输出连线的非 object_info 节点类型(插件未加载)。"""
        result = {}
        for nd in self.node_map.values():
            ctype = nd.get("type")
            if (ctype in self.real_types or ctype in FRONTEND_ONLY_TYPES
                    or is_set_node_type(ctype)):
                continue
            if nd.get("mode", 0) != 0:
                continue
            if not any((out.get("links") or []) for out in nd.get("outputs") or []):
                continue
            result[ctype] = result.get(ctype, 0) + 1
        return result


def _to_api_value(resolved):
    if resolved is None:
        return _MISSING
    if resolved[0] == "ref":
        return [str(resolved[1]), resolved[2]]
    return resolved[1]


def _assign_widget_values(native_inputs, widgets, declared):
    """按原生 inputs 的真实控件顺序对齐 widgets_values,并自动适配版本漂移。

    返回 [(api_key或None, stored_value或_MISSING, native_input或None)]。
    api_key=None 表示上传/预览等纯前端控件,值只消费不提交。

    适配规则:
    - 名称与当前声明一致时直接使用(优先 widget.name,其次 input.name)。
    - widget.name 是 "组.子名" 且组是 DYNAMICCOMBO 时原样保留,后端认这种键。
    - 其余控件按声明顺序做位置回退(节点升级后英文/旧中文名改名的场景),
      只映射到尚未使用且类型兼容的声明控件。
    - DYNAMICCOMBO 组按控件值里出现的选项 key 定位真实选择器;旧版扁平控件
      没有选择器时,按子控件名称命中数 + 类型序列推断选项,并改写成点分键。
    - 仍无法归位(动态 kwargs,如 anything2/any_02)保留原名。
    """
    items = []
    wi = 0
    for inp in native_inputs:
        if inp.get("widget") is None:
            continue
        raw = (inp.get("widget") or {}).get("name") or inp.get("name")
        if not isinstance(raw, str):
            raw = str(raw)
        value = widgets[wi] if wi < len(widgets) else _MISSING
        if value is not _MISSING:
            wi += 1
            spec = declared.get(raw)
            opts = _spec_opts(spec) if spec is not None else {}
            if "control_after_generate" in opts:
                if wi < len(widgets):
                    wi += 1
            elif (_seed_like_name(raw) and wi < len(widgets)
                  and widgets[wi] in CONTROL_VALUES):
                wi += 1
        items.append({
            "raw": raw, "name": inp.get("name"), "value": value, "inp": inp,
            "type": inp.get("type"), "key": None, "dead": False,
            "ui": _is_native_ui_only(inp),
        })

    assigned = []
    if not items:
        # 旧版序列化没有 inputs 控件标记时,只能按 object_info 声明顺序消费。
        # UI 预览/上传按钮仍占用控件槽位,只是不提交。
        for name, spec in declared.items():
            if not is_widget_spec(spec):
                continue
            value = widgets[wi] if wi < len(widgets) else _MISSING
            if value is not _MISSING:
                wi += 1
                if ("control_after_generate" in _spec_opts(spec)
                        and wi < len(widgets)
                        and widgets[wi] in CONTROL_VALUES):
                    wi += 1
            if _is_ui_only_socket(spec):
                continue
            assigned.append((name, value, None))
        return assigned

    widget_names = [n for n, s in declared.items()
                    if is_widget_spec(s) and not _is_ui_only_socket(s)]
    dynamic_groups = {n: s for n, s in declared.items() if _is_dynamic_combo_spec(s)}
    used = set()

    # 1) 名称直接命中;同组子控件保留点分键。
    for it in items:
        if it["ui"]:
            it["dead"] = True
            continue
        for cand in (it["raw"], it["name"]):
            if isinstance(cand, str) and cand in declared:
                it["key"] = cand
                used.add(cand)
                break
        if it["key"] is not None:
            continue
        for cand in (it["raw"], it["name"]):
            if not isinstance(cand, str) or "." not in cand:
                continue
            root = cand.split(".", 1)[0]
            if root in dynamic_groups:
                it["key"] = cand
                break

    # 1b) 动态组子控件与隐藏根输入同名时,原生控件属于动态组。
    #     例: SaveVideo.format 的子输入 codec 与隐藏根输入 codec 同名,前端只
    #     序列化一个 codec 控件;若写进根输入,format.codec 会错误回退默认值。
    for g, gspec in dynamic_groups.items():
        option_keys = [o.get("key") for o in _dynamic_options(gspec)]
        chosen = None
        for it in items:
            if it["key"] == g and it["value"] in option_keys:
                chosen = it["value"]
                break
        if chosen is None:
            for it in items:
                if ((it["key"] or "").startswith(g + ".")
                        and it["value"] in option_keys):
                    chosen = it["value"]
                    break
        if chosen is None:
            continue
        sub_specs = _dynamic_option_specs(gspec, chosen)
        for it in items:
            key = it["key"]
            if (not key or "." in key or key == g or key not in sub_specs):
                continue
            root_spec = declared.get(key)
            if not _spec_opts(root_spec).get("hidden"):
                continue
            full = "%s.%s" % (g, key)
            it["key"] = full
            used.discard(key)
            used.add(full)

    replacements = {}  # items 下标 -> 重建后的赋值项

    # 2) 动态组合:定位真实选择器,或从旧版扁平控件推断选项。
    for g, gspec in dynamic_groups.items():
        related = [it for it in items
                   if it["key"] == g or (it["key"] or "").startswith(g + ".")]
        option_keys = [o.get("key") for o in _dynamic_options(gspec)]
        anchor = None
        chosen = None
        for it in related:
            if it["key"] == g and it["value"] in option_keys:
                anchor, chosen = it, it["value"]
                break
        if anchor is None:
            for it in related:
                if it["value"] in option_keys:
                    anchor, chosen = it, it["value"]
                    break

        if anchor is not None:
            start = items.index(anchor)
            selector_anchor = anchor["key"] == g
            rel_ids = set(id(it) for it in related)
            following = [it for it in items[start + 1:] if id(it) in rel_ids]
            subs = _dynamic_option_specs(gspec, chosen)
            matched_names = set(it["key"] for it in related
                                if (it["key"] or "").startswith(g + "."))
            rebuilt = [{"key": g, "value": chosen, "inp": anchor["inp"],
                        "type": anchor["type"], "ui": False, "dead": False,
                        "raw": g, "name": g}]
            for j, (sub_name, sub_spec) in enumerate(subs.items()):
                full = "%s.%s" % (g, sub_name)
                matched = None
                for it in related:
                    if it["key"] == full:
                        matched = it
                        break
                # 选择器在正确位置时,子值按控件名对齐(有些子输入是无 widget
                # 的 socket,不占值槽);选择器值错位时只能按值的先后顺序取。
                if selector_anchor and matched_names:
                    if matched is not None:
                        src, val = matched, matched["value"]
                    else:
                        src, val = anchor, _MISSING
                elif selector_anchor:
                    if j < len(following):
                        src, val = following[j], following[j]["value"]
                    else:
                        src, val = anchor, _MISSING
                else:
                    val = following[j]["value"] if j < len(following) else _MISSING
                    src = matched if matched is not None else (
                        following[j] if j < len(following) else anchor)
                rebuilt.append({"key": full, "value": val, "inp": src["inp"],
                                "type": src["type"], "ui": False, "dead": False,
                                "raw": full, "name": full})
            replacements[start] = rebuilt
            for it in related:
                it["dead"] = True
            used.add(g)
            continue

        # 无选择器:旧版扁平控件,按名称命中数 + 类型序列推断选项。
        candidates = [it for it in items if not it["ui"] and it["key"] is None]
        if not candidates:
            continue
        best = None
        for oi, option in enumerate(_dynamic_options(gspec)):
            key = option.get("key")
            subs = _dynamic_option_specs(gspec, key)
            if not subs:
                continue
            sub_names = list(subs)
            name_hits = sum(
                1 for it in candidates
                if any(it["raw"] == sn or it["name"] == sn for sn in sub_names))
            type_hits = 0
            for j, sub_spec in enumerate(subs.values()):
                if j < len(candidates) and _native_type_matches_spec(
                        candidates[j]["type"], sub_spec):
                    type_hits += 1
            if name_hits == 0 and type_hits < min(2, len(subs)):
                continue
            score = (name_hits, type_hits)
            if best is None or score > best[0]:
                best = (score, oi, key, subs)
        if best is None:
            continue
        _, _, chosen, subs = best
        mapping = []
        taken = set()
        for j, (sub_name, sub_spec) in enumerate(subs.items()):
            it = None
            for cand in candidates:
                if id(cand) in taken:
                    continue
                if cand["raw"] == sub_name or cand["name"] == sub_name:
                    it = cand
                    break
            if it is None and j < len(candidates):
                cand = candidates[j]
                if (id(cand) not in taken
                        and _native_type_matches_spec(cand["type"], sub_spec)):
                    it = cand
            if it is not None:
                taken.add(id(it))
                mapping.append((it, sub_name))
        if not mapping:
            continue
        start = items.index(mapping[0][0])
        rebuilt = [{"key": g, "value": chosen, "inp": None, "type": None,
                    "ui": False, "dead": False, "raw": g, "name": g}]
        for it, sub_name in mapping:
            full = "%s.%s" % (g, sub_name)
            rebuilt.append({"key": full, "value": it["value"], "inp": it["inp"],
                            "type": it["type"], "ui": False, "dead": False,
                            "raw": full, "name": full})
        other_subs = set()
        for option in _dynamic_options(gspec):
            if option.get("key") == chosen:
                continue
            other_subs.update(_dynamic_option_specs(gspec, option.get("key")))
        for it in candidates:
            if id(it) in taken:
                it["dead"] = True
            elif it["raw"] in other_subs or it["name"] in other_subs:
                it["dead"] = True  # 其它模式的旧残留值,不属于当前模式
        replacements[start] = rebuilt
        used.add(g)

    # 3) 版本漂移位置回退:只映射类型兼容且尚未使用的声明控件。
    candidates_order = [n for n in widget_names if n not in dynamic_groups]
    cursor = 0
    for it in items:
        if it["ui"] or it["dead"] or it["key"] is not None:
            continue
        pick = None
        for idx in range(cursor, len(candidates_order)):
            name = candidates_order[idx]
            if name in used:
                continue
            spec = declared.get(name)
            if _is_ui_only_socket(spec):
                continue
            if not _native_type_matches_spec(it["type"], spec):
                continue
            pick = (idx, name)
            break
        if pick is not None:
            it["key"] = pick[1]
            used.add(pick[1])
            cursor = pick[0] + 1
        elif widget_names and "." not in it["raw"]:
            # 当前节点没有任何同类型控件可对应(旧版多余字段),提交也会被忽略。
            it["dead"] = True
        else:
            it["key"] = it["raw"]  # 动态 kwargs(anything2/any_02 等)保留原名

    # 4) 按原生顺序输出,动态组合在锚点处用重建结果替换。
    for idx, it in enumerate(items):
        if idx in replacements:
            for r in replacements[idx]:
                assigned.append((r["key"], r["value"], r["inp"]))
            continue
        if it["dead"] or it["ui"]:
            continue
        assigned.append((it["key"], it["value"], it["inp"]))
    return assigned


def _fill_dynamic_defaults(prefix, spec, out, depth=0):
    """缺省动态组:取第一个选项,并递归补齐子控件默认值。"""
    if depth > 4 or not _is_dynamic_combo_spec(spec):
        return
    options = _dynamic_options(spec)
    if not options:
        return
    key = options[0].get("key")
    if key is None:
        return
    out[prefix] = key
    for sub_name, sub_spec in _dynamic_option_specs(spec, key).items():
        full = "%s.%s" % (prefix, sub_name)
        if full in out:
            continue
        if _is_dynamic_combo_spec(sub_spec):
            _fill_dynamic_defaults(full, sub_spec, out, depth + 1)
        else:
            fallback = default_value(sub_spec)
            if fallback is not _MISSING:
                out[full] = fallback


def convert_api(native, object_info):
    """返回 (api_prompt, node_titles, diagnostics)。

    diagnostics = {
      "unresolved": [(node_id, class_type, input_name, reason)],
      "suspicious": [(node_id, class_type, input_name, expected, actual)],
      "unsupported": {class_type: count},
      "included": int, "skipped_muted": int, "skipped_bypassed": int,
    }
    """
    flat = flatten_subgraphs(native)
    if flat is not None:
        native = flat
    resolver = GraphResolver(native, object_info)
    diagnostics = {
        "unresolved": [], "suspicious": [], "unsupported": resolver.unsupported_types(),
        "included": 0, "skipped_muted": 0, "skipped_bypassed": 0,
    }
    api = {}
    titles = {}
    for nd in native.get("nodes", []):
        ctype = nd.get("type")
        if ctype not in resolver.real_types:
            continue
        mode = nd.get("mode", 0)
        if mode == 2:
            diagnostics["skipped_muted"] += 1
            continue
        if mode == 4:
            diagnostics["skipped_bypassed"] += 1
            continue
        nid = nd["id"]
        title = nd.get("title", ctype)
        titles[str(nid)] = title
        diagnostics["included"] += 1

        native_inputs = [inp for inp in nd.get("inputs", []) if isinstance(inp, dict)]
        declared = OrderedDict(ordered_inputs(object_info, ctype))
        required = required_input_names(object_info, ctype)
        widgets = nd.get("widgets_values")
        if not isinstance(widgets, list):
            widgets = []

        assigned = [(k, v, inp) for k, v, inp in
                    _assign_widget_values(native_inputs, widgets, declared)
                    if k is not None]
        covered = set(widget_id for widget_id, _, _ in assigned)

        # 动态组合: 先收齐组选中值,再展开子控件 spec(键为 group.sub)。
        # 子选项本身也可能是 DYNAMICCOMBO_V3,迭代展开。
        dynamic_specs = {}
        pending = []
        for widget_id, value, _ in assigned:
            spec = declared.get(widget_id)
            if (_is_dynamic_combo_spec(spec) and value is not _MISSING
                    and isinstance(value, str)):
                pending.append((widget_id, spec, value))
        expanded_groups = set()
        while pending:
            widget_id, spec, value = pending.pop(0)
            if widget_id in expanded_groups:
                continue
            expanded_groups.add(widget_id)
            _expand_dynamic_specs(widget_id, spec, value, dynamic_specs)
            for sub_id, sub_spec in list(dynamic_specs.items()):
                if sub_id in expanded_groups or not _is_dynamic_combo_spec(sub_spec):
                    continue
                for w2, v2, _ in assigned:
                    if w2 == sub_id and isinstance(v2, str):
                        pending.append((sub_id, sub_spec, v2))
                        break

        def spec_for(widget_id):
            if widget_id in declared:
                return declared[widget_id]
            return dynamic_specs.get(widget_id)

        inputs = {}
        for widget_id, value, native_inp in assigned:
            spec = spec_for(widget_id)
            if spec is not None and _is_ui_only_socket(spec):
                continue  # socketless/UI 预览控件没有后端语义,不提交也不报缺失
            link = native_inp.get("link") if native_inp is not None else None
            resolved = resolver.resolve(link) if link is not None else None
            if resolved is not None:
                resolved_value = _to_api_value(resolved)
                # 前端虚拟节点(如 GGOptionConfirm)也可能给出字面量;槽位错位时
                # 类型不匹配,必须告警并回退声明默认值,不能把字符串交给 INT 输入。
                if (resolved[0] == "value" and spec is not None
                        and not _value_matches_type(spec, resolved_value)):
                    expected = spec[0] if not isinstance(spec[0], list) else "COMBO"
                    diagnostics["suspicious"].append(
                        (nid, ctype, widget_id, expected, resolved_value))
                    fallback = default_value(spec)
                    if fallback is not _MISSING:
                        resolved_value = fallback
                inputs[widget_id] = resolved_value
                continue
            if value is _MISSING or value is None:
                fallback = default_value(spec) if spec is not None else _MISSING
                if fallback is not _MISSING:
                    inputs[widget_id] = fallback
                elif widget_id in required:
                    diagnostics["unresolved"].append(
                        (nid, ctype, widget_id, "missing-widget-value"))
                continue
            if (isinstance(value, str) and value == "" and spec is not None
                    and isinstance(spec[0], str) and spec[0] == "BOOLEAN"):
                # 旧版前端会把 BOOLEAN 序列化为空字符串,按 falsy 语义还原,不算槽位错位。
                inputs[widget_id] = False
                continue
            if spec is not None and not _value_matches_type(spec, value):
                expected = spec[0] if not isinstance(spec[0], list) else "COMBO"
                diagnostics["suspicious"].append(
                    (nid, ctype, widget_id, expected, value))
                fallback = default_value(spec)
                inputs[widget_id] = fallback if fallback is not _MISSING else value
            else:
                inputs[widget_id] = value

        # 连线输入(Reroute/GetNode/旁路等由 resolver 还原);autogrow 子输入
        # 以 "group.name" 形式直接作为 API 键。
        # 旧版工作流的 socket 名也可能漂移(如 image_A -> 图像_A),按声明顺序
        # 映射到尚未使用且类型兼容的 socket。
        declared_sockets = [(n, s) for n, s in declared.items()
                            if not is_widget_spec(s)]
        socket_names = [n for n, _ in declared_sockets]
        socket_name_set = set(socket_names)
        used_sockets = set()
        for inp in native_inputs:
            if inp.get("widget") is not None:
                continue
            name = inp.get("name")
            if not isinstance(name, str):
                continue
            target = None
            if name in declared:
                target = name
            elif "." in name and name.split(".", 1)[0] in declared:
                target = name  # autogrow / 动态组合子输入,dotted 即后端键
            if target is None and not _looks_dynamic_kwarg(name, socket_names):
                for cand, spec in declared_sockets:
                    if cand in used_sockets:
                        continue
                    if _is_ui_only_socket(spec):
                        continue
                    if not _native_type_matches_spec(inp.get("type"), spec):
                        continue
                    target = cand
                    break
            if target is None:
                target = name
            if target in socket_name_set:
                used_sockets.add(target)
                spec = declared[target]
            else:
                spec = declared.get(target)
            link = inp.get("link")
            trace = []
            resolved = resolver.resolve(link, trace=trace) if link is not None else None
            if resolved is not None:
                inputs[target] = _to_api_value(resolved)
                continue
            if spec is None:
                continue  # autogrow 子输入 / 旧版本遗留输入
            if target in required and not _is_ui_only_socket(spec):
                reason = trace[0] if trace else "no-link"
                diagnostics["unresolved"].append((nid, ctype, target, reason))

        # 声明了但原生图里完全找不到的控件: 必需项补默认值(例如节点新增了
        # 输入而旧工作流尚未保存它),可选项交给后端默认。
        used_or_missing = set(covered)
        used_or_missing.update(inputs.keys())
        for name, spec in declared.items():
            if name in used_or_missing:
                continue
            if not is_widget_spec(spec) or _is_ui_only_socket(spec):
                continue
            if _is_dynamic_combo_spec(spec):
                if name not in required:
                    continue
                expanded = {}
                _fill_dynamic_defaults(name, spec, expanded)
                for key, val in expanded.items():
                    if key not in inputs:
                        inputs[key] = val
                continue
            if name in required:
                fallback = default_value(spec)
                if fallback is not _MISSING:
                    inputs[name] = fallback
                else:
                    diagnostics["unresolved"].append(
                        (nid, ctype, name, "missing-widget-value"))

        # 动态组已展开但原生图缺某些子控件时补默认值(节点升级新增子项的常见情况)。
        for sub_name, sub_spec in dynamic_specs.items():
            if sub_name in inputs or _is_ui_only_socket(sub_spec):
                continue
            fallback = default_value(sub_spec)
            if fallback is not _MISSING:
                inputs[sub_name] = fallback

        # LoadImage 默认文件名清空,避免手机端沿用桌面测试图。
        if ctype == "LoadImage" and isinstance(inputs.get("image"), str):
            inputs["image"] = ""
        api[str(nid)] = {"inputs": inputs, "class_type": ctype,
                         "_meta": {"title": title}}
    return api, titles, diagnostics


def derive_ui(api, titles, category, order, source_filename):
    """根据 api prompt 推导 .ui.json 的 fields / labels。"""
    base_title = os.path.splitext(os.path.basename(source_filename))[0]
    for prefix in ("▶", "▷", "▶▶", "▶▷", "【", "】", "「", "」"):
        base_title = base_title.replace(prefix, "")
    base_title = base_title.strip(" ._-")

    fields = []
    labels = OrderedDict()
    defaults = {}
    seen = set()

    def editable_value(nid, iname):
        """该输入在 API 图里是否对应一个可编辑的真实值。
        连线引用([源id, 槽位])与 None 不是可编辑参数,进常用区会被手机端渲染成错误红框。
        """
        node = api.get(nid)
        if not node:
            return None, False
        v = node.get("inputs", {}).get(iname)
        if v is None:
            return None, False
        if isinstance(v, list) and len(v) == 2 and isinstance(v[0], str):
            return None, False
        return v, True

    def add(key, label, nid, iname):
        if key in seen:
            return
        node = api.get(nid) or {}
        if is_prompt_accessory(node.get("class_type"), titles.get(nid, "")):
            return
        v, ok = editable_value(nid, iname)
        if not ok:
            return
        seen.add(key)
        fields.append(key)
        labels[key] = label
        defaults[key] = v

    def label_for(nid, iname):
        t = titles.get(nid, "")
        if iname in INPUT_LABELS:
            return INPUT_LABELS[iname] if not t else f"{t} · {INPUT_LABELS[iname]}"
        return f"{t} · {iname}" if t else iname

    # 1) 输入图
    for nid, node in api.items():
        if "image" in node["inputs"] and node["class_type"] == "LoadImage":
            add(f"{nid}.image", label_for(nid, "image"), nid, "image")
            break

    # 2) 正向提示词:只选一个。沿采样器 positive 链路上溯,排除反向链路与
    #    提示词增强/反推等辅助节点;无采样器时才退回普通文本控件。
    def linked_source(value):
        if (isinstance(value, list) and len(value) == 2
                and isinstance(value[0], str)):
            return value[0]
        return None

    def upstream_closure(seeds):
        seen = set()
        stack = list(seeds)
        while stack:
            nid = stack.pop()
            if nid in seen or nid not in api:
                continue
            seen.add(nid)
            for v in api[nid].get("inputs", {}).values():
                src = linked_source(v)
                if src is not None and src not in seen:
                    stack.append(src)
        return seen

    positive_keys = ("positive", "正向提示词")
    negative_keys = ("negative", "负向提示词")
    sampler_ids = [
        nid for nid, node in api.items()
        if any(k in node.get("inputs", {}) for k in positive_keys)
        and any(k in node.get("inputs", {}) for k in negative_keys)
    ]
    positive_closure = upstream_closure(
        [s for s in (linked_source(api[nid]["inputs"].get(k))
                     for nid in sampler_ids for k in positive_keys) if s])
    negative_closure = upstream_closure(
        [s for s in (linked_source(api[nid]["inputs"].get(k))
                     for nid in sampler_ids for k in negative_keys) if s])

    prompt_candidates = []
    for nid, node in api.items():
        ctype = node.get("class_type")
        if is_prompt_accessory(ctype, titles.get(nid, "")):
            continue
        if sampler_ids and nid not in positive_closure:
            continue
        if nid in negative_closure and nid not in positive_closure:
            continue
        for iname, value in node.get("inputs", {}).items():
            if iname not in PROMPT_INPUT_NAMES or not isinstance(value, str):
                continue
            score = 100 if nid in positive_closure else 0
            norm = _normalize_class_name(ctype)
            if norm == "ggtextdisplaycopy":
                score += 16
            elif norm in ("primitivestringmultiline", "primitivestring"):
                score += 14
            elif norm in ("ggcliptext", "ggcliptextencode"):
                score += 10
            elif "textencode" in norm:
                score += 8
            elif norm == "easypositive":
                score += 6
            elif "prompt" in norm:
                score += 2
            if iname in ("positive_prompt", "positive"):
                score += 6
            elif iname == "prompt":
                score += 4
            elif iname == "文本":
                score += 3
            elif iname == "text":
                score += 2
            elif iname == "value":
                score -= 4
            # 多阶段工作流里常有“放大/精修”短指令与主提示词各一条;
            # 同等条件下优先更完整的一条,通常即首阶段主提示词。
            score += min(len(value), 2000) // 250
            prompt_candidates.append((score, len(prompt_candidates), nid, iname))
    if prompt_candidates:
        prompt_candidates.sort(key=lambda c: (-c[0], c[1]))
        _, _, nid, iname = prompt_candidates[0]
        add(f"{nid}.{iname}", label_for(nid, iname), nid, iname)

    # 3) 采样步数
    for nid, node in api.items():
        for iname in node["inputs"]:
            if is_steps_input(iname):
                add(f"{nid}.{iname}", label_for(nid, iname), nid, iname)
                break

    # 4) 处理分辨率
    for nid, node in api.items():
        for iname in node["inputs"]:
            if is_resolution_input(iname) and not is_sampler_or_scheduler(iname):
                add(f"{nid}.{iname}", label_for(nid, iname), nid, iname)
                break

    # 5) 主模型
    for nid, node in api.items():
        for iname in ("unet_name", "ckpt_name", "model_name", "lora_name"):
            if iname in node["inputs"] and node["class_type"] in (
                "UNETLoader", "CheckpointLoaderSimple", "CheckpointLoader",
                "UNETLoaderGGUF", "LoaderGGUF"
            ):
                add(f"{nid}.{iname}", label_for(nid, iname), nid, iname)
                break

    # 6) LoRA
    for nid, node in api.items():
        for iname in node["inputs"]:
            if is_lora_input(iname) and not is_sampler_or_scheduler(iname):
                add(f"{nid}.{iname}", label_for(nid, iname), nid, iname)

    ui = {
        "title": base_title,
        "order": order,
        "hideAdvanced": False,
        "fields": fields,
        "labels": labels,
        "defaults": defaults,
        "category": category,
    }
    return ui


def convert_file(native_path, base_url, category, order):
    native = json.load(open(native_path, encoding="utf-8"))
    try:
        object_info = fetch_object_info(base_url)
    except urllib.error.URLError as e:
        print("无法获取 object_info: %s" % e, file=sys.stderr)
        sys.exit(1)
    api, titles, diagnostics = convert_api(native, object_info)
    ui = derive_ui(api, titles, category, order, native_path)
    base = os.path.splitext(native_path)[0]
    with open(base + ".api.json", "w", encoding="utf-8") as f:
        json.dump(api, f, ensure_ascii=False, indent=2)
    with open(base + ".ui.json", "w", encoding="utf-8") as f:
        json.dump(ui, f, ensure_ascii=False, indent=2)
    return api, ui, diagnostics


def format_diagnostics(diagnostics, limit=6):
    """把诊断压成单行摘要,供批量脚本直接打印。"""
    parts = []
    unresolved = diagnostics.get("unresolved") or []
    if unresolved:
        sample = ", ".join("%s.%s(%s)" % (t, n, r) for _, t, n, r in unresolved[:limit])
        parts.append("未解析输入 %d: %s" % (len(unresolved), sample))
    suspicious = diagnostics.get("suspicious") or []
    if suspicious:
        sample = ", ".join("%s.%s=%r(应为%s)" % (t, n, v, e)
                           for _, t, n, e, v in suspicious[:limit])
        parts.append("可疑值 %d: %s" % (len(suspicious), sample))
    unsupported = diagnostics.get("unsupported") or {}
    if unsupported:
        sample = ", ".join("%s×%d" % (t, c) for t, c in sorted(unsupported.items())[:limit])
        parts.append("未加载节点 %d 类: %s" % (len(unsupported), sample))
    return "; ".join(parts)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("usage: comfy_native_to_api.py <native.json> [--base-url URL] [--category 分类] [--order N]",
              file=sys.stderr)
        sys.exit(2)
    path = sys.argv[1]
    base_url = "http://127.0.0.1:8188"
    category = "我的工作流"
    order = 50
    if "--base-url" in sys.argv:
        base_url = sys.argv[sys.argv.index("--base-url") + 1]
    if "--category" in sys.argv:
        category = sys.argv[sys.argv.index("--category") + 1]
    if "--order" in sys.argv:
        order = int(sys.argv[sys.argv.index("--order") + 1])
    api, ui, diag = convert_file(path, base_url, category, order)
    print("api nodes: %d, ui fields: %d, category=%s title=%s" %
          (len(api), len(ui["fields"]), ui["category"], ui["title"]))
    summary = format_diagnostics(diag)
    if summary:
        print("诊断: " + summary)
