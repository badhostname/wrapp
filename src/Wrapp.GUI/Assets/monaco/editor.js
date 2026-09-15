// Single-editor host page bootstrap (MonacoService). Externalized from the
// former inline HTML so the page can carry a strict CSP with no inline script
// (Workstream G1/G5). Behavior is intentionally identical to the inline
// version; the theme now arrives via the query string instead of C# string
// interpolation.
(function () {
    var t = new URLSearchParams(window.location.search).get('theme');
    var theme = (t === 'vs' || t === 'vs-dark') ? t : 'vs-dark';
    var bg = theme === 'vs' ? '#f3f3f3' : '#1e1e1e';
    document.getElementById('container').style.background = bg;

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
            window.chrome.webview.postMessage(window.editor.getValue());
        });
        // Apply content queued before Monaco finished loading.
        if (window._pendVal !== undefined) {
            window._suppressChange = true;
            var lang = window._pendLang || 'powershell';
            monaco.editor.setModelLanguage(window.editor.getModel(), lang);
            window.editor.setValue(window._pendVal);
            window._suppressChange = false;
            window._pendVal = undefined;
            window._pendLang = undefined;
        }
    });

    window.setValue = function (content, language) {
        if (!window.editor) { window._pendVal = content; window._pendLang = language; return; }
        window._suppressChange = true;
        monaco.editor.setModelLanguage(window.editor.getModel(), language || 'powershell');
        window.editor.setValue(content);
        window._suppressChange = false;
    };
    window.getValue = function () {
        return window.editor ? window.editor.getValue() : '';
    };
    window.setReadOnly = function (readOnly) {
        if (window.editor) window.editor.updateOptions({ readOnly: readOnly });
    };
})();
