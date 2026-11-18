// The local map server's addition to the site's 3D viewer (LocalMap.cs): the captured game objects of the map on screen,
// handed to the renderer build kept with ForeverCapture, which can draw models at given positions. Their model files are
// answered by the local server from the game's own storage. The site itself has no part in it.
(function () {
    'use strict';
    var running = false;
    function apply() {
        var params = new URLSearchParams(location.search), M = window.Module;
        if (!M || typeof M._viewerSetObjects !== 'function') return;
        fetch('/__local/objects/' + encodeURIComponent(params.get('map') || '') + '.json?product=' + encodeURIComponent(params.get('product') || ''), { cache: 'no-store' })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (doc) {
                if (!doc || !Array.isArray(doc.rows) || !doc.rows.length) return;
                var count = Math.floor(doc.rows.length / 10), pointer = M._malloc(count * 80) >>> 0;
                try {
                    // The view is read after _malloc: memory growth replaces it.
                    M.HEAPF64.set(Float64Array.from(doc.rows), pointer >>> 3);
                    M._viewerSetObjects(pointer, count);
                } finally { M._free(pointer); }
                M._viewerSetObjectsEnabled(1);
                console.info('[ForeverCapture] ' + doc.drawn + ' captured objects drawn' + (doc.skipped ? ', ' + doc.skipped + ' without a model of their own' : ''));
            }).catch(function () {});
    }
    // A new map or a restarted renderer starts without them: hand them over each time the viewer starts running.
    setInterval(function () {
        var now = typeof window.viewer3dDiagnostics === 'function' && !!window.viewer3dDiagnostics().running;
        if (now && !running) apply();
        running = now;
    }, 500);
}());
