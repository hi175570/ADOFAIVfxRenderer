import argparse
import copy
import gc
import json
import os
import shutil
from pathlib import Path

import UnityPy
from UnityPy.files.ObjectReader import ObjectReader


def external_file_id(serialized_file, name: str) -> int:
    for index, external in enumerate(serialized_file.externals, start=1):
        if Path(external.path).name.lower() == name.lower():
            return index
    raise RuntimeError(f"{name} is not an external of {serialized_file.name}")


def remap_pptr_file_ids(value, file_ids: dict[int, int]) -> None:
    if isinstance(value, dict):
        if "m_FileID" in value and "m_PathID" in value:
            value["m_FileID"] = file_ids.get(value["m_FileID"], value["m_FileID"])
            return
        for child in value.values():
            remap_pptr_file_ids(child, file_ids)
    elif isinstance(value, list):
        for child in value:
            remap_pptr_file_ids(child, file_ids)


def patch_resource_index(
    worker_global: Path,
    game_global: Path,
    include_game_scene: bool,
    worker_script_ids: dict[int, int],
) -> None:
    worker_env = UnityPy.load(str(worker_global))
    game_env = UnityPy.load(str(game_global))
    worker_file = next(iter(worker_env.files.values()))
    game_file = next(iter(game_env.files.values()))
    for external in worker_file.externals:
        name = Path(external.path).name.lower()
        if name == "resources.assets":
            external.path = "worker_resources.assets"
        elif name == "sharedassets0.assets":
            external.path = "worker_sharedassets0.assets"
    try:
        worker_resources_id = external_file_id(worker_file, "resources.assets")
    except RuntimeError:
        external = copy.copy(worker_file.externals[external_file_id(worker_file, "worker_resources.assets") - 1])
        external.path = "resources.assets"
        worker_file.externals.append(external)
        worker_resources_id = len(worker_file.externals)
    game_resources_id = external_file_id(game_file, "resources.assets")
    try:
        worker_shared4_id = external_file_id(worker_file, "sharedassets4.assets")
    except RuntimeError:
        external = copy.copy(game_file.externals[external_file_id(game_file, "sharedassets4.assets") - 1])
        worker_file.externals.append(external)
        worker_shared4_id = len(worker_file.externals)
    active_global_assets_id = external_file_id(worker_file, "globalgamemanagers.assets")
    game_to_worker_file_ids: dict[int, int] = {0: 0}
    for game_file_id, game_external in enumerate(game_file.externals, start=1):
        name = Path(game_external.path).name
        try:
            worker_file_id = external_file_id(worker_file, name)
        except RuntimeError:
            worker_file.externals.append(copy.copy(game_external))
            worker_file_id = len(worker_file.externals)
        game_to_worker_file_ids[game_file_id] = worker_file_id

    worker_manager = next(obj for obj in worker_env.objects if obj.type.name == "ResourceManager")
    game_manager = next(obj for obj in game_env.objects if obj.type.name == "ResourceManager")
    worker_tree = worker_manager.read_typetree()
    game_tree = game_manager.read_typetree()
    existing = {
        (name, pointer["m_FileID"], pointer["m_PathID"])
        for name, pointer in worker_tree["m_Container"]
    }
    for name, pointer in game_tree["m_Container"]:
        if pointer["m_FileID"] != game_resources_id:
            continue
        translated = (name, worker_resources_id, pointer["m_PathID"])
        if translated in existing:
            continue
        worker_tree["m_Container"].append(
            (name, {"m_FileID": worker_resources_id, "m_PathID": pointer["m_PathID"]})
        )
        existing.add(translated)

    # VideoBloom is referenced by the game scene rather than Resources.Load.
    # Give the exact installed shader a private lookup key for the standalone worker.
    synthetic_resources = {
        "adofai_videobloom": 2797,
        "adofai_floormeshdefault": 29,
        "adofai_floormeshlong": 5920,
        "adofai_camera": 5936,
    }
    for name, path_id in synthetic_resources.items():
        entry = (name, worker_resources_id, path_id)
        if entry in existing:
            continue
        worker_tree["m_Container"].append(
            (name, {"m_FileID": worker_resources_id, "m_PathID": path_id})
        )
    for name, path_id in {
        "adofai_scrollingsprite": 4,
        "adofai_holdshader": 5,
        "adofai_tileblur": 6,
        "adofai_overlay": 7,
        "adofai_polarscreen": 8,
        "adofai_screentile": 9,
        "adofai_screenscroll": 10,
    }.items():
        entry = (name, active_global_assets_id, path_id)
        if entry not in existing:
            worker_tree["m_Container"].append(
                (name, {"m_FileID": active_global_assets_id, "m_PathID": path_id})
            )
    particle_entry = ("adofai_particledecoration", worker_shared4_id, 173)
    if particle_entry not in existing:
        worker_tree["m_Container"].append(
            (particle_entry[0], {"m_FileID": particle_entry[1], "m_PathID": particle_entry[2]})
        )
    tile_shader_entry = ("adofai_spritestile", worker_shared4_id, 23)
    if tile_shader_entry not in existing:
        worker_tree["m_Container"].append(
            (tile_shader_entry[0], {"m_FileID": tile_shader_entry[1], "m_PathID": tile_shader_entry[2]})
        )

    worker_manager.save_typetree(worker_tree)

    # Layer and sorting-layer IDs are serialized in TagManager, not in the
    # managed assemblies. Decorations use the game's "Bg" sorting layer while
    # tracks use "Default"; keeping the Worker's default TagManager collapses
    # both into one layer and changes the draw order.
    worker_tag_manager = next(obj for obj in worker_env.objects if obj.type.name == "TagManager")
    game_tag_manager = next(obj for obj in game_env.objects if obj.type.name == "TagManager")
    worker_tag_manager.save_typetree(game_tag_manager.read_typetree())

    for manager_type in ("GraphicsSettings", "QualitySettings"):
        worker_manager_object = next(obj for obj in worker_env.objects if obj.type.name == manager_type)
        game_manager_object = next(obj for obj in game_env.objects if obj.type.name == manager_type)
        manager_tree = game_manager_object.read_typetree()
        remap_pptr_file_ids(manager_tree, game_to_worker_file_ids)
        worker_manager_object.save_typetree(manager_tree)

    worker_mono = next(obj for obj in worker_env.objects if obj.type.name == "MonoManager")
    game_mono = next(obj for obj in game_env.objects if obj.type.name == "MonoManager")
    worker_mono_tree = worker_mono.read_typetree()
    game_mono_tree = game_mono.read_typetree()
    for key in ("m_ScriptHashes", "m_RuntimeClassHashes", "m_Scripts"):
        combined = list(game_mono_tree[key])
        known = {repr(item) for item in combined}
        for item in worker_mono_tree[key]:
            if key == "m_Scripts" and item["m_FileID"] == active_global_assets_id:
                old_path_id = item["m_PathID"]
                if old_path_id in worker_script_ids:
                    item = dict(item)
                    item["m_PathID"] = worker_script_ids[old_path_id]
            marker = repr(item)
            if marker not in known:
                combined.append(item)
                known.add(marker)
        worker_mono_tree[key] = combined
    worker_mono.save_typetree(worker_mono_tree)
    if include_game_scene:
        worker_build_settings = next(
            obj for obj in worker_env.objects if obj.type.name == "BuildSettings"
        )
        build_tree = worker_build_settings.read_typetree(check_read=False)
        if "Assets/scenes/scnGame.unity" not in build_tree["scenes"]:
            build_tree["scenes"].append("Assets/scenes/scnGame.unity")
        worker_build_settings.save_typetree(build_tree)
    worker_global.write_bytes(worker_file.save())


