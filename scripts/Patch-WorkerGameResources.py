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


def patch_resource_index(
    worker_global: Path,
    game_global: Path,
    include_game_scene: bool,
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
        worker_shared4_id = external_file_id(worker_file, "game_sharedassets4.assets")
    except RuntimeError:
        external = copy.copy(game_file.externals[external_file_id(game_file, "sharedassets4.assets") - 1])
        external.path = "game_sharedassets4.assets"
        worker_file.externals.append(external)
        worker_shared4_id = len(worker_file.externals)

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

    worker_mono = next(obj for obj in worker_env.objects if obj.type.name == "MonoManager")
    game_mono = next(obj for obj in game_env.objects if obj.type.name == "MonoManager")
    worker_mono_tree = worker_mono.read_typetree()
    game_mono_tree = game_mono.read_typetree()
    for key in ("m_ScriptHashes", "m_RuntimeClassHashes", "m_Scripts"):
        combined = list(game_mono_tree[key])
        known = {repr(item) for item in combined}
        for item in worker_mono_tree[key]:
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


def merge_game_monoscripts(worker_scripts: Path, game_scripts: Path) -> None:
    worker_env = UnityPy.load(str(worker_scripts))
    game_env = UnityPy.load(str(game_scripts))
    worker_file = next(iter(worker_env.files.values()))
    game_file = next(iter(game_env.files.values()))
    worker_script_reader = next(
        obj for obj in worker_file.objects.values() if obj.type.name == "MonoScript"
    )
    worker_type_id = worker_script_reader.type_id
    worker_serialized_type = worker_script_reader.serialized_type
    protected_worker_ids = {4, 6, 8, 9, 10}
    game_script_ids = {
        obj.path_id for obj in game_file.objects.values() if obj.type.name == "MonoScript"
    }
    collision = protected_worker_ids & game_script_ids
    if collision:
        raise RuntimeError(f"Game MonoScript IDs collide with Worker entry scripts: {sorted(collision)}")
    for source in game_file.objects.values():
        if source.type.name != "MonoScript":
            continue
        raw = source.get_raw_data()
        worker_file.objects[source.path_id] = ObjectReader(
            worker_file,
            worker_file.reader,
            source.path_id,
            worker_type_id,
            worker_serialized_type,
            source.class_id,
            source.type,
            0,
            len(raw),
            source.is_destroyed,
            source.is_stripped,
            data=raw,
        )
    worker_scripts.write_bytes(worker_file.save())


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
) -> None:
    env = UnityPy.load(str(source))
    serialized = next(iter(env.files.values()))
    for external in serialized.externals:
        name = Path(external.path).name.lower()
        if name in external_names:
            external.path = external_names[name]
    destination.write_bytes(serialized.save())
    del serialized
    del env
    gc.collect()


def install_particle_prefab_dependencies(worker_data: Path, game_data: Path) -> None:
    copy_serialized_with_external_map(
        game_data / "sharedassets4.assets",
        worker_data / "game_sharedassets4.assets",
        {
            "sharedassets0.assets": "game_sharedassets0.assets",
            "sharedassets1.assets": "game_sharedassets1.assets",
        },
    )
    for source_name, destination_name in (
        ("sharedassets0.assets", "game_sharedassets0.assets"),
        ("sharedassets1.assets", "game_sharedassets1.assets"),
    ):
        os.link(game_data / source_name, worker_data / destination_name)
        for suffix in (".resS", ".resource"):
            source = game_data / f"{source_name}{suffix}"
            if source.exists():
                os.link(source, worker_data / f"{destination_name}{suffix}")
    for suffix in (".resS", ".resource"):
        source = game_data / f"sharedassets4.assets{suffix}"
        if source.exists():
            os.link(source, worker_data / f"sharedassets4.assets{suffix}")


def install_game_scene(worker_data: Path, game_data: Path) -> None:
    worker_shared = worker_data / "sharedassets0.assets"
    worker_shared.rename(worker_data / "worker_sharedassets0.assets")
    copy_serialized_with_external_map(
        worker_data / "level0",
        worker_data / "level0.patched",
        {"sharedassets0.assets": "worker_sharedassets0.assets"},
    )
    os.replace(worker_data / "level0.patched", worker_data / "level0")
    os.link(game_data / "sharedassets0.assets", worker_data / "sharedassets0.assets")
    os.link(game_data / "level4", worker_data / "level1")
    for index in (1, 4):
        name = f"sharedassets{index}.assets"
        os.link(game_data / name, worker_data / name)
        res_s = game_data / f"{name}.resS"
        if res_s.exists():
            os.link(res_s, worker_data / res_s.name)
        resource = game_data / f"sharedassets{index}.resource"
        if resource.exists():
            os.link(resource, worker_data / resource.name)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--worker", required=True, type=Path)
    parser.add_argument("--game-data", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument(
        "--include-game-scene",
        action="store_true",
        help="Diagnostic only: install scnGame as level1. The installed scene currently crashes during preload.",
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
    for source_name in ("resources.assets", "resources.assets.resS", "resources.resource"):
        source = game_data / source_name
        if source.exists():
            os.link(source, output_data / source_name)
    merge_game_monoscripts(
        output_data / "globalgamemanagers.assets",
        game_data / "globalgamemanagers.assets",
    )
    patch_resource_index(
        output_data / "globalgamemanagers",
        game_data / "globalgamemanagers",
        args.include_game_scene,
    )
    link_game_managed(output_data, game_data)
    install_particle_prefab_dependencies(output_data, game_data)
    if args.include_game_scene:
        install_game_scene(output_data, game_data)
    print(output)


if __name__ == "__main__":
    main()
