import os
import sys
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "tools")))
import comfy_native_to_api as converter


class OptionTargetTests(unittest.TestCase):
    def test_wildcard_output_does_not_shift_widget_link_slot(self):
        option = {
            "id": 1,
            "type": "GGOptionConfirm",
            "widgets_values": [12],
            "outputs": [
                {"name": "socket", "type": "*", "links": [100]},
                {"name": "steps", "type": "INT", "widget": {"name": "steps"}, "links": [101]},
            ],
        }
        resolver = type("Resolver", (), {"node_map": {
            1: option,
            2: {"id": 2, "type": "KSampler", "inputs": [{"name": "image", "link": 100}]},
            3: {"id": 3, "type": "KSampler", "inputs": [{"name": "steps", "link": 101}]},
        }})()

        targets = converter.option_entries(option, resolver)

        self.assertEqual(
            [{"ref": "steps", "label": "steps", "slot": 0, "node": "3", "name": "steps"}],
            targets,
        )

    def test_reroute_output_resolves_to_the_downstream_widget(self):
        option = {
            "id": 1,
            "type": "GGOptionConfirm",
            "widgets_values": [12],
            "outputs": [
                {"name": "steps", "type": "INT", "widget": {"name": "steps"}, "links": [100]},
            ],
        }
        reroute = {
            "id": 2,
            "type": "Reroute",
            "inputs": [{"name": "input", "type": "*", "link": 100}],
            "outputs": [{"name": "output", "type": "*", "links": [101]}],
        }
        target = {"id": 3, "type": "KSampler", "inputs": [{"name": "steps", "link": 101}]}
        resolver = type("Resolver", (), {"node_map": {1: option, 2: reroute, 3: target}})()

        self.assertEqual(
            [{"ref": "steps", "label": "steps", "slot": 0, "node": "3", "name": "steps"}],
            converter.option_entries(option, resolver),
        )


class ControllerGroupMetadataTests(unittest.TestCase):
    def test_converter_preserves_controller_group_members_for_api_only_runtime(self):
        derive = getattr(converter, "gg_controller_groups", None)
        self.assertIsNotNone(derive, "converter must emit API-only controller group metadata")
        native = {
            "groups": [{"title": "Save alternatives", "bounding": [0, 0, 100, 100]}],
            "nodes": [
                {"id": 597, "type": "GGGroupControllerS", "pos": [10, 10],
                 "properties": {"selectedGroup": "Save alternatives"}},
                {"id": 595, "type": "SaveImage", "pos": [50, 50]},
                {"id": 596, "type": "PreviewImage", "pos": [100, 100]},
                {"id": 593, "type": "SaveImage", "pos": [150, 150]},
            ],
        }
        api = {
            "1": {"class_type": "GGOptionConfirm", "inputs": {"enabled": False},
                  "_target": {"enabled": ["597", "enabled"]}},
        }

        controller_groups = derive(native, api)
        self.assertEqual(
            [{"optionKey": "597.enabled", "mutedNodes": ["595", "596"]}],
            controller_groups,
        )
        self.assertEqual(
            {
                "fields": ["597.enabled"],
                "options": [{
                    "key": "597.enabled",
                    "label": "enabled",
                    "kind": "value",
                    "choices": [False],
                }],
                "controllerGroups": controller_groups,
            },
            converter.gg_metadata(native, api, {"fields": ["597.enabled"]}),
        )


if __name__ == "__main__":
    unittest.main()
