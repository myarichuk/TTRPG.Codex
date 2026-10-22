window.codexTimeZone = function () { return Intl.DateTimeFormat().resolvedOptions().timeZone; };

(function () {
    // ---- Splash ----
    function removeSplash() {
        const el = document.getElementById("boot-splash");
        if (!el) return;
        el.style.opacity = '0';
        el.style.transition = 'opacity 0.3s ease';
        setTimeout(function () {
            if (el.parentNode) el.parentNode.removeChild(el);
        }, 300);
    }

    // ---- Reconnect modal ----
    function initReconnectLogic() {
        const reconnectModal = document.getElementById("components-reconnect-modal");
        const retryButton = document.getElementById("components-reconnect-button");
        const resumeButton = document.getElementById("components-resume-button");
        if (!reconnectModal) return;

        reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);
        if (retryButton) retryButton.addEventListener("click", retry);
        if (resumeButton) resumeButton.addEventListener("click", resume);

        function handleReconnectStateChanged(event) {
            if (event.detail.state === "show") {
                if (reconnectModal.showModal && !reconnectModal.open) reconnectModal.showModal();
            } else if (event.detail.state === "hide") {
                if (reconnectModal.close && reconnectModal.open) reconnectModal.close();
            } else if (event.detail.state === "failed") {
                document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
            } else if (event.detail.state === "rejected") {
                location.reload();
            }
        }

        async function retry() {
            document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
            try {
                const successful = await Blazor.reconnect();
                if (!successful) {
                    const resumeSuccessful = await Blazor.resumeCircuit();
                    if (!resumeSuccessful) {
                        location.reload();
                    } else if (reconnectModal.close) {
                        reconnectModal.close();
                    }
                }
            } catch (err) {
                document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
            }
        }

        async function resume() {
            try {
                const successful = await Blazor.resumeCircuit();
                if (!successful) location.reload();
            } catch {
                location.reload();
            }
        }

        async function retryWhenDocumentBecomesVisible() {
            if (document.visibilityState === "visible") await retry();
        }
    }

    // ---- Installable app ----
    function registerServiceWorker() {
        if (!('serviceWorker' in navigator)) return;
        // Only on secure origins (https or localhost); registration silently fails elsewhere.
        navigator.serviceWorker.register('/sw.js', { scope: '/' }).catch(function (err) {
            console.debug('Service worker not registered:', err && err.message);
        });
    }

    function init() {
        registerServiceWorker();
        initReconnectLogic();
        if (window.Blazor) {
            try {
                Blazor.start().then(removeSplash).catch(removeSplash);
            } catch (e) {
                removeSplash();
            }
        } else {
            setTimeout(removeSplash, 2000);
        }

        window.addEventListener("load", function () {
            setTimeout(removeSplash, 8000);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();

// ---- Interop used by components ----
window.codex = {
    // Theme: persisted per browser; the inline <head> script applies it before first paint.
    getTheme: function () {
        return document.documentElement.getAttribute('data-theme') || 'dark';
    },
    setTheme: function (theme) {
        document.documentElement.setAttribute('data-theme', theme);
        document.documentElement.setAttribute('data-bs-theme', theme);
        try { localStorage.setItem('codex-theme', theme); } catch (e) { /* private mode */ }
    },

    // Downloads: Blazor hands over a data URI (or blob URL) plus filename.
    download: function (href, filename) {
        const a = document.createElement('a');
        a.href = href;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        a.remove();
    },

    copyText: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (e) {
            return false;
        }
    },

    // Native <dialog> helpers for the ConfirmDialog component and other modals.
    showModal: function (id) {
        const el = document.getElementById(id);
        if (el && el.showModal && !el.open) el.showModal();
    },
    // Escape on a guarded dialog doesn't close it; the component's oncancel handler decides (asking before discarding work).
    guardModal: function (id) {
        const el = document.getElementById(id);
        if (el && !el.dataset.guarded) {
            el.dataset.guarded = "true";
            el.addEventListener("cancel", e => e.preventDefault());
        }
    },
    closeModal: function (id) {
        const el = document.getElementById(id);
        if (el && el.close && el.open) el.close();
    },
    focus: function (id) {
        const el = document.getElementById(id);
        if (el) el.focus();
    }
};

// Kept for backwards compatibility with earlier builds.
window.codexDownload = window.codex.download;
