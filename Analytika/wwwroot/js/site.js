// Bix — Global JavaScript

// ── App-wide loading indicator ───────────────────────────────────────────────
(function initAppLoadingIndicator() {
    var overlay = document.getElementById('appLoadingOverlay');
    var activeCount = 0;
    var showTimer = null;

    function setVisible(visible) {
        if (!overlay) return;
        overlay.classList.toggle('is-active', visible);
        overlay.setAttribute('aria-hidden', visible ? 'false' : 'true');
    }

    window.showAppLoader = function() {
        activeCount += 1;
        if (showTimer) window.clearTimeout(showTimer);
        showTimer = window.setTimeout(function() { setVisible(activeCount > 0); }, 120);
    };

    window.hideAppLoader = function() {
        activeCount = Math.max(0, activeCount - 1);
        if (activeCount === 0) {
            if (showTimer) window.clearTimeout(showTimer);
            showTimer = null;
            setVisible(false);
        }
    };

    // Round spinner (ported from React RoundSpinner) — sizeClass: bix-spinner-xs|sm|md|lg|xl
    window.bixSpinnerMarkup = function(sizeClass, colorClass) {
        var size = sizeClass || 'bix-spinner-sm';
        var color = colorClass || 'bix-spinner-teal';
        return '<svg class="bix-spinner ' + size + ' ' + color + ' me-1" viewBox="3 3 18 18" aria-hidden="true">' +
            '<path class="bix-spinner-track" d="M12 5C8.13401 5 5 8.13401 5 12C5 15.866 8.13401 19 12 19C15.866 19 19 15.866 19 12C19 8.13401 15.866 5 12 5ZM3 12C3 7.02944 7.02944 3 12 3C16.9706 3 21 7.02944 21 12C21 16.9706 16.9706 21 12 21C7.02944 21 3 16.9706 3 12Z"></path>' +
            '<path class="bix-spinner-head" d="M16.9497 7.05015C14.2161 4.31648 9.78392 4.31648 7.05025 7.05015C6.65973 7.44067 6.02656 7.44067 5.63604 7.05015C5.24551 6.65962 5.24551 6.02646 5.63604 5.63593C9.15076 2.12121 14.8492 2.12121 18.364 5.63593C18.7545 6.02646 18.7545 6.65962 18.364 7.05015C17.9734 7.44067 17.3403 7.44067 16.9497 7.05015Z"></path>' +
            '</svg>';
    };

    window.bixDotsMarkup = function(variant) {
        variant = variant || 'v2';
        if (variant === 'v3') {
            return '<div class="bix-dots bix-dots-v3" aria-hidden="true"><span></span><span></span><span></span></div>';
        }
        return '<div class="bix-dots bix-dots-v2" aria-hidden="true"><span></span><span></span><span></span></div>';
    };

    document.addEventListener('click', function(e) {
        var link = e.target.closest('a[href]');
        if (!link || link.target || link.hasAttribute('download') || link.dataset.noLoader === 'true') return;
        var href = link.getAttribute('href') || '';
        if (!href || href.charAt(0) === '#' || href.indexOf('javascript:') === 0 || href.indexOf('mailto:') === 0) return;
        try {
            var next = new URL(href, window.location.href);
            if (next.origin === window.location.origin && next.pathname !== window.location.pathname + window.location.search) {
                window.showAppLoader();
            }
        } catch (_) {}
    });

    window.addEventListener('beforeunload', function() {
        setVisible(true);
    });

    if (window.fetch) {
        var originalFetch = window.fetch.bind(window);
        window.fetch = function(input, init) {
            var method = ((init && init.method) || (input && input.method) || 'GET').toUpperCase();
            var shouldShow = method !== 'GET' && !(init && init.headers && init.headers['X-No-Loader']);
            if (shouldShow) window.showAppLoader();
            return originalFetch(input, init).finally(function() {
                if (shouldShow) window.hideAppLoader();
            });
        };
    }

    if (window.jQuery) {
        $(document).ajaxSend(function(_event, _xhr, settings) {
            var method = ((settings && settings.type) || 'GET').toUpperCase();
            if (method !== 'GET') window.showAppLoader();
        });
        $(document).ajaxComplete(function(_event, _xhr, settings) {
            var method = ((settings && settings.type) || 'GET').toUpperCase();
            if (method !== 'GET') window.hideAppLoader();
        });
    }
})();

