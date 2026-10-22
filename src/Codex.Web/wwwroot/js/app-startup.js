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

    // Quick-entry forms (the table's notes drawer): the box empties the moment the form is sent, not when the
    // server's render gets back. Otherwise whatever is typed straight after Enter either joins the last line or is
    // wiped by the late clear. Only a form marked ready is cleared, so text the server would refuse isn't lost.
    document.addEventListener("submit", function (e) {
        const form = e.target;
        if (!(form instanceof HTMLFormElement) || form.dataset.clearOnSubmit !== "true") return;
        form.querySelectorAll("[data-clear-on-submit-field]").forEach(function (field) {
            if (field.value.trim()) field.value = "";
        });
    }, true);

    // Ctrl+Enter (Cmd+Enter on a Mac) sends a textarea's form, through the same submit as the button.
    document.addEventListener("keydown", function (e) {
        const field = e.target;
        if (e.key !== "Enter" || !(e.ctrlKey || e.metaKey) || !(field instanceof HTMLTextAreaElement) || !field.hasAttribute("data-submit-on-mod-enter") || !field.form) return;
        e.preventDefault();
        field.form.requestSubmit();
    });

    // Mentions in note boxes: typing @ (a character), # (a place) or $ (an item) offers matching names from the
    // box's data-mentions list. Picking one writes "@Name" into the box; the server turns names into ids when the
    // note is saved. The list lives on <body>, outside anything Blazor renders, so a re-render can't disturb it.
    (function () {
        const MAX = 8;
        let list = null, field = null, items = [], active = 0, start = -1;

        function candidates(el) {
            if (el._mentionsSource !== el.dataset.mentions) {
                el._mentionsSource = el.dataset.mentions;
                try { el._mentions = JSON.parse(el.dataset.mentions || "[]"); } catch { el._mentions = []; }
            }
            return el._mentions;
        }

        // The trigger and query just before the caret: "@ma" in "ask @ma|". Spaces are allowed ("@mara of") while
        // something still matches.
        function query(el) {
            const caret = el.selectionStart, before = el.value.slice(0, caret);
            for (let i = before.length - 1; i >= 0 && before.length - i <= 40; i--) {
                const ch = before[i];
                if (ch === "\n") return null;
                if ((ch === "@" || ch === "#" || ch === "$") && (i === 0 || !/[\p{L}\p{N}]/u.test(before[i - 1]))) {
                    return { trigger: ch, text: before.slice(i + 1), start: i };
                }
            }
            return null;
        }

        function matches(el, q) {
            const needle = q.text.toLowerCase();
            return candidates(el).filter(function (c) {
                if (c.t !== q.trigger) return false;
                const name = c.n.toLowerCase();
                return name.startsWith(needle) || name.split(/\s+/).some(function (word) { return word.startsWith(needle); });
            }).slice(0, MAX);
        }

        function close() {
            if (list) list.hidden = true;
            if (field) {
                field.setAttribute("aria-expanded", "false");
                field.removeAttribute("aria-activedescendant");
            }
            field = null; items = []; start = -1;
        }

        function render() {
            if (!list) {
                list = document.createElement("ul");
                list.id = "mention-list";
                list.className = "mention-list";
                list.setAttribute("role", "listbox");
                list.setAttribute("aria-label", "Suggestions");
                // mousedown, not click: the box keeps focus, so the caret is still where the name goes.
                list.addEventListener("mousedown", function (e) {
                    const option = e.target.closest("[role=option]");
                    if (!option) return;
                    e.preventDefault();
                    choose(Number(option.dataset.index));
                });
                document.body.appendChild(list);
            }
            list.replaceChildren.apply(list, items.map(function (c, i) {
                const li = document.createElement("li");
                li.id = "mention-option-" + i;
                li.setAttribute("role", "option");
                li.setAttribute("aria-selected", i === active ? "true" : "false");
                li.dataset.index = i;
                const name = document.createElement("span");
                name.textContent = c.t + c.n;
                const kind = document.createElement("span");
                kind.className = "mention-kind";
                kind.textContent = c.k;
                li.append(name, kind);
                return li;
            }));
            const box = field.getBoundingClientRect();
            list.style.left = box.left + "px";
            list.style.width = box.width + "px";
            list.style.top = (box.bottom + 4) + "px";
            list.hidden = false;
            field.setAttribute("aria-controls", "mention-list");
            field.setAttribute("aria-expanded", "true");
            field.setAttribute("aria-activedescendant", "mention-option-" + active);
        }

        function update(el) {
            const q = query(el);
            const found = q ? matches(el, q) : [];
            if (!q || found.length === 0) { if (field === el) close(); return; }
            if (field !== el) active = 0;
            field = el; items = found; start = q.start;
            active = Math.min(active, items.length - 1);
            render();
        }

        function choose(index) {
            const c = items[index], el = field;
            if (!c || !el) return;
            const caret = el.selectionStart;
            const insert = c.t + c.n + " ";
            el.value = el.value.slice(0, start) + insert + el.value.slice(caret);
            const at = start + insert.length;
            el.setSelectionRange(at, at);
            close();
            // Blazor reads the box from its input event.
            el.dispatchEvent(new Event("input", { bubbles: true }));
        }

        document.addEventListener("input", function (e) {
            if (e.target instanceof HTMLElement && e.target.hasAttribute("data-mentions")) update(e.target);
        });

        // Capture on window, ahead of Blazor and the drawer: while the list is open, its keys are the list's.
        window.addEventListener("keydown", function (e) {
            if (!field || e.target !== field || list.hidden) return;
            if (e.key === "ArrowDown" || e.key === "ArrowUp") {
                active = (active + (e.key === "ArrowDown" ? 1 : -1) + items.length) % items.length;
                render();
            } else if ((e.key === "Enter" && !e.ctrlKey && !e.metaKey) || e.key === "Tab") {
                choose(active);
            } else if (e.key === "Escape") {
                close();
            } else {
                return;
            }
            e.preventDefault();
            e.stopPropagation();
        }, true);

        document.addEventListener("focusout", function (e) { if (e.target === field) close(); });
        document.addEventListener("click", function (e) { if (field && e.target !== field) close(); });
        window.addEventListener("resize", close);
        document.addEventListener("scroll", function () { if (field) render(); }, true);
    })();

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
