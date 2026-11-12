#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 3 ]]; then
    printf 'Usage: %s SOURCE_ARCHIVE EDITED_SOURCE BUILD_DIRECTORY\n' "$0" >&2
    exit 2
fi

archive=$(realpath "$1")
edited_source=$(realpath "$2")
build_root=$(realpath -m "$3")
mkdir -p "$build_root/source"
archive_hash=$(sha256sum "$archive" | cut -d ' ' -f 1)
if [[ ! -f "$build_root/source-archive.sha256" ]]; then
    tar -xf "$archive" -C "$build_root/source"
    sed -i '/const auto prepare_record =/,/^    };/s/\bgraphics_key\b/prepared_graphics_key/g' \
        "$build_root/source/src/video_core/renderer_vulkan/vk_pipeline_cache.cpp"
    printf '%s\n' "$archive_hash" > "$build_root/source-archive.sha256"
elif [[ $(<"$build_root/source-archive.sha256") != "$archive_hash" ]]; then
    printf 'Choose a new build directory for a different source archive.\n' >&2
    exit 2
fi

console_sources=(
    src/core/core.cpp
    src/core/loader/nx.cpp
    src/core/loader/nx.h
    src/core/perf_stats.cpp
    src/core/perf_stats.h
    src/video_core/CMakeLists.txt
    src/video_core/nx2_precompile.cpp
    src/video_core/host1x/nx2_video_decoder.cpp
    src/video_core/jinra_capture_buffer.h
    src/video_core/jinra_gameplay_capture.h
    src/video_core/vulkan_common/ngx_dlss_device.cpp
    src/video_core/vulkan_common/ngx_dlss_device.h
    src/video_core/vulkan_common/vulkan_device.cpp
    src/video_core/vulkan_common/vulkan_device.h
    src/video_core/vulkan_common/vulkan_instance.cpp
    src/video_core/vulkan_common/vulkan_instance.h
    src/video_core/renderer_vulkan/renderer_vulkan.cpp
    src/video_core/renderer_vulkan/renderer_vulkan.h
    src/video_core/renderer_vulkan/vk_rasterizer.cpp
    src/video_core/renderer_vulkan/vk_ngx_dlss.cpp
    src/video_core/renderer_vulkan/vk_ngx_dlss.h
    src/video_core/renderer_vulkan/vk_ocarina_camera_motion.cpp
    src/yuzu/CMakeLists.txt
    src/yuzu/configuration/configure_graphics.cpp
    src/yuzu/jinra_avatar.cpp
    src/yuzu/jinra_console.h
    src/yuzu/jinra_console_dialog.h
    src/yuzu/jinra_recording.cpp
    src/yuzu/jinra_home.cpp
    src/yuzu/jinra_home.h
    src/yuzu/main.cpp
    src/yuzu/main_window.cpp
    src/yuzu/main_window.h
    src/yuzu/game/game_list.cpp
    src/yuzu/game/game_list.h
    src/yuzu/game/shader_sync.cpp
    src/yuzu/render/performance_overlay.cpp
    src/yuzu/render/performance_overlay.h
    src/yuzu/util/controller_navigation.cpp
)
for file in "${console_sources[@]}"; do
    if ! cmp -s "$edited_source/$file" "$build_root/source/$file"; then
        cp "$edited_source/$file" "$build_root/source/$file"
    fi
done

cache_module=CMakeModules/JinraShaderCache.cmake
if ! cmp -s "$edited_source/$cache_module" "$build_root/source/$cache_module"; then
    cp "$edited_source/$cache_module" "$build_root/source/$cache_module"
fi
# Keep the console renderer baseline and sync the shared pipeline preparation changes.
python3 - "$edited_source" "$build_root/source" <<'PY'
from pathlib import Path
import sys

relative = 'src/video_core/renderer_vulkan/vk_pipeline_cache.cpp'
source, target = (Path(path) / relative for path in sys.argv[1:])
reference = source.read_text()
previous = target.read_text()
updated = previous

regions = (
    ('    const auto prepare_begin = std::chrono::steady_clock::now();\n',
     '    const auto temporary = base_dir / fmt::format("shader-prep.'),
    ('        LogDriverCache(device, "after shader preparation");\n    }\n',
     '    if (skipped.load() != 0) {'),
    ('    rejected_prepared_compute.clear();\n', '    pipeline_cache_filename =\n'),
    ('            LoadVulkanPipelineCache(vulkan_pipeline_cache_filename, CACHE_VERSION);\n    }\n',
     '    struct {\n        std::mutex mutex;'),
    ('    GetGraphicsEnvironments(environments, graphics_key.unique_hashes);\n',
     '    if (!pipeline || pipeline_cache_filename.empty()) {'),
    ('    env.KeepStoragePointers(keep_pointers);\n',
     '    if (const char* directory = std::getenv("EDEN_NX2_SHADER_DUMP");'),
)
for start, end in regions:
    first = updated.index(start) + len(start)
    last = updated.index(end, first)
    ref_first = reference.index(start) + len(start)
    ref_last = reference.index(end, ref_first)
    updated = updated[:first] + reference[ref_first:ref_last] + updated[last:]

marker = '#if defined(__linux__)\n    // The OS updater can identify'
anchor = '    if (std::filesystem::exists(kept_file)) {\n        LOG_INFO(Render_Vulkan, "Prepared shader cache needs rebuilding: {}", reason);'
begin = updated.find(marker)
end = updated.index(anchor)
updated = (updated[:end if begin == -1 else begin]
           + reference[reference.index(marker):reference.index(anchor)] + updated[end:])
if updated != previous:
    target.write_text(updated)
PY

cmake -S "$build_root/source" -B "$build_root/build" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_C_COMPILER=clang -DCMAKE_CXX_COMPILER=clang++ \
    -DYUZU_TESTS=OFF -DDYNARMIC_TESTS=OFF -DYUZU_USE_BUNDLED_QT=OFF \
    -DYUZU_USE_QT_WEB_ENGINE=OFF -DENABLE_UPDATE_CHECKER=OFF \
    "-DJINRA_NGX_SDK=$build_root/nvidia-dlss"

cmake --build "$build_root/build" --target yuzu --parallel "${JINRA_BUILD_JOBS:-8}" -- -k 0
install -Dm755 "$build_root/build/bin/jinra" "$build_root/stage/usr/bin/jinra"