// ── Horizontal menubar (mobile drawer) ───────────────────────────────────────
(function initMenubar() {
    var root = document.documentElement;
    var openBtn = document.getElementById('menubarOpen');
    var mobileNav = document.getElementById('menubarMobile');
    var overlay = document.getElementById('menubarOverlay');
    if (!mobileNav) return;

    function openMobile() {
        root.classList.add('menubar-open');
        mobileNav.hidden = false;
        if (overlay) {
            overlay.removeAttribute('aria-hidden');
            overlay.style.display = 'block';
        }
        if (openBtn) openBtn.setAttribute('aria-expanded', 'true');
        document.body.style.overflow = 'hidden';
    }

    function closeMobile() {
        root.classList.remove('menubar-open');
        mobileNav.hidden = true;
        if (overlay) {
            overlay.setAttribute('aria-hidden', 'true');
            overlay.style.display = 'none';
        }
        if (openBtn) openBtn.setAttribute('aria-expanded', 'false');
        document.body.style.overflow = '';
    }

    if (openBtn) openBtn.addEventListener('click', function () {
        if (root.classList.contains('menubar-open')) closeMobile();
        else openMobile();
    });
    if (overlay) overlay.addEventListener('click', closeMobile);

    document.querySelectorAll('.menubar-mobile-group-toggle').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var group = this.closest('.menubar-mobile-group');
            if (!group) return;
            var expanded = group.classList.toggle('expanded');
            this.setAttribute('aria-expanded', expanded ? 'true' : 'false');
        });
    });

    document.querySelectorAll('.menubar-mobile-link, .menubar-mobile-sublink').forEach(function (link) {
        link.addEventListener('click', function () {
            if (window.innerWidth < 992) closeMobile();
        });
    });

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && root.classList.contains('menubar-open')) closeMobile();
    });

    window.addEventListener('resize', function () {
        if (window.innerWidth >= 992 && root.classList.contains('menubar-open')) closeMobile();
    });
})();

// ── Toast helper ──────────────────────────────────────────────────────────────
function showToast(message, type) {
    type = type || 'success';
    var classes = { success: 'toast-success', error: 'toast-error', warning: 'toast-warning', info: 'toast-info' };
    var icons   = { success: 'fa-check-circle', error: 'fa-exclamation-circle', warning: 'fa-exclamation-triangle', info: 'fa-info-circle' };
    var delay   = type === 'error' ? 6000 : 4500;

    var toastEl = document.createElement('div');
    toastEl.className = 'toast align-items-center border-0 ' + (classes[type] || 'toast-success');
    toastEl.setAttribute('role', 'alert');
    toastEl.setAttribute('aria-live', 'assertive');
    toastEl.setAttribute('aria-atomic', 'true');
    toastEl.setAttribute('data-bs-autohide', 'true');
    toastEl.setAttribute('data-bs-delay', delay);
    toastEl.innerHTML =
        '<div class="d-flex">' +
            '<div class="toast-body d-flex align-items-center gap-2">' +
                '<i class="fas ' + (icons[type] || 'fa-check-circle') + '" aria-hidden="true"></i>' +
                '<span>' + message + '</span>' +
            '</div>' +
            '<button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast" aria-label="Close"></button>' +
        '</div>';

    var container = document.querySelector('.toast-container');
    if (container) {
        container.appendChild(toastEl);
        bootstrap.Toast.getOrCreateInstance(toastEl).show();
        toastEl.addEventListener('hidden.bs.toast', function() { toastEl.remove(); });
    }
}

