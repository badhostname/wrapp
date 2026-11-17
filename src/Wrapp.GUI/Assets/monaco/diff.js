// Diff-editor host page bootstrap (MonacoDiffService). Externalized from the
// former inline HTML for the strict-CSP page (Workstream G1/G5). Behavior is
// intentionally identical to the inline version.
(function () {
    var t = new URLSearchParams(window.location.search).get('theme');
    var theme = (t === 'vs' || t === 'vs-dark') ? t : 'vs-dark';
    var bg = theme === 'vs' ? '#f3f3f3' : '#1e1e1e';
    document.getElementById('container').style.background = bg;

    require.config({ paths: { 'vs': 'https://monaco.local/vs' } });
    require(['vs/editor/editor.main'], function () {
        window.diffEditor = monaco.editor.createDiffEditor(document.getElementById('container'), {
            theme: theme,
            automaticLayout: true,
            readOnly: true,
            renderSideBySide: true,
            scrollBeyondLastLine: false,
            fontFamily: 'Consolas, "Courier New", monospace',
            fontSize: 13,
            minimap: { enabled: false }
        });
        window._ready = true;
        if (window._pending) {
            window.setDiff(window._pending.original, window._pending.modified, window._pending.language);
            window._pending = undefined;
        }
    });
    window.setDiff = function (original, modified, language) {
        if (!window._ready || !window.diffEditor) {
            window._pending = { original: original, modified: modified, language: language };
            return;
        }
        var lang = language || 'plaintext';
        var originalModel = monaco.editor.createModel(original, lang);
        var modifiedModel = monaco.editor.createModel(modified, lang);
        window.diffEditor.setModel({ original: originalModel, modified: modifiedModel });
    };
})();
