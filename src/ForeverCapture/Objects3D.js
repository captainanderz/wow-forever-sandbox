// The local map server's addition to the site's 3D viewer (LocalMap.cs): the captured game objects of the map on screen,
// handed to the renderer build kept with ForeverCapture, which can draw models at given positions. Their model files are
// answered by the local server from the game's own storage. The site itself has no part in it.
// A "Server objects" button under the Scene panel's layer buttons shows or hides them, to tell them from the game files' own
// models (the client side). It starts on with every page: a remembered "off" made a later visit look like a lost capture.
(function () {
    'use strict';
    var POLL_TICKS = 10;   // ticks of 500 ms: a recording adds objects to its capture folder every few seconds
    var running = false, ticks = 0, busy = false, shown = null, count = 0, modelsAsked = false, button = null;
    var wanted = true;

    function layers() { return typeof window.viewer3dDiagnostics === 'function' ? window.viewer3dDiagnostics().layers || {} : {}; }
    // A cheap fingerprint of the list: a new or moved object changes it.
    function signature(rows) {
        var sum = rows.length;
        for (var i = 1; i < rows.length; i += 10) sum += rows[i] * 0.001 + rows[i + 1] * 0.000001;
        return String(sum);
    }
    function setEnabled() {
        var M = window.Module;
        if (M && typeof M._viewerSetObjectsEnabled === 'function') M._viewerSetObjectsEnabled(wanted ? 1 : 0);
    }
    // The renderer draws objects only with the M2 layer on, and the viewer starts with it off: switch it on through the
    // viewer's own button. Done once per page on its own (so turning M2 off again sticks), and whenever Server objects is
    // switched on.
    function showModels() {
        var m2 = document.getElementById('toggle-layer-m2');
        if (layers().m2 !== false) return true;
        if (!m2 || m2.disabled) return false;   // not ready yet
        m2.click();
        return true;
    }
    function autoModels() {
        if (modelsAsked || !wanted) return;
        if (layers().m2 === false) {
            if (!showModels()) return;
            console.info('[ForeverCapture] Models (M2) layer turned on: server objects are drawn with it');
        }
        modelsAsked = true;
    }

    // The button: pressed while server objects are on screen (on, and the M2 layer on); hidden on a map without any.
    function ensureButton() {
        if (button) return button;
        var row = document.getElementById('viewer-layer-toggles');
        if (!row) return null;
        button = document.createElement('button');
        button.type = 'button';
        button.className = 'viewer-toggle-btn';
        button.style.gridColumn = '1 / -1';
        button.textContent = 'Server objects';
        button.hidden = true;
        button.addEventListener('click', function () {
            var visible = wanted && layers().m2 !== false;
            wanted = !visible;
            setEnabled();
            if (wanted) showModels();
            sync();
        });
        row.appendChild(button);
        return button;
    }
    function sync() {
        if (!ensureButton()) return;
        var visible = wanted && layers().m2 !== false;
        button.hidden = !count;
        button.disabled = !running;
        button.classList.toggle('active', visible);
        button.setAttribute('aria-pressed', String(visible));
        button.title = count + ' objects the game server placed on this map, recorded by ForeverCapture'
            + (wanted && !visible ? ' (they need the M2 layer)' : '');
    }

    // shown: the fingerprint of the list this run of the renderer has; null until it has one ('' for a map without objects).
    function apply() {
        var params = new URLSearchParams(location.search), M = window.Module;
        if (busy || !M || typeof M._viewerSetObjects !== 'function') return;
        busy = true;
        fetch('/__local/objects/' + encodeURIComponent(params.get('map') || '') + '.json?product=' + encodeURIComponent(params.get('product') || ''), { cache: 'no-store' })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (doc) {
                if (!doc || !Array.isArray(doc.rows)) return;
                if (!doc.rows.length) { if (shown === null) shown = ''; return; }
                var mark = signature(doc.rows);
                if (mark === shown) { autoModels(); return; }
                var rows = Math.floor(doc.rows.length / 10), pointer = M._malloc(rows * 80) >>> 0;
                try {
                    // The view is read after _malloc: memory growth replaces it.
                    M.HEAPF64.set(Float64Array.from(doc.rows), pointer >>> 3);
                    M._viewerSetObjects(pointer, rows);
                } finally { M._free(pointer); }
                setEnabled();
                shown = mark;
                count = doc.drawn;
                console.info('[ForeverCapture] ' + doc.drawn + ' server objects' + (wanted ? ' drawn' : ' (hidden)') + (doc.skipped ? ', ' + doc.skipped + ' without a model of their own' : ''));
                autoModels();
            })
            .catch(function () {})
            .then(function () { busy = false; sync(); });
    }
    // A new map or a restarted renderer starts without them: hand them over each time the viewer starts running, then look
    // for new ones now and then (a recording that is still going writes them as they come).
    setInterval(function () {
        var now = typeof window.viewer3dDiagnostics === 'function' && !!window.viewer3dDiagnostics().running;
        if (now && !running) shown = null;
        running = now;
        // Until this run has its list, every tick; after that now and then.
        if (now && (shown === null || ++ticks % POLL_TICKS === 0)) apply();
        sync();
    }, 500);
}());