def install_game_global_assets(worker_scripts: Path, game_scripts: Path) -> dict[int, int]:
    worker_env = UnityPy.load(str(worker_scripts))
    game_env = UnityPy.load(str(game_scripts))
    worker_file = next(iter(worker_env.files.values()))
    game_file = next(iter(game_env.files.values()))
    game_script_reader = next(
        obj for obj in game_file.objects.values() if obj.type.name == "MonoScript"
    )
    next_path_id = max(game_file.objects) + 1
    relocated: dict[int, int] = {}
    for source in worker_file.objects.values():
        if source.type.name != "MonoScript":
            continue
        path_id = next_path_id
        next_path_id += 1
        relocated[source.path_id] = path_id
        raw = source.get_raw_data()
        game_file.objects[path_id] = ObjectReader(
            game_file,
            game_file.reader,
            path_id,
            game_script_reader.type_id,
            game_script_reader.serialized_type,
            source.class_id,
            source.type,
            0,
            len(raw),
            source.is_destroyed,
            source.is_stripped,
            data=raw,
        )
    worker_scripts.write_bytes(game_file.save())
    return relocated


def link_game_managed(worker_data: Path, game_data: Path) -> None:
    worker_managed = worker_data / "Managed"
    game_managed = game_data / "Managed"
    worker_managed.mkdir(parents=True, exist_ok=True)
    existing = {path.name.lower() for path in worker_managed.glob("*.dll")}
    for source in game_managed.glob("*.dll"):
        if source.name.lower() in existing:
            continue
        os.link(source, worker_managed / source.name)
        existing.add(source.name.lower())
    worker_scripting = worker_data / "ScriptingAssemblies.json"
    game_scripting = game_data / "ScriptingAssemblies.json"
    worker_table = json.loads(worker_scripting.read_text(encoding="utf-8-sig"))
    game_table = json.loads(game_scripting.read_text(encoding="utf-8-sig"))
    known_names = {name.lower() for name in worker_table["names"]}
    for name, assembly_type in zip(game_table["names"], game_table["types"]):
        if name.lower() in known_names:
            continue
        worker_table["names"].append(name)
        worker_table["types"].append(assembly_type)
        known_names.add(name.lower())
    worker_scripting.write_text(
        json.dumps(worker_table, separators=(",", ":")),
        encoding="utf-8",
    )