// ── Confirmation modal ────────────────────────────────────────────────────────
(function initConfirmModal() {
    var modalEl = document.getElementById('confirmModal');
    if (!modalEl) return;

    var bsModal    = new bootstrap.Modal(modalEl);
    var msgEl      = document.getElementById('confirmModalMessage');
    var confirmBtn = document.getElementById('confirmModalConfirm');
    var pendingFn  = null;

    document.addEventListener('click', function(e) {
        var btn = e.target.closest('[data-confirm]');
        if (!btn) return;
        e.preventDefault();
        e.stopPropagation();
        if (msgEl) msgEl.textContent = btn.dataset.confirm || 'Are you sure you want to continue?';
        pendingFn = function() {
            if (btn.tagName === 'A') {
                window.location.href = btn.href;
            } else if (btn.type === 'submit') {
                btn.removeAttribute('data-confirm');
                btn.click();
            }
        };
        bsModal.show();
    });

    if (confirmBtn) confirmBtn.addEventListener('click', function() {
        bsModal.hide();
        if (pendingFn) { pendingFn(); pendingFn = null; }
    });

    modalEl.addEventListener('hidden.bs.modal', function() { pendingFn = null; });
})();

// ── Submit button loading state ───────────────────────────────────────────────
(function initLoadingButtons() {
    document.querySelectorAll('form').forEach(function(form) {
        form.addEventListener('submit', function() {
            var btn = form.querySelector('[data-loading-text]');
            if (btn && !btn.disabled) {
                var originalHtml = btn.innerHTML;
                btn.disabled = true;
                btn.innerHTML = window.bixSpinnerMarkup('bix-spinner-sm') + btn.dataset.loadingText;
                setTimeout(function() { btn.disabled = false; btn.innerHTML = originalHtml; }, 15000);
            }
            window.showAppLoader();
        });
    });
})();

// ── Bootstrap 5 native form validation ───────────────────────────────────────
(function initFormValidation() {
    document.querySelectorAll('form.needs-validation').forEach(function(form) {
        form.addEventListener('submit', function(e) {
            if (!form.checkValidity()) {
                e.preventDefault();
                e.stopPropagation();
            }
            form.classList.add('was-validated');
        });
    });
})();

// ── Bootstrap tooltips ───────────────────────────────────────────────────────
(function initTooltips() {
    if (!window.bootstrap || !bootstrap.Tooltip) return;
    document.querySelectorAll('[data-bs-toggle="tooltip"]').forEach(function(el) {
        bootstrap.Tooltip.getOrCreateInstance(el);
    });
})();

// ── DataTables ────────────────────────────────────────────────────────────────
$(document).ready(function() {
    if ($.fn.DataTable) {
        $('.data-table').each(function() {
            if (!$.fn.DataTable.isDataTable(this)) {
                $(this).DataTable({
                    pageLength: 25,
                    lengthMenu: [[25, 50, 100, -1], [25, 50, 100, 'All']],
                    responsive: true,
                    dom: '<"dt-toolbar d-flex flex-wrap justify-content-between align-items-center gap-2 mb-3"<"dt-search"f><"dt-export"B>>rtip',
                    buttons: [
                        { extend: 'csv',    className: 'btn btn-sm btn-outline-secondary', text: '<i class="fas fa-download me-1" aria-hidden="true"></i>CSV' },
                        { extend: 'colvis', className: 'btn btn-sm btn-outline-secondary', text: '<i class="fas fa-columns me-1" aria-hidden="true"></i>Columns' }
                    ],
                    columnDefs: [{ orderable: false, targets: -1 }],
                    language: {
                        search: '',
                        searchPlaceholder: 'Search…',
                        emptyTable:   'No records found',
                        zeroRecords:  'No matching records',
                        info:         '_START_–_END_ of _TOTAL_',
                        infoEmpty:    '0 records',
                        infoFiltered: '(filtered from _MAX_)',
                        paginate:     { previous: '‹', next: '›' }
                    }
                });
            }
        });
    }
});

