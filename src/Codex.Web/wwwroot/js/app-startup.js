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

        // Where the caret is inside a textarea, measured on a hidden copy of its text (a textarea doesn't say).
        const COPIED = ["boxSizing", "width", "fontFamily", "fontSize", "fontWeight", "fontStyle", "letterSpacing", "lineHeight", "textTransform", "wordSpacing",
            "textIndent", "tabSize", "paddingTop", "paddingRight", "paddingBottom", "paddingLeft", "borderTopWidth", "borderRightWidth", "borderBottomWidth", "borderLeftWidth"];
        function caretRect(el) {
            const style = getComputedStyle(el), mirror = document.createElement("div");
            COPIED.forEach(function (name) { mirror.style[name] = style[name]; });
            mirror.style.cssText += ";position:absolute;visibility:hidden;top:0;left:-9999px;white-space:pre-wrap;overflow-wrap:break-word;";
            mirror.textContent = el.value.slice(0, el.selectionStart);
            const marker = document.createElement("span");
            marker.textContent = "\u200b";
            mirror.appendChild(marker);
            document.body.appendChild(mirror);
            const height = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.4;
            const rect = { left: marker.offsetLeft, top: marker.offsetTop, height: height };
            mirror.remove();
            return rect;
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
            list.hidden = false;
            if (box.height > 200) {
                // A tall box (a wiki page): its bottom edge can be off screen, so the list follows the caret instead.
                const caret = caretRect(field);
                const width = Math.min(box.width, 320);
                const line = box.top + caret.top - field.scrollTop;
                list.style.width = width + "px";
                list.style.left = Math.max(box.left, Math.min(box.left + caret.left, box.right - width)) + "px";
                list.style.top = (line + caret.height + 4) + "px";
                if (list.getBoundingClientRect().bottom > window.innerHeight - 8) {
                    list.style.top = Math.max(8, line - list.offsetHeight - 4) + "px";
                }
            } else {
                list.style.left = box.left + "px";
                list.style.width = box.width + "px";
                list.style.top = (box.bottom + 4) + "px";
            }
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

// <details class="popover-menu"> menus (header language and phone menu): one open at a time, closed by an
// outside click or Escape, which then returns focus to the menu's summary.
(function () {
    function closeAll(except) {
        document.querySelectorAll("details.popover-menu[open]").forEach(function (d) { if (d !== except) d.open = false; });
    }
    document.addEventListener("click", function (e) {
        const target = e.target instanceof Element ? e.target : null;
        const menu = target ? target.closest("details.popover-menu") : null;
        // Following a link out of a menu (the phone section list) must not leave the menu open on the next page, and
        // choosing an item (Build with AI's recipes) closes the menu behind the dialog it opens.
        closeAll(menu && target.closest(".popover-panel a, .popover-panel .menu-item") ? null : menu);
    });
    document.addEventListener("keydown", function (e) {
        if (e.key !== "Escape") return;
        const open = document.querySelector("details.popover-menu[open]");
        if (!open) return;
        open.open = false;
        open.querySelector("summary")?.focus();
    });
})();

// Phones never show title= tooltips, and some carry rules (a condition's expiry, an initiative roll, death
// saves). On a screen without hover, tapping something that has a title shows it in a bubble; the next tap,
// a scroll or Escape hides it. Controls keep their own tap behaviour.
(function () {
    if (!window.matchMedia || !window.matchMedia("(hover: none)").matches) return;
    let tip = null;
    function hide() { if (tip) { tip.remove(); tip = null; } }
    document.addEventListener("click", function (e) {
        const wasOpen = !!tip;
        hide();
        const target = e.target instanceof Element ? e.target : null;
        const el = target ? target.closest("[title]") : null;
        if (!el || wasOpen || !el.title.trim() || target.closest("button, a, input, select, textarea, label, summary")) return;
        tip = document.createElement("div");
        tip.className = "tap-tip";
        tip.setAttribute("role", "tooltip");
        tip.dir = "auto";
        tip.textContent = el.title;
        document.body.appendChild(tip);
        const r = el.getBoundingClientRect();
        const w = tip.offsetWidth, h = tip.offsetHeight;
        const left = Math.min(Math.max(8, r.left + r.width / 2 - w / 2), window.innerWidth - w - 8);
        const top = r.top - h - 8 > 8 ? r.top - h - 8 : r.bottom + 8;
        tip.style.left = left + "px";
        tip.style.top = top + "px";
    });
    window.addEventListener("scroll", hide, { passive: true, capture: true });
    document.addEventListener("keydown", function (e) { if (e.key === "Escape") hide(); });
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
    },
    // A text box whose value the server changes from outside what the reader typed (AI text, an undo): the box itself is not
    // re-sent its value on every keystroke, so this is the one way such a change reaches it.
    setValue: function (id, value) {
        const el = document.getElementById(id);
        if (el && el.value !== value) el.value = value;
    },
    // A growing log that scrolls inside itself (the world-building chat): keep its end in view while the reader is at the end,
    // so scrolling back up to re-read is not undone by every new word. force pins it again (the reader just sent something).
    followLog: function (id, force) {
        const el = document.getElementById(id);
        if (!el) return;
        if (!el.dataset.follows) {
            el.dataset.follows = "true";
            el.dataset.pinned = "true";
            el.addEventListener("scroll", () => {
                el.dataset.pinned = el.scrollHeight - el.scrollTop - el.clientHeight < 80 ? "true" : "false";
            }, { passive: true });
        }
        if (force) el.dataset.pinned = "true";
        if (el.dataset.pinned === "true") el.scrollTop = el.scrollHeight;
    },
    // The world-building chat's message box: it grows with what is typed (to 40% of the screen, then scrolls), and Enter sends
    // it where there is a keyboard to make new lines with Shift+Enter. On a touch screen Enter is a new line and the Send button
    // sends. Never while an input method is composing a word (Enter picks the word there).
    composer: function (id, sendId) {
        const el = document.getElementById(id);
        if (!el) return;
        const grow = () => {
            el.style.height = "auto";
            el.style.height = Math.min(el.scrollHeight + 2, Math.round(window.innerHeight * 0.4)) + "px";
        };
        if (!el.dataset.composer) {
            el.dataset.composer = "true";
            el.addEventListener("input", grow);
            el.addEventListener("keydown", e => {
                if (e.key !== "Enter" || e.shiftKey || e.isComposing || e.keyCode === 229) return;
                if (!e.ctrlKey && !e.metaKey && !window.matchMedia("(pointer: fine)").matches) return;
                e.preventDefault();
                const send = document.getElementById(sendId);
                if (send && !send.disabled) send.click();
            });
        }
        grow();
    },
    // The reveal and hide buttons the DM's reading view draws on a page's secret blocks. The page's text is markup, not
    // components, so one listener on its container hands each click to the page. Called after every render; the newest
    // reference wins, and the listener is added once.
    secretActions: function (id, ref) {
        const el = document.getElementById(id);
        if (!el) return;
        el._secretRef = ref;
        if (el.dataset.secretActions) return;
        el.dataset.secretActions = "true";
        el.addEventListener("click", e => {
            const b = e.target.closest("[data-secret-block]");
            if (!b || !el.contains(b) || !el._secretRef) return;
            e.preventDefault();
            el._secretRef.invokeMethodAsync("OnSecretAction", parseInt(b.dataset.secretBlock, 10), b.dataset.secretTo);
        });
    },
    // When this browser last opened a page (an ISO time, or null the first time), noting that it is open now (the server's
    // time, as the reveals are stamped with it, so a wrong clock here marks nothing wrongly): what was revealed on it since is
    // marked new. Per browser, and only a convenience: with storage blocked nothing is marked.
    seenSince: function (key, now) {
        try {
            const was = localStorage.getItem(key);
            localStorage.setItem(key, now);
            return was;
        } catch (e) {
            return null;
        }
    },
    // When this browser last opened each of the pages, for the ones it has opened.
    seenAll: function (keys) {
        const out = {};
        try {
            for (const k of keys) {
                const v = localStorage.getItem(k);
                if (v) out[k] = v;
            }
        } catch (e) { /* storage blocked */ }
        return out;
    },
    scrollToId: function (id) {
        const el = document.getElementById(id);
        if (el) el.scrollIntoView({ block: "center", behavior: "smooth" });
    },
    // Bring a panel into view unless all of it already is (beside the chat on a wide screen it usually is).
    revealPanel: function (id) {
        const el = document.getElementById(id);
        if (!el) return;
        const r = el.getBoundingClientRect();
        if (r.top >= 0 && r.bottom <= window.innerHeight) return;
        el.scrollIntoView({ block: "nearest", behavior: "smooth" });
    },
    // MarkdownEditor's toolbar: wrap the selection (or the word "text" when nothing is selected) in a marker, or start
    // each selected line with a prefix. "input" is raised so Blazor's oninput sees the new value, and Undo still works
    // where the browser supports execCommand on a textarea.
    mdWrap: function (id, before, after) {
        const el = document.getElementById(id);
        if (!el || el.readOnly) return;
        const s = el.selectionStart, e = el.selectionEnd;
        const picked = el.value.slice(s, e) || "text";
        mdReplace(el, s, e, before + picked + after);
        el.setSelectionRange(s + before.length, s + before.length + picked.length);
    },
    mdPrefix: function (id, prefix) {
        const el = document.getElementById(id);
        if (!el || el.readOnly) return;
        const v = el.value;
        const s = v.lastIndexOf("\n", el.selectionStart - 1) + 1;
        let e = v.indexOf("\n", el.selectionEnd);
        if (e < 0) e = v.length;
        const lines = v.slice(s, e).split("\n");
        const all = lines.every(function (l) { return l.startsWith(prefix); });
        const text = lines.map(function (l) { return all ? l.slice(prefix.length) : prefix + l.replace(/^(#+ |- )/, ""); }).join("\n");
        mdReplace(el, s, e, text);
        el.setSelectionRange(s, s + text.length);
    },
    // Puts the selected lines (or, with nothing selected, the paragraph at the cursor) in a ":::dm" block, the secret
    // blocks of SecretText.cs; inside a block already, takes it out of it instead. The text keeps its own lines.
    mdSecret: function (id) {
        const el = document.getElementById(id);
        if (!el || el.readOnly) return;
        const v = el.value;
        const lines = v.split("\n");
        const lineAt = function (pos) { return v.slice(0, pos).split("\n").length - 1; };
        const isOpen = function (l) { return /^\s*:::\s*[^\s:]/.test(l); };
        const isClose = function (l) { return /^\s*:::\s*$/.test(l); };
        let first = lineAt(el.selectionStart), last = lineAt(el.selectionEnd);
        // The block the cursor is in, if any: the nearest opening line above with its closing line below.
        let depth = 0, open = -1;
        for (let i = first; i >= 0; i--) {
            if (isClose(lines[i]) && i !== first) depth++;
            else if (isOpen(lines[i])) { if (depth === 0) { open = i; break; } depth--; }
        }
        if (open >= 0) {
            let close = -1; depth = 0;
            for (let i = open + 1; i < lines.length; i++) {
                if (isOpen(lines[i])) depth++;
                else if (isClose(lines[i])) { if (depth === 0) { close = i; break; } depth--; }
            }
            if (close >= first || close < 0) {
                const end = close < 0 ? lines.length : close + 1;
                const inner = lines.slice(open + 1, close < 0 ? lines.length : close).join("\n");
                const s = lines.slice(0, open).join("\n").length + (open > 0 ? 1 : 0);
                const e = lines.slice(0, end).join("\n").length;
                mdReplace(el, s, e, inner);
                el.setSelectionRange(s, s + inner.length);
                return;
            }
        }
        if (el.selectionStart === el.selectionEnd) {
            while (first > 0 && lines[first - 1].trim() !== "") first--;
            while (last < lines.length - 1 && lines[last + 1].trim() !== "") last++;
        }
        const s = lines.slice(0, first).join("\n").length + (first > 0 ? 1 : 0);
        const e = lines.slice(0, last + 1).join("\n").length;
        const picked = v.slice(s, e);
        const text = ":::dm\n" + (picked.trim() === "" ? "" : picked) + "\n:::";
        mdReplace(el, s, e, text);
        el.setSelectionRange(s + 6, s + 6 + (picked.trim() === "" ? 0 : picked.length));
    },
    // A detail pane that is a full-screen sheet on a phone (.phone-sheet): move focus into it when it opens,
    // so keyboards and screen readers follow. Beside the list on a wide screen it is left alone.
    // Opening and closing a Library entry are navigations, and Blazor scrolls to the top after each one;
    // sheetClosed puts the list back where it was when the entry was opened.
    focusSheet: function (id) {
        // Following a link inside an open entry (to another entry) keeps the list position of the first one.
        if (sheetListScroll === null) sheetListScroll = lastLinkScroll;
        const el = document.getElementById(id);
        const sheet = el && el.closest(".phone-sheet");
        if (!sheet || getComputedStyle(sheet).position !== "fixed") return;
        sheet.scrollTop = 0;
        el.focus({ preventScroll: true });
    },
    sheetClosed: function () {
        if (sheetListScroll === null) return;
        window.scrollTo({ top: sheetListScroll, behavior: "instant" });
        sheetListScroll = null;
    }
};

function mdReplace(el, start, end, text) {
    el.focus();
    el.setSelectionRange(start, end);
    if (!document.execCommand || !document.execCommand("insertText", false, text)) {
        el.setRangeText(text, start, end, "end");
        el.dispatchEvent(new Event("input", { bubbles: true }));
    }
}

// Ctrl/Cmd+Shift+H in a MarkdownEditor box that takes secret blocks.
document.addEventListener("keydown", function (e) {
    const el = e.target;
    if (!(el instanceof HTMLTextAreaElement) || !el.hasAttribute("data-md-secrets") || !(e.ctrlKey || e.metaKey) || !e.shiftKey || e.altKey) return;
    if (e.key.toLowerCase() !== "h") return;
    e.preventDefault();
    window.codex.mdSecret(el.id);
});

// Ctrl/Cmd+B and Ctrl/Cmd+I in a MarkdownEditor box.
document.addEventListener("keydown", function (e) {
    const el = e.target;
    if (!(el instanceof HTMLTextAreaElement) || !el.hasAttribute("data-md") || !(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return;
    const key = e.key.toLowerCase();
    if (key !== "b" && key !== "i") return;
    e.preventDefault();
    if (key === "b") window.codex.mdWrap(el.id, "**", "**"); else window.codex.mdWrap(el.id, "*", "*");
});

// Where the page was scrolled when a link was last followed (captured before Blazor handles the click).
let lastLinkScroll = 0;
let sheetListScroll = null;
document.addEventListener("click", function (e) {
    if (e.target instanceof Element && e.target.closest("a[href]")) lastLinkScroll = window.scrollY;
}, true);

// Kept for backwards compatibility with earlier builds.
window.codexDownload = window.codex.download;