def copy_serialized_with_external_map(
    source: Path,
    destination: Path,
    external_names: dict[str, str],
    inactive_game_objects: set[str] | None = None,
) -> None:
    env = UnityPy.load(str(source))
    serialized = next(iter(env.files.values()))
    for external in serialized.externals:
        name = Path(external.path).name.lower()
        if name in external_names:
            external.path = external_names[name]
    if inactive_game_objects:
        for obj in serialized.objects.values():
            if obj.type.name != "GameObject":
                continue
            tree = obj.read_typetree()
            if tree.get("m_Name") not in inactive_game_objects:
                continue
            tree["m_IsActive"] = 0
            obj.save_typetree(tree)
    destination.write_bytes(serialized.save())
    del serialized
    del env
    gc.collect()


def install_particle_prefab_dependencies(worker_data: Path, game_data: Path) -> None:
    for source_name in ("sharedassets0.assets", "sharedassets1.assets", "sharedassets4.assets"):
        os.link(game_data / source_name, worker_data / source_name)
        stream_names = (f"{source_name}.resS", f"{Path(source_name).stem}.resource")
        for stream_name in stream_names:
            source = game_data / stream_name
            if source.exists():
                os.link(source, worker_data / stream_name)


def install_native_dependencies(worker_data: Path, game_data: Path) -> None:
    source = game_data / "Plugins" / "x86_64" / "skyhook.dll"
    if not source.exists():
        return
    destination_directory = worker_data / "Plugins" / "x86_64"
    destination_directory.mkdir(parents=True, exist_ok=True)
    os.link(source, destination_directory / source.name)


def install_game_scene(worker_data: Path, game_data: Path) -> None:
    copy_serialized_with_external_map(
        game_data / "level4",
        worker_data / "level1",
        {},
        {
            "Controller", "scnGame", "Conductor", "Vfx", "Decoration Container", "CamParent",
            "Level Maker", "GUI", "PlayerManager", "TutorialBackground1", "WorldCanvas",
            "CustomBackgroundSprite",
        },
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--worker", required=True, type=Path)
    parser.add_argument("--game-data", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument(
        "--include-game-scene",
        action="store_true",
        help="Install the game's scnGame scene as level1 for the independent original-runtime renderer.",
    )
    args = parser.parse_args()

    worker = args.worker.resolve()
    game_data = args.game_data.resolve()
    output = args.output.resolve()
    if not worker.is_dir() or not (worker / "ADOFAI VFX Worker_Data").is_dir():
        raise RuntimeError(f"Invalid worker directory: {worker}")
    if not (game_data / "resources.assets").is_file():
        raise RuntimeError(f"Invalid game data directory: {game_data}")

    output.mkdir(parents=True, exist_ok=True)
    shutil.copytree(worker, output, dirs_exist_ok=True)
    output_data = output / "ADOFAI VFX Worker_Data"
    (output_data / "resources.assets").rename(output_data / "worker_resources.assets")
    (output_data / "sharedassets0.assets").rename(output_data / "worker_sharedassets0.assets")
    worker_shared_stream = output_data / "sharedassets0.assets.resS"
    if worker_shared_stream.exists():
        worker_shared_stream.rename(output_data / "worker_sharedassets0.assets.resS")
    copy_serialized_with_external_map(
        output_data / "worker_resources.assets",
        output_data / "worker_resources.assets.patched",
        {"sharedassets0.assets": "worker_sharedassets0.assets"},
    )
    os.replace(
        output_data / "worker_resources.assets.patched",
        output_data / "worker_resources.assets",
    )
    os.link(game_data / "resources.assets", output_data / "resources.assets")
    for source_name in ("resources.assets.resS", "resources.resource"):
        source = game_data / source_name
        if source.exists():
            os.link(source, output_data / source_name)
    worker_script_ids = install_game_global_assets(
        output_data / "globalgamemanagers.assets",
        game_data / "globalgamemanagers.assets",
    )
    game_global_stream = game_data / "globalgamemanagers.assets.resS"
    output_global_stream = output_data / "globalgamemanagers.assets.resS"
    if output_global_stream.exists():
        output_global_stream.unlink()
    if game_global_stream.exists():
        os.link(game_global_stream, output_global_stream)
    patch_resource_index(
        output_data / "globalgamemanagers",
        game_data / "globalgamemanagers",
        args.include_game_scene,
        worker_script_ids,
    )
    link_game_managed(output_data, game_data)
    install_particle_prefab_dependencies(output_data, game_data)
    install_native_dependencies(output_data, game_data)
    if args.include_game_scene:
        install_game_scene(output_data, game_data)
    print(output)


if __name__ == "__main__":
    main()
