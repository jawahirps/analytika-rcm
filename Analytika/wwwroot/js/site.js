// Analytika RCM — Global JavaScript

// ── Sidebar ───────────────────────────────────────────────────────────────────
(function initSidebar() {
    var root    = document.documentElement;
    var sidebar = document.getElementById('sidebar');
    var overlay = document.getElementById('sidebarOverlay');
    var toggleBtn  = document.getElementById('sidebarToggle');
    var toggleIcon = document.getElementById('sidebarToggleIcon');
    var openBtn    = document.getElementById('sidebarOpen');
    var closeBtn   = document.getElementById('sidebarClose');

    if (!sidebar) return;

    function setCollapsed(collapsed) {
        root.classList.toggle('sidebar-collapsed', collapsed);
        try { localStorage.setItem('sidebar_collapsed', collapsed); } catch (_) {}
        if (toggleIcon) toggleIcon.className = collapsed
            ? 'fas fa-chevron-right sidebar-icon'
            : 'fas fa-chevron-left sidebar-icon';
        if (toggleBtn) toggleBtn.setAttribute('aria-label', collapsed ? 'Expand sidebar' : 'Collapse sidebar');
    }

    function openMobile() {
        root.classList.add('sidebar-open');
        if (overlay) { overlay.removeAttribute('aria-hidden'); overlay.style.display = 'block'; }
        if (openBtn) openBtn.setAttribute('aria-expanded', 'true');
        document.body.style.overflow = 'hidden';
    }

    function closeMobile() {
        root.classList.remove('sidebar-open');
        if (overlay) { overlay.setAttribute('aria-hidden', 'true'); overlay.style.display = 'none'; }
        if (openBtn) openBtn.setAttribute('aria-expanded', 'false');
        document.body.style.overflow = '';
    }

    if (toggleBtn) toggleBtn.addEventListener('click', function() {
        setCollapsed(!root.classList.contains('sidebar-collapsed'));
    });

    if (openBtn)  openBtn.addEventListener('click', openMobile);
    if (closeBtn) closeBtn.addEventListener('click', closeMobile);
    if (overlay)  overlay.addEventListener('click', closeMobile);

    // Section accordion
    document.querySelectorAll('.sidebar-section-toggle').forEach(function(btn) {
        btn.addEventListener('click', function() {
            var section    = this.closest('.sidebar-section');
            var isExpanded = section.classList.contains('expanded');
            section.classList.toggle('expanded', !isExpanded);
            this.setAttribute('aria-expanded', !isExpanded ? 'true' : 'false');
        });
    });

    // Close mobile sidebar on nav click
    document.querySelectorAll('.sidebar-link, .sidebar-sublink').forEach(function(link) {
        link.addEventListener('click', function() {
            if (window.innerWidth < 992) closeMobile();
        });
    });

    // Keyboard: close mobile with Escape
    document.addEventListener('keydown', function(e) {
        if (e.key === 'Escape' && root.classList.contains('sidebar-open')) closeMobile();
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
            if (!btn || btn.disabled) return;
            var originalHtml = btn.innerHTML;
            btn.disabled = true;
            btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1" aria-hidden="true"></span>' + btn.dataset.loadingText;
            setTimeout(function() { btn.disabled = false; btn.innerHTML = originalHtml; }, 15000);
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

// ── Utility: format date as DD/MM/YYYY ───────────────────────────────────────
function formatDateDDMMYYYY(dateStr) {
    var d = new Date(dateStr);
    return ('0' + d.getDate()).slice(-2) + '/' +
           ('0' + (d.getMonth() + 1)).slice(-2) + '/' +
           d.getFullYear();
}
