# Server game objects (bridge-v103): models the page lists for the open map (doors, chests, signs
# and the other objects the game server places, which are in no ADT or WMO), drawn like placed
# scenery while Scene > Objects is on. Each one comes with a world position, a quaternion and a
# scale, so it is placed the way a WMO places its doodads (no MDDF Euler angles involved).
# Invoked by patch-viewer3d-source.ps1 after every other patch, so its anchors are the final
# text; Replace-Exact and $source belong to that script's scope.
$mainCpp = Join-Path $source 'emscripten_port/src/main.cpp'
$configH = Join-Path $source 'wowViewerLib/src/include/config.h'
$mapH = Join-Path $source 'wowViewerLib/src/engine/objects/scenes/map.h'
$mapCpp = Join-Path $source 'wowViewerLib/src/engine/objects/scenes/map.cpp'
$wmoSceneCpp = Join-Path $source 'wowViewerLib/src/engine/objects/scenes/wmoScene.cpp'

if (-not (Get-Content -LiteralPath $configH -Raw).Contains('bool viewerObjectsEnabled')) {
    Replace-Exact $configH '    ViewerBeforeState viewerBefore;' @'
    ViewerBeforeState viewerBefore;
    // Server game objects (bridge-v103): drawn while set (Scene > Objects).
    bool viewerObjectsEnabled = false;
'@
}

if (-not (Get-Content -LiteralPath $mapH -Raw).Contains('viewerServerObjects')) {
    Replace-Exact $mapH '    void viewerApplyBefore(const mathfu::vec4 &cameraPos, std::vector<std::shared_ptr<M2Object>> &m2,' @'
    // Server game objects (bridge-v103). i, j: the ADT tile (mapTiles index) the object stands on.
    struct ViewerServerObject { std::shared_ptr<M2Object> m2; int i, j; };
    std::vector<ViewerServerObject> viewerServerObjects;
    // rows: count x 10 doubles [fid, x, y, z, qx, qy, qz, qw, scale, id]: an M2 by file ID at a
    // world position (x north, y west, z up) with a rotation quaternion and a scale; id comes
    // back as the picked placement's uniqueId. Replaces the list. Returns the count kept.
    uint32_t viewerSetObjects(const double *rows, uint32_t count) {
        viewerServerObjects.clear();
        mathfu::mat4 world = mathfu::mat4::Identity();
        for (uint32_t r = 0; r < count; ++r) {
            const double *v = rows + size_t(r) * 10;
            bool finite = true;
            for (int k = 0; k < 10; ++k) finite = finite && std::isfinite(v[k]);
            if (!finite || v[0] < 1 || v[0] > 2147483647.0 || v[8] <= 0 || v[9] < 0 || v[9] > 4294967295.0) continue;
            SMODoodadDef def{};
            def.position = mathfu::vec3_packed(mathfu::vec3(float(v[1]), float(v[2]), float(v[3])));
            def.orientation = mathfu::vec4_packed(mathfu::vec4(float(v[4]), float(v[5]), float(v[6]), float(v[7])));
            def.scale = float(v[8]);
            auto m2 = std::make_shared<M2Object>(m_api);
            m2->setLoadParams(0, {}, {});
            m2->setModelFileId(int(v[0]));
            m2->createPlacementMatrix(def, world);
            m2->calcWorldPosition();
            m2->viewerUniqueId = uint32_t(v[9]);
            viewerServerObjects.push_back({m2, worldCoordinateToAdtIndex(float(v[2])), worldCoordinateToAdtIndex(float(v[1]))});
        }
        return uint32_t(viewerServerObjects.size());
    }
    // Per frame, while Objects is on: adds the objects to the M2 candidates. On a terrain map those
    // on resident tiles, out to the terrain's view distance, not the M2 one: there are few of them,
    // and with M2 at 1x1 one a few yards past a tile edge would vanish. A WMO-only scene has no
    // tiles and takes them all.
    void viewerApplyObjects(const mathfu::vec4 &cameraPos, std::vector<std::shared_ptr<M2Object>> &m2, bool byTile) {
        auto config = m_api->getConfig();
        if (!config->viewerObjectsEnabled || !config->renderM2) return;
        for (auto &object : viewerServerObjects) {
            if (byTile && (object.i < 0 || object.i >= 64 || object.j < 0 || object.j >= 64 || mapTiles[object.i][object.j] == nullptr)) continue;
            m2.push_back(object.m2);
        }
    }
    void viewerApplyBefore(const mathfu::vec4 &cameraPos, std::vector<std::shared_ptr<M2Object>> &m2,
'@
}

if (-not (Get-Content -LiteralPath $mapCpp -Raw).Contains('viewerApplyObjects(cameraPos, m2ObjectsCandidates, true);')) {
    Replace-Exact $mapCpp '    viewerApplyBefore(cameraPos, m2ObjectsCandidates, wmoCandidates);' @'
    viewerApplyBefore(cameraPos, m2ObjectsCandidates, wmoCandidates);
    viewerApplyObjects(cameraPos, m2ObjectsCandidates, true);
'@
}
if (-not (Get-Content -LiteralPath $mapCpp -Raw).Contains('viewerApplyObjects(cameraPos, m2, true);')) {
    Replace-Exact $mapCpp '    viewerApplyBefore(cameraPos, m2, wmo);' @'
    viewerApplyBefore(cameraPos, m2, wmo);
    viewerApplyObjects(cameraPos, m2, true);
'@
}

if (-not (Get-Content -LiteralPath $wmoSceneCpp -Raw).Contains('viewerApplyObjects(cameraPos, m2ObjectsCandidates, false);')) {
    Replace-Exact $wmoSceneCpp @'
    wmoCandidates.push_back(viewerShownWmo());
};
'@ @'
    wmoCandidates.push_back(viewerShownWmo());
    viewerApplyObjects(cameraPos, m2ObjectsCandidates, false);
};
'@
}

if (-not (Get-Content -LiteralPath $mainCpp -Raw).Contains('int viewerSetObjects(')) {
    Replace-Exact $mainCpp @'
    // WMO-only scenes: the base build's root WMO by file ID for the in-place Before view (0
'@ @'
    // Server game objects (bridge-v103). rows: count x 10 doubles [fid, x, y, z, qx, qy, qz, qw,
    // scale, id] (see Map::viewerSetObjects). Replaces the list of the open map; nothing is drawn
    // until viewerSetObjectsEnabled(1). Returns the objects kept, or -1 without a map.
    EMSCRIPTEN_KEEPALIVE
    int viewerSetObjects(const double *rows, uint32_t count) {
        auto map = std::dynamic_pointer_cast<Map>(currentScene);
        if (!map || !apiContainer || count > 65536 || (count && !rows)) return -1;
        return int(map->viewerSetObjects(rows, count));
    }
    // Draws the listed objects (1) or not (0), from the next frame.
    EMSCRIPTEN_KEEPALIVE
    int viewerSetObjectsEnabled(int enabled) {
        if (!apiContainer) return 0;
        apiContainer->getConfig()->viewerObjectsEnabled = enabled != 0;
        return 1;
    }
    // WMO-only scenes: the base build's root WMO by file ID for the in-place Before view (0
'@
}
