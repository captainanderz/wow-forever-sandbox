// The live map window's own script, added by ForeverCapture's local map server (LocalMap.cs) to the WoW Atlas 2D map it
// serves from the site's folder. The site itself has no part in it: this only reads the site's page globals (map, L,
// current_map, min_x, min_y, Viewer3DCoordinates, MapUI) and draws on top.
// "Captured" (a row added to the map library, on by default): every capture's game objects as dots and the character's
// paths, from /__local/coverage/<mapId>.json, which the server builds from the capture folders.
// Live (?live=1): /__local/live.json, which a recording rewrites every few seconds, adds this session's objects and path,
// where the character is and the ring the server sends objects within. The window follows the character: onto the map it
// is on, and along when it walks out of view.
(function () {
    'use strict';
    var STORAGE_KEY = 'forevercapture.captured.v1', LIVE_MS = 2000;
    // Yards per Leaflet unit: one ADT (1600/3 yd) is half a unit.
    var UNIT_YARDS = 3200 / 3;
    // Most objects arrive within about this distance: 90% of the first capture's were first seen within ~200 yd of the
    // path (a few large ones from up to 600).
    var RING_YARDS = 200;
    var PATH = { color: '#35e0ff', weight: 3, opacity: 0.95, interactive: false };
    var PATH_EDGE = { color: '#041418', weight: 6, opacity: 0.55, interactive: false };
    var SPOT = { radius: 2.5, stroke: false, fillColor: '#ffd24a', fillOpacity: 0.9, interactive: false };
    var YOU = { radius: 6, color: '#ffffff', weight: 2, fillColor: '#35e0ff', fillOpacity: 1, interactive: false };
    var RING = { color: '#35e0ff', weight: 1.5, opacity: 0.8, dashArray: '6 6', fill: true, fillOpacity: 0.06, interactive: false };
    var shown = null, layer = null, row = null, input = null, token = 0, renderer = null;
    var liveLayer = null, liveFollowing = false, liveVersions = null, selected = null;

    function live() { return new URLSearchParams(location.search).get('live') === '1'; }
    function mapReady() { return typeof map !== 'undefined' && !!map && typeof map.addLayer === 'function'; }
    function canDraw() { return mapReady() && typeof L !== 'undefined' && typeof Viewer3DCoordinates !== 'undefined' && typeof min_x !== 'undefined'; }
    function flatToPoints(flat) {
        var points = [];
        if (Array.isArray(flat)) for (var i = 0; i + 1 < flat.length; i += 2) if (Number.isFinite(flat[i]) && Number.isFinite(flat[i + 1])) points.push([flat[i], flat[i + 1]]);
        return points;
    }
    /** A coverage answer read: objects as [x, y] and each stretch of the path as [[x, y], ...]. Null when it is not one. */
    function parse(doc) {
        if (!doc || doc.schemaVersion !== 1 || doc.generator !== 'coverage/1' || !Array.isArray(doc.objects) || !Array.isArray(doc.tracks)) return null;
        var spot = function (p) { return Array.isArray(p) && p.length >= 2 && Number.isFinite(p[0]) && Number.isFinite(p[1]); };
        return { objects: doc.objects.filter(spot), tracks: doc.tracks.map(flatToPoints).filter(function (points) { return points.length > 0; }) };
    }
    /** A live file read, for one map: this session's objects and path there, and where the character is (null elsewhere). */
    function parseLive(doc, mapId) {
        if (!doc || doc.schemaVersion !== 1 || doc.generator !== 'live/1') return null;
        var objects = (Array.isArray(doc.objects) ? doc.objects : []).filter(function (o) {
            return Array.isArray(o) && o[0] === mapId && Number.isFinite(o[1]) && Number.isFinite(o[2]);
        }).map(function (o) { return [o[1], o[2]]; });
        var tracks = (Array.isArray(doc.tracks) ? doc.tracks : []).filter(function (t) { return t && t.map === mapId; })
            .map(function (t) { return flatToPoints(t.points); }).filter(function (points) { return points.length > 0; });
        var position = doc.map === mapId && Array.isArray(doc.position) && Number.isFinite(doc.position[0]) && Number.isFinite(doc.position[1]) ? [doc.position[0], doc.position[1]] : null;
        return { map: Number.isInteger(doc.map) ? doc.map : null, client: String(doc.client || ''), objects: objects, tracks: tracks, position: position };
    }
    /** The map list entry for a map the character is on: the product's entry made from the client's build, else its newest. */
    function entryFor(versions, product, mapId, client) {
        var number = function (build) { var m = /^WOW-(\d+)/.exec(String(build || '')); return m ? Number(m[1]) : 0; };
        var clientBuild = /\.(\d+)$/.exec(client || '');
        var matches = Object.values(versions || {}).filter(function (e) { return e && e.map && e.map.product === product && e.map.ID === mapId; })
            .sort(function (a, b) { return number(a.BuildId) - number(b.BuildId); });
        return matches.find(function (e) { return clientBuild && number(e.BuildId) === Number(clientBuild[1]); }) || matches[matches.length - 1] || null;
    }
    if (typeof module !== 'undefined' && module.exports) { module.exports = { parse: parse, parseLive: parseLive, entryFor: entryFor, RING_YARDS: RING_YARDS }; return; }

    function on() {
        try { return localStorage.getItem(STORAGE_KEY) !== 'false'; } catch (_) { return true; }
    }
    function latLng(p) {
        var spot = Viewer3DCoordinates.worldToLeaflet(p[0], p[1], Number(min_x), Number(min_y));
        return [spot.lat, spot.lng];
    }
    /** Dots and path stretches into a group, on the one canvas (a continent can hold tens of thousands of objects). */
    function marks(group, data) {
        renderer = renderer || L.canvas({ padding: 0.5 });
        data.objects.forEach(function (p) { group.addLayer(L.circleMarker(latLng(p), Object.assign({ renderer: renderer }, SPOT))); });
        data.tracks.forEach(function (points) {
            var line = points.map(latLng);
            // A stretch of one point (a login that never moved) still shows where it was.
            if (line.length === 1) { group.addLayer(L.circleMarker(line[0], Object.assign({ renderer: renderer }, SPOT, { radius: 4, fillColor: PATH.color }))); return; }
            group.addLayer(L.polyline(line, Object.assign({ renderer: renderer }, PATH_EDGE)));
            group.addLayer(L.polyline(line, Object.assign({ renderer: renderer }, PATH)));
        });
    }
    function remove() {
        if (layer && mapReady() && map.hasLayer(layer)) map.removeLayer(layer);
        layer = null;
    }
    function draw() {
        remove();
        if (!shown || !shown.data || !canDraw()) return;
        var group = L.layerGroup();
        try { marks(group, shown.data); } catch (_) { return; }   // the map's own tile range is not known yet: the next select draws
        layer = group.addTo(map);
    }

    function drawLive(data) {
        if (!canDraw()) return;
        var group = L.layerGroup(), you = null;
        try {
            marks(group, data);
            if (data.position) {
                you = latLng(data.position);
                group.addLayer(L.circle(you, Object.assign({ renderer: renderer, radius: RING_YARDS / UNIT_YARDS }, RING)));
                group.addLayer(L.circleMarker(you, Object.assign({ renderer: renderer }, YOU)));
            }
        } catch (_) { return; }
        if (liveLayer && map.hasLayer(liveLayer)) map.removeLayer(liveLayer);
        liveLayer = group.addTo(map);
        if (!you) return;
        // The first position centres the window at a scale that shows the ring; after that it only moves when the
        // character walks out of the middle of the view, so dragging around to look stays possible.
        if (!liveFollowing) { map.setView(you, Math.max(map.getZoom(), 9), { animate: false }); liveFollowing = true; }
        else if (!map.getBounds().pad(-0.2).contains(you)) map.panTo(you);
    }
    async function liveTick() {
        var product = new URLSearchParams(location.search).get('product') || 'wow_classic_beta', doc = null;
        try {
            var response = await fetch('/__local/live.json', { cache: 'no-store' });
            if (response.ok) doc = await response.json();
        } catch (_) { doc = null; }
        var here = typeof current_map !== 'undefined' && current_map && current_map.map ? current_map.map : null;
        var data = here ? parseLive(doc, here.ID) : null;
        if (data && data.map !== null && (here.ID !== data.map || here.product !== product)) {
            // The character is on another map: open that one (a new page, as when picking it from the list).
            if (!liveVersions) liveVersions = await fetch('versions.json', { cache: 'no-cache' }).then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; });
            var entry = entryFor(liveVersions, product, data.map, data.client);
            var asked = new URLSearchParams(location.search).get('map');
            if (entry && String(entry.map.WdtFileDataID) !== asked) {
                location.replace('?' + new URLSearchParams({ map: entry.map.WdtFileDataID, build: entry.BuildId, product: product, live: '1' }));
                return;
            }
        } else if (data) drawLive(data);
        setTimeout(liveTick, LIVE_MS);
    }

    /** The row in the map library, after the last layer; hidden on a map without a capture. */
    function ensureRow() {
        if (row) return row;
        var last = document.querySelector('#checkboxes .layer-row:last-of-type');
        if (!last || !last.parentNode) return null;
        row = document.createElement('div');
        row.className = 'layer-row';
        var head = document.createElement('div'), label = document.createElement('label'), tag = document.createElement('code');
        head.className = 'layer-head';
        label.className = 'layer-name';
        input = document.createElement('input');
        input.type = 'checkbox';
        input.checked = on();
        input.addEventListener('change', function () {
            try { localStorage.setItem(STORAGE_KEY, String(input.checked)); } catch (_) {}
            if (input.checked) draw(); else remove();
        });
        label.append(input, ' Captured');
        tag.className = 'layer-tag';
        tag.textContent = 'this PC';
        head.append(label, tag);
        var definition = document.createElement('p');
        definition.className = 'layer-def';
        definition.textContent = 'Your path and the game objects recorded from the live game.';
        row.append(head, definition);
        last.parentNode.insertBefore(row, last.nextSibling);
        return row;
    }

    /** The map on screen changed: load that map's coverage. */
    async function select(entry) {
        var key = entry.map.product + '|' + entry.map.ID;
        if (shown && shown.key === key) return;
        var mine = ++token;
        remove();
        shown = null;
        var data = null;
        try {
            var response = await fetch('/__local/coverage/' + encodeURIComponent(entry.map.ID) + '.json', { cache: 'no-store' });
            if (response.ok) data = parse(await response.json());
        } catch (_) { data = null; }
        if (mine !== token) return;
        shown = { key: key, data: data };
        if (!ensureRow()) return;
        row.hidden = !data;
        if (data && input.checked) draw();
    }

    // The page has no hook for this script: it watches for the map on screen to change (and its tile range to be known).
    setInterval(function () {
        var entry = typeof current_map !== 'undefined' && current_map && current_map.map && Number.isInteger(current_map.map.ID) ? current_map : null;
        var key = entry && canDraw() ? entry.map.product + '|' + entry.map.ID + '|' + min_x + '|' + min_y : null;
        if (!key || key === selected) return;
        selected = key;
        select(entry);
    }, 500);
    if (live()) {
        // A small window beside the game: the map gets all of it.
        var collapse = setInterval(function () {
            if (typeof MapUI === 'undefined' || !MapUI || typeof MapUI.collapse !== 'function') return;
            clearInterval(collapse);
            MapUI.collapse();
        }, 200);
        liveTick();
    }
}());
