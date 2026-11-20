// Mirrors the attachedCallback of jellyfin-web's emby-* custom elements, so the harness renders as the dashboard does.
(function () {
    var n = 0;
    function up(el) {
        var is = el.getAttribute && el.getAttribute('is');
        if (!is || el.__up) return;
        el.__up = true;
        var parent = el.parentNode;
        if (!el.id) el.id = is.replace('-', '') + (n++);
        if (is === 'emby-input') {
            el.classList.add('emby-input');
            var l = document.createElement('label'); l.innerText = el.getAttribute('label') || '';
            l.className = 'inputLabel inputLabelUnfocused'; l.htmlFor = el.id; parent && parent.insertBefore(l, el);
        } else if (is === 'emby-select') {
            el.classList.add('emby-select-withcolor', 'emby-select');
            var s = document.createElement('label'); s.innerText = el.getAttribute('label') || '';
            s.className = 'selectLabel'; s.htmlFor = el.id; parent && parent.insertBefore(s, el);
            parent && parent.insertAdjacentHTML('beforeend', '<div class="selectArrowContainer"><div style="visibility:hidden;display:none;">0</div><span class="selectArrow material-icons keyboard_arrow_down" aria-hidden="true"></span></div>');
        } else if (is === 'emby-textarea') {
            el.classList.add('emby-textarea');
        } else if (is === 'emby-checkbox') {
            el.classList.add('emby-checkbox');
            if (parent) {
                parent.classList.add('emby-checkbox-label');
                var span = parent.querySelector('span');
                parent.insertAdjacentHTML('beforeend', '<span class="checkboxOutline"><span class="material-icons checkboxIcon checkboxIcon-checked ' + (el.getAttribute('data-checkedicon') || 'check') + '" aria-hidden="true"></span><span class="material-icons checkboxIcon checkboxIcon-unchecked ' + (el.getAttribute('data-uncheckedicon') || '') + '" aria-hidden="true"></span></span>');
                span && span.classList.add('checkboxLabel');
            }
        } else if (is === 'emby-button' || is === 'paper-icon-button-light' || is === 'emby-linkbutton') {
            el.classList.add(is === 'paper-icon-button-light' ? 'paper-icon-button-light' : 'emby-button');
        }
    }
    function scan(root) {
        if (root.nodeType !== 1) return;
        if (root.hasAttribute('is')) up(root);
        root.querySelectorAll('[is]').forEach(up);
    }
    new MutationObserver(function (ms) { ms.forEach(function (m) { m.addedNodes.forEach(scan); }); })
        .observe(document.documentElement, { childList: true, subtree: true });
})();
