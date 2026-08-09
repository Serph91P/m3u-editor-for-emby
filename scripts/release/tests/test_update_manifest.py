import importlib.util
import unittest
from pathlib import Path
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "update-manifest.py"
SPEC = importlib.util.spec_from_file_location("update_manifest", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class UpdateManifestTests(unittest.TestCase):
    def test_metadata_uses_new_product_repository_and_preserved_guid(self):
        self.assertEqual("m3u-editor for Emby", MODULE.PLUGIN_NAME)
        self.assertEqual(
            "b7e3c4a1-9f2d-4e8b-a5c6-d1f0e2b3c4a5",
            MODULE.PLUGIN_GUID,
        )
        self.assertEqual("Emby.M3uEditor.Plugin.dll", MODULE.DLL_ASSET_NAME)
        self.assertIn("Serph91P/m3u-editor-for-emby", MODULE.PLUGIN_IMAGE_URL)

    def test_release_fixture_builds_stable_entry_for_new_dll_asset(self):
        release = self._release("v2.4.1", False)

        with patch.object(MODULE, "md5_of_url", return_value="fixture-md5"):
            entry = MODULE.build_version_entry(release, "fixture-token")

        self.assertEqual("2.4.1.0", entry["version"])
        self.assertEqual("fixture-md5", entry["checksum"])
        self.assertEqual(
            "https://github.com/Serph91P/m3u-editor-for-emby/releases/download/"
            "v2.4.1/Emby.M3uEditor.Plugin.dll",
            entry["sourceUrl"],
        )
        self.assertFalse(entry["_prerelease"])

    def test_release_fixture_maps_beta_version_and_asset(self):
        release = self._release("v2.5.0-beta.3", True)

        with patch.object(MODULE, "md5_of_url", return_value="fixture-md5"):
            entry = MODULE.build_version_entry(release, "fixture-token")

        self.assertEqual("2.5.0.3", entry["version"])
        self.assertTrue(entry["_prerelease"])
        self.assertEqual("Emby.M3uEditor.Plugin.dll", MODULE.find_dll_asset(release)["name"])

    def test_manifest_fixture_exposes_new_metadata(self):
        manifest = MODULE.build_manifest(
            [
                {
                    "version": "2.4.1.0",
                    "checksum": "fixture-md5",
                    "_semver": "2.4.1",
                    "_prerelease": False,
                }
            ]
        )[0]

        self.assertEqual("m3u-editor for Emby", manifest["name"])
        self.assertEqual(MODULE.PLUGIN_GUID, manifest["guid"])
        self.assertNotIn("_semver", manifest["versions"][0])
        self.assertNotIn("_prerelease", manifest["versions"][0])

    def test_channel_partition_keeps_stable_clean_and_beta_complete(self):
        stable_entry = {"version": "2.4.1.0", "_prerelease": False}
        beta_entry = {"version": "2.5.0.3", "_prerelease": True}

        stable, beta = MODULE.partition_versions([beta_entry, stable_entry])

        self.assertEqual([stable_entry], stable)
        self.assertEqual([beta_entry, stable_entry], beta)

    @staticmethod
    def _release(tag, prerelease):
        asset_name = "Emby.M3uEditor.Plugin.dll"
        return {
            "tag_name": tag,
            "body": "Fixture release",
            "prerelease": prerelease,
            "published_at": "2026-08-08T00:00:00Z",
            "assets": [
                {
                    "name": asset_name,
                    "url": "https://api.github.com/assets/1",
                    "browser_download_url": (
                        "https://github.com/Serph91P/m3u-editor-for-emby/releases/"
                        f"download/{tag}/{asset_name}"
                    ),
                }
            ],
        }


if __name__ == "__main__":
    unittest.main()
