// Multi-model (tabbed) host page bootstrap (MonacoTabService). Externalized
// from the former inline HTML for the strict-CSP page (Workstream G1/G5).
// Behavior is intentionally identical to the inline version.
(function () {
    var t = new URLSearchParams(window.location.search).get('theme');
    var theme = (t === 'vs' || t === 'vs-dark') ? t : 'vs-dark';
    var bg = theme === 'vs' ? '#f3f3f3' : '#1e1e1e';
    document.getElementById('container').style.background = bg;

    window.models = {};
    window._activeModelId = '';
    window._suppressChange = false;

    require.config({ paths: { 'vs': 'https://monaco.local/vs' } });
    require(['vs/editor/editor.main'], function () {
        window.editor = monaco.editor.create(document.getElementById('container'), {
            value: '',
            language: 'powershell',
            theme: theme,
            automaticLayout: true,
            minimap: { enabled: true },
            scrollBeyondLastLine: false,
            fontFamily: 'Consolas, "Courier New", monospace',
            fontSize: 13
        });

        window.editor.onDidChangeModelContent(function () {
            if (window._suppressChange) return;
            var id = window._activeModelId || '';
            window.chrome.webview.postMessage(JSON.stringify({
                id: id,
                content: window.editor.getValue()
            }));
        });

        // Process any models queued before Monaco loaded
        if (window._pendingModels) {
            for (var id in window._pendingModels) {
                var p = window._pendingModels[id];
                window.models[id] = monaco.editor.createModel(p.content, p.language || 'powershell');
            }
            window._pendingModels = null;
        }
        if (window._pendingSwitchId && window.models[window._pendingSwitchId]) {
            window._suppressChange = true;
            window.editor.setModel(window.models[window._pendingSwitchId]);
            window._activeModelId = window._pendingSwitchId;
            window._suppressChange = false;
            window._pendingSwitchId = null;
        }
    });

    window.createModel = function (id, content, language) {
        if (!window.editor) {
            window._pendingModels = window._pendingModels || {};
            window._pendingModels[id] = { content: content, language: language };
            return;
        }
        window.models[id] = monaco.editor.createModel(content, language || 'powershell');
    };

    window.switchModel = function (id) {
        if (!window.editor || !window.models[id]) {
            window._pendingSwitchId = id;
            return;
        }
        window._suppressChange = true;
        window.editor.setModel(window.models[id]);
        window._activeModelId = id;
        window._suppressChange = false;
    };

    window.setModelContent = function (id, content, language) {
        if (!window.editor) {
            window.createModel(id, content, language);
            return;
        }
        if (!window.models[id]) {
            window.models[id] = monaco.editor.createModel(content, language || 'powershell');
            return;
        }
        window._suppressChange = true;
        window.models[id].setValue(content);
        if (language) monaco.editor.setModelLanguage(window.models[id], language);
        window._suppressChange = false;
    };

    window.getModelContent = function (id) {
        return window.models[id] ? window.models[id].getValue() : '';
    };
})();