// ── Aurora shader background — auto-loaded on Report Scheduler pages ──────────
(function () {
    /* Only on ReportScheduler routes */
    if (!/\/ReportScheduler\//i.test(window.location.pathname)) return;

    function runShader() {
        /* shader-bg.js is self-contained (raw WebGL, no Three.js); guard against double-load */
        if (document.querySelector('script[src*="shader-bg.js?v=4"]')) return;
        var s = document.createElement('script');
        s.src = '/js/shader-bg.js?v=4';
        document.head.appendChild(s);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', runShader);
    } else {
        runShader();
    }
})();

// ── Support chat panel ────────────────────────────────────────────────────────
(function initSupportChat() {
    var panel    = document.getElementById('supportChatPanel');
    var backdrop = document.getElementById('supportBackdrop');
    var openBtn  = document.getElementById('supportChatBtn');
    var closeBtn = document.getElementById('supportCloseBtn');
    var form     = document.getElementById('supportChatForm');
    var input    = document.getElementById('supportInput');
    var sendBtn  = document.getElementById('supportSendBtn');
    var messages = document.getElementById('supportMessages');

    if (!panel || !openBtn) return;

    /* Conversation history sent to the server */
    var history = [];
    var openBtnMobile = document.getElementById('supportChatBtnMobile');

    function open() {
        panel.classList.add('is-open');
        backdrop.classList.add('is-open');
        panel.setAttribute('aria-hidden', 'false');
        input.focus();
    }
    function close() {
        panel.classList.remove('is-open');
        backdrop.classList.remove('is-open');
        panel.setAttribute('aria-hidden', 'true');
    }

    openBtn.addEventListener('click', function(e) { e.preventDefault(); open(); });
    if (openBtnMobile) openBtnMobile.addEventListener('click', function(e) { e.preventDefault(); open(); });
    closeBtn.addEventListener('click', close);
    backdrop.addEventListener('click', close);
    document.addEventListener('keydown', function(e) {
        if (e.key === 'Escape' && panel.classList.contains('is-open')) close();
    });

    /* Auto-grow textarea */
    input.addEventListener('input', function() {
        this.style.height = 'auto';
        this.style.height = Math.min(this.scrollHeight, 120) + 'px';
    });
    /* Send on Enter (Shift+Enter = newline) */
    input.addEventListener('keydown', function(e) {
        if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
    });

    form.addEventListener('submit', function(e) { e.preventDefault(); send(); });

    function appendMsg(role, text) {
        var div = document.createElement('div');
        div.className = 'support-msg support-msg-' + role;
        var bubble = document.createElement('div');
        bubble.className = 'support-bubble';
        bubble.textContent = text;
        div.appendChild(bubble);
        messages.appendChild(div);
        messages.scrollTop = messages.scrollHeight;
        return div;
    }

    function showTyping() {
        var div = document.createElement('div');
        div.className = 'support-msg support-msg-assistant support-typing';
        div.id = 'supportTyping';
        div.innerHTML = '<div class="support-bubble"><span class="support-dot"></span><span class="support-dot"></span><span class="support-dot"></span></div>';
        messages.appendChild(div);
        messages.scrollTop = messages.scrollHeight;
    }
    function hideTyping() {
        var t = document.getElementById('supportTyping');
        if (t) t.remove();
    }

    function send() {
        var text = input.value.trim();
        if (!text) return;

        input.value = '';
        input.style.height = 'auto';
        sendBtn.disabled = true;

        appendMsg('user', text);
        history.push({ role: 'user', content: text });

        showTyping();

        /* Get CSRF token */
        var token = form.querySelector('input[name="__RequestVerificationToken"]');
        var csrf  = token ? token.value : '';

        fetch('/Support/Chat', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': csrf
            },
            body: JSON.stringify({ messages: history })
        })
        .then(function(r) { return r.json(); })
        .then(function(data) {
            hideTyping();
            var reply = data.reply || data.error || 'Something went wrong.';
            appendMsg('assistant', reply);
            history.push({ role: 'assistant', content: reply });
        })
        .catch(function() {
            hideTyping();
            appendMsg('assistant', 'Network error — please check your connection and try again.');
        })
        .finally(function() {
            sendBtn.disabled = false;
            input.focus();
        });
    }
})();

// ── Utility: format date as DD/MM/YYYY ───────────────────────────────────────
function formatDateDDMMYYYY(dateStr) {
    var d = new Date(dateStr);
    return ('0' + d.getDate()).slice(-2) + '/' +
           ('0' + (d.getMonth() + 1)).slice(-2) + '/' +
           d.getFullYear();
}
