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
    // A box with templates (data-templates) also offers them after a "/" that starts a word: picking one puts it in.
    // A box with wiki pages (data-pages: [{ n: title, k: kind }]) offers them after "[[": picking one writes "[[Title]]".
    (function () {
        const MAX = 8;
        let list = null, field = null, items = [], active = 0, start = -1;

        function candidates(el, trigger) {
            if (trigger === "[[") {
                if (el._pagesSource !== el.dataset.pages) {
                    el._pagesSource = el.dataset.pages;
                    try { el._pages = JSON.parse(el.dataset.pages || "[]").map(function (p) { return { t: "[[", n: p.n, k: p.k }; }); } catch { el._pages = []; }
                }
                return el._pages;
            }
            if (trigger === "/") {
                return window.codexTemplates.of(el).map(function (t, i) {
                    return { t: "/", n: t.a || t.n, k: t.a ? t.n : "", i: i };
                });
            }
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
            // An open "[[" on this line, with no "]" after it: the title typed so far. It wins over an "@" or "#" before it,
            // and loses to one typed after it ("[[a @b": the mention).
            let link = null;
            if (el.hasAttribute("data-pages")) {
                const at = before.lastIndexOf("[[");
                const typed = at < 0 ? "" : before.slice(at + 2);
                if (at >= 0 && typed.length <= 60 && !/[\]\n\[]/.test(typed)) link = { trigger: "[[", text: typed, start: at };
            }
            const other = mentionQuery(el, before);
            return other && (!link || other.start > link.start) ? other : link;
        }

        function mentionQuery(el, before) {
            for (let i = before.length - 1; i >= 0 && before.length - i <= 40; i--) {
                const ch = before[i];
                if (ch === "\n") return null;
                if ((ch === "@" || ch === "#" || ch === "$") && (i === 0 || !/[\p{L}\p{N}]/u.test(before[i - 1]))) {
                    return { trigger: ch, text: before.slice(i + 1), start: i };
                }
                // "/" only where a word starts, so "and/or" and a link's "https://" never open the list.
                if (ch === "/" && el.hasAttribute("data-templates") && (i === 0 || /\s/.test(before[i - 1]))) {
                    return { trigger: ch, text: before.slice(i + 1), start: i };
                }
            }
            return null;
        }

        function matches(el, q) {
            const needle = q.text.toLowerCase();
            return candidates(el, q.trigger).filter(function (c) {
                if (c.t !== q.trigger) return false;
                return [c.n, c.t === "/" ? c.k : ""].some(function (text) {
                    const name = text.toLowerCase();
                    return name.startsWith(needle) || name.split(/\s+/).some(function (word) { return word.startsWith(needle); });
                });
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
                name.textContent = c.t === "[[" ? c.n : c.t + c.n;
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
            if (c.t === "/") {
                const from = start;
                close();
                window.codexTemplates.insert(el, c.i, from, caret);
                return;
            }
            // A link closes itself, over a "]]" already after the caret (typed, or left from an earlier title).
            const closing = c.t === "[[" ? (el.value.slice(caret, caret + 2) === "]]" ? 2 : el.value[caret] === "]" ? 1 : 0) : 0;
            const insert = c.t === "[[" ? "[[" + c.n + "]]" : c.t + c.n + " ";
            el.value = el.value.slice(0, start) + insert + el.value.slice(caret + closing);
            const at = start + insert.length;
            el.setSelectionRange(at, at);
            close();
            // Blazor reads the box from its input event.
            el.dispatchEvent(new Event("input", { bubbles: true }));
        }

        document.addEventListener("input", function (e) {
            if (e.target instanceof HTMLElement && (e.target.hasAttribute("data-mentions") || e.target.hasAttribute("data-templates") || e.target.hasAttribute("data-pages"))) update(e.target);
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
    // Brings a field into view, focused and its text selected (a link to copy by hand).
    selectField: function (id) {
        const el = document.getElementById(id);
        if (!el) return;
        el.scrollIntoView({ block: "center", behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" });
        el.focus({ preventScroll: true });
        if (el.select) el.select();
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
    // Puts text in at the box's caret (or over its selection), with a space either side where a word would touch it.
    mdInsert: function (id, text) {
        const el = document.getElementById(id);
        if (!el || el.readOnly) return;
        const start = el.selectionStart, end = el.selectionEnd, v = el.value;
        const before = start > 0 && !/\s/.test(v[start - 1]) ? " " : "";
        const after = end < v.length && !/\s/.test(v[end]) ? " " : "";
        mdReplace(el, start, end, before + text + after);
        const at = start + before.length + text.length;
        el.setSelectionRange(at, at);
    },
    // Puts template number <index> of the box's data-templates in at the caret (the editor's template menu, a new page).
    mdTemplate: function (id, index) {
        const el = document.getElementById(id);
        document.querySelectorAll("details.md-templates[open]").forEach(function (d) { d.open = false; });
        if (!el || el.readOnly) return;
        el.focus();
        window.codexTemplates.insert(el, index, el.selectionStart, el.selectionEnd);
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

// Ctrl+S (Cmd+S on a Mac) presses the Save of what is being edited: the nearest [data-save-shortcut] button around the focus
// (a page can hold several editors, a session's recap and summary), else the only one on screen; an open dialog's own first.
// The browser's "Save page as" never opens over an editor. A field that sends its value on change (not on input) is blurred
// first, so the value is in before Save runs; focus goes back afterwards.
document.addEventListener("keydown", function (e) {
    if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey || e.key.toLowerCase() !== "s") return;
    function usable(b) { return !b.disabled && b.getClientRects().length > 0; }
    const dialog = Array.from(document.querySelectorAll("dialog[open]")).pop();
    const scope = dialog || document;
    let button = null;
    for (let node = e.target instanceof Element ? e.target : null; node && !button && node !== document.body; node = node.parentElement) {
        if (dialog && !dialog.contains(node)) break;
        button = Array.from(node.querySelectorAll("[data-save-shortcut]")).find(usable) || null;
    }
    if (!button) {
        const all = Array.from(scope.querySelectorAll("[data-save-shortcut]")).filter(usable);
        if (all.length === 1) button = all[0];
    }
    if (!button) return;
    e.preventDefault();
    const field = document.activeElement;
    if (field instanceof HTMLInputElement || field instanceof HTMLSelectElement) field.blur();
    button.click();
    if (field instanceof HTMLElement && field !== document.body) field.focus({ preventScroll: true });
});

// Where the page was scrolled when a link was last followed (captured before Blazor handles the click).
let lastLinkScroll = 0;
let sheetListScroll = null;
document.addEventListener("click", function (e) {
    if (e.target instanceof Element && e.target.closest("a[href]")) lastLinkScroll = window.scrollY;
}, true);

// Kept for backwards compatibility with earlier builds.
window.codexDownload = window.codex.download;

// Live templates (LiveTemplate.cs) in a box with data-templates: [{ a: abbreviation, n: name, b: body }]. Typing an
// abbreviation and Tab puts its template in; then Tab and Shift+Tab step through its fields, a field used twice repeats what
// is typed in the first, and Tab after the last field (or Escape, or leaving the box, or saving) ends it. Tab anywhere else
// still moves focus, so a keyboard user is never trapped. As in an IDE, a field with no default holds its name, greyed and
// selected, so typing replaces it; a name still there when the template ends is taken out again, so it never reaches the
// page. The fields are drawn over the box (a layer that copies the box's text and wrapping, with only the fields showing),
// and a hint (data-template-hint, with {0} the field, {1} its number and {2} how many) says where the caret is.
window.codexTemplates = (function () {
    // Keep in step with Token in LiveTemplate.cs.
    const TOKEN = /\$\$|\$([\p{L}\p{N}_]+)(?::([^$\r\n]*))?\$/gu;
    // What the layer over the box copies from it, so its text wraps line for line with the box's.
    const COPIED = ["fontFamily", "fontSize", "fontWeight", "fontStyle", "fontVariant", "fontStretch", "fontFeatureSettings", "fontVariationSettings",
        "fontKerning", "letterSpacing", "wordSpacing", "lineHeight", "textTransform", "textIndent", "textAlign", "direction", "unicodeBidi", "tabSize",
        "whiteSpace", "overflowWrap", "wordBreak", "paddingTop", "paddingRight", "paddingBottom", "paddingLeft"];
    let s = null;
    let hint = null;
    let layer = null;
    let mirroring = false;
    let frame = 0;
    const resized = window.ResizeObserver ? new ResizeObserver(function () { later(); }) : null;

    function of(el) {
        if (el._templatesSource !== el.dataset.templates) {
            el._templatesSource = el.dataset.templates;
            try { el._templates = JSON.parse(el.dataset.templates || "[]"); } catch { el._templates = []; }
        }
        return el._templates;
    }

    function label(name) { return name.replace(/_/g, " "); }

    // The template with each field holding its default; with <placeholders>, a field with none holds its name instead (ph).
    function expand(body, placeholders) {
        const defaults = {};
        for (const m of body.matchAll(TOKEN)) {
            if (m[1] !== undefined && m[2] !== undefined && !(m[1] in defaults)) defaults[m[1]] = m[2];
        }
        let text = "", at = 0, end = -1;
        const fields = [], seen = new Set();
        for (const m of body.matchAll(TOKEN)) {
            text += body.slice(at, m.index);
            at = m.index + m[0].length;
            if (m[1] === undefined) { text += "$"; continue; }
            if (m[1] === "END") { if (end < 0) end = text.length; continue; }
            const value = defaults[m[1]] || "";
            const ph = !value && !!placeholders;
            const shown = ph ? label(m[1]) : value;
            fields.push({ name: m[1], start: text.length, end: text.length + shown.length, mirror: seen.has(m[1]), ph: ph });
            seen.add(m[1]);
            text += shown;
        }
        text += body.slice(at);
        return { text: text, fields: fields, end: end < 0 ? text.length : end };
    }

    function insert(el, index, from, to) {
        const t = of(el)[index];
        if (!t || el.readOnly) return;
        // A template put in while another is being filled ends that one first; taking its names out moves what comes after.
        stop().forEach(function (r) {
            from = moved(from, r);
            to = moved(to, r);
        });
        const x = expand(t.b.replace(/\r\n/g, "\n"), true);
        mdReplace(el, from, to, x.text);
        const fields = x.fields.map(function (f) { return { name: f.name, start: f.start + from, end: f.end + from, mirror: f.mirror, ph: f.ph }; });
        const stops = fields.filter(function (f) { return !f.mirror; });
        if (stops.length === 0) {
            el.setSelectionRange(from + x.end, from + x.end);
            return;
        }
        s = { el: el, fields: fields, stops: stops, end: from + x.end, index: 0, value: el.value };
        if (resized) resized.observe(el);
        go(0);
    }

    // Where a position ends up once the text from r.start to r.end is taken out.
    function moved(p, r) {
        if (p >= r.end) return p - (r.end - r.start);
        return p > r.start ? r.start : p;
    }

    function go(i) {
        s.index = i;
        const f = s.stops[i];
        s.el.setSelectionRange(f.start, f.end);
        show();
        follow();
    }

    // The part of the screen the box can be read in: under the site's header, above the editor's Save row when that sticks.
    function readable() {
        const top = parseFloat(getComputedStyle(document.documentElement).getPropertyValue("--header-h")) || 0;
        let bottom = window.innerHeight;
        const form = s.el.closest("form");
        const bar = form && form.querySelector(".save-bar");
        if (bar) {
            const r = bar.getBoundingClientRect();
            if (r.top > top && r.top < bottom) bottom = r.top;
        }
        return { top: top, bottom: bottom };
    }

    // Brings the field being filled into view, as an IDE moves to it: in the box first, then the page (on a phone the box is
    // mostly under the Save row and the tab bar). Where the field is comes from the layer, which lays the text out as the box does.
    function follow() {
        const mark = layer && layer.querySelector(".tf-current");
        if (!mark) return;
        const box = s.el.getBoundingClientRect();
        let r = mark.getBoundingClientRect();
        if (r.top < box.top || r.bottom > box.bottom) {
            s.el.scrollTop += r.top - box.top - Math.max(0, (box.height - r.height) / 3);
            paint();
            r = layer.querySelector(".tf-current").getBoundingClientRect();
        }
        const view = readable();
        if (r.top < view.top + 8 || r.bottom > view.bottom - 8) {
            const reduce = window.matchMedia && matchMedia("(prefers-reduced-motion: reduce)").matches;
            window.scrollBy({ top: r.top - view.top - Math.max(0, (view.bottom - view.top - r.height) / 3), behavior: reduce ? "auto" : "smooth" });
        }
    }

    // Ends the template being filled and takes out the names still standing in fields no one typed in, unless <keep> (the box
    // was changed from outside, so where the fields are is not known). Returns the ranges taken out, last first.
    function stop(keep) {
        const was = s;
        s = null;
        if (hint) { hint.hidden = true; hint.textContent = ""; }
        if (layer) { layer.hidden = true; layer.firstChild.textContent = ""; }
        if (resized) resized.disconnect();
        if (!was || keep || was.el.value !== was.value) return [];
        const gone = was.fields.filter(function (f) { return f.ph && f.end > f.start; })
            .sort(function (a, b) { return b.start - a.start; })
            .map(function (f) { return { start: f.start, end: f.end }; });
        if (gone.length === 0) return gone;
        const el = was.el;
        gone.forEach(function (r) { el.setRangeText("", r.start, r.end, "preserve"); });
        el.dispatchEvent(new Event("input", { bubbles: true }));
        return gone;
    }

    function finish() {
        s.el.setSelectionRange(s.end, s.end);
        stop();
    }

    function show() {
        if (!hint) {
            hint = document.createElement("div");
            hint.className = "template-hint";
            hint.setAttribute("role", "status");
            hint.setAttribute("aria-live", "polite");
            hint.dataset.testid = "template-hint";
            document.body.appendChild(hint);
        }
        const f = s.stops[s.index];
        const words = s.el.dataset.templateHint || "{0} ({1}/{2})";
        hint.textContent = words.replace("{0}", label(f.name)).replace("{1}", s.index + 1).replace("{2}", s.stops.length);
        hint.hidden = false;
        place();
        paint();
    }

    // The hint in the box's bottom corner on the end side (left in Hebrew), or the bottom of the screen when that is out of view;
    // above the editor's Save row when that sticks over the box, so the hint never covers its buttons.
    function place() {
        const box = s.el.getBoundingClientRect();
        const rtl = getComputedStyle(s.el).direction === "rtl";
        const left = rtl ? box.left + 18 : box.right - hint.offsetWidth - 18;
        const bottom = Math.min(box.bottom, readable().bottom);
        hint.style.left = Math.max(8, Math.min(left, window.innerWidth - hint.offsetWidth - 8)) + "px";
        hint.style.top = Math.max(8, bottom - hint.offsetHeight - 8) + "px";
    }

    function escape(text) {
        return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    }

    // Above the box's own layer: one more than the highest z-index among its positioned ancestors (a full-screen editor's).
    function zOver(el) {
        let z = 0;
        for (let node = el.parentElement; node && node !== document.body; node = node.parentElement) {
            const cs = getComputedStyle(node);
            if (cs.position !== "static" && cs.zIndex !== "auto") z = Math.max(z, parseInt(cs.zIndex, 10) || 0);
        }
        return z + 1;
    }

    // Draws the fields over the box: the box's text again, invisible, with each field outlined, the one being filled lit,
    // and a name standing in a field drawn greyed over the box's own letters (it covers them with the box's background).
    function paint() {
        const el = s.el;
        if (!layer) {
            layer = document.createElement("div");
            layer.className = "template-marks";
            layer.setAttribute("aria-hidden", "true");
            layer.appendChild(document.createElement("div"));
            document.body.appendChild(layer);
        }
        const cs = getComputedStyle(el);
        const box = el.getBoundingClientRect();
        layer.hidden = false;
        layer.style.left = (box.left + window.scrollX + el.clientLeft) + "px";
        layer.style.top = (box.top + window.scrollY + el.clientTop) + "px";
        layer.style.width = el.clientWidth + "px";
        layer.style.height = el.clientHeight + "px";
        layer.style.zIndex = zOver(el);
        layer.style.setProperty("--tf-bg", cs.backgroundColor);
        const inner = layer.firstChild;
        COPIED.forEach(function (p) { inner.style[p] = cs[p]; });
        inner.style.width = el.clientWidth + "px";
        inner.style.transform = "translate(" + (-el.scrollLeft) + "px, " + (-el.scrollTop) + "px)";
        const value = el.value, current = s.stops[s.index].name;
        let html = "", at = 0;
        s.fields.slice().sort(function (a, b) { return a.start - b.start; }).forEach(function (f) {
            if (f.start < at) return;
            html += escape(value.slice(at, f.start));
            const kind = "tf" + (f.ph ? " tf-ph" : "") + (f.name === current ? " tf-current" : "") + (f.end === f.start ? " tf-empty" : "");
            html += "<span class=\"" + kind + "\">" + escape(value.slice(f.start, f.end)) + "</span>";
            at = f.end;
        });
        // A last empty line still takes its height in the box.
        inner.innerHTML = html + escape(value.slice(at)) + "\n";
    }

    function later() {
        if (!s || frame) return;
        frame = requestAnimationFrame(function () {
            frame = 0;
            if (s) { place(); paint(); }
        });
    }

    // The edit just made: the old text from p to oldEnd became the new text from p to newEnd. Typing, deleting and pasting
    // all leave the caret at the end of what changed, which settles where a run of equal letters was typed.
    function change(before, after, caret) {
        const tail = after.length - caret;
        let p = 0;
        const limit = Math.min(before.length, after.length);
        while (p < limit && before[p] === after[p]) p++;
        if (tail >= 0 && tail <= before.length && before.slice(before.length - tail) === after.slice(caret)) {
            return { p: Math.min(p, caret, before.length - tail), oldEnd: before.length - tail, newEnd: caret };
        }
        let q = 0;
        while (q < limit - p && before[before.length - 1 - q] === after[after.length - 1 - q]) q++;
        return { p: p, oldEnd: before.length - q, newEnd: after.length - q };
    }

    function shift(from, delta, except) {
        s.fields.forEach(function (f) {
            if (f === except) return;
            if (f.start >= from) f.start += delta;
            if (f.end >= from) f.end += delta;
        });
        if (s.end >= from) s.end += delta;
    }

    function edited(el) {
        const c = change(s.value, el.value, el.selectionStart);
        const delta = (c.newEnd - c.p) - (c.oldEnd - c.p);
        // The field the edit is in: the one being filled first, then any (a click into another before typing).
        const inside = function (f) { return c.p >= f.start && c.oldEnd <= f.end; };
        const field = inside(s.stops[s.index]) ? s.stops[s.index] : s.fields.find(inside);
        if (!field) {
            // Typed outside the fields: that ends the template, the fields after the edit moved by it.
            shift(c.oldEnd, delta, null);
            s.value = el.value;
            stop();
            return;
        }
        const oldEnd = field.end;
        field.end += delta;
        shift(oldEnd, delta, field);
        // Typed in: what the field holds is the DM's now, in every use of it.
        s.fields.forEach(function (f) { if (f.name === field.name) f.ph = false; });
        const value = el.value.slice(field.start, field.end);
        let caret = el.selectionStart;
        // Repeat it in the field's other uses, last first so the earlier positions stay right.
        s.fields.filter(function (f) { return f !== field && f.name === field.name; })
            .sort(function (a, b) { return b.start - a.start; })
            .forEach(function (m) {
                const d = value.length - (m.end - m.start);
                if (d === 0 && el.value.slice(m.start, m.end) === value) return;
                el.setRangeText(value, m.start, m.end, "preserve");
                const mEnd = m.end;
                m.end = m.start + value.length;
                shift(mEnd, d, m);
                if (m.start < caret) caret += d;
            });
        if (el.value !== s.value) {
            el.setSelectionRange(caret, caret);
            mirroring = true;
            el.dispatchEvent(new Event("input", { bubbles: true }));
            mirroring = false;
        }
        // Typing in a repeat of a field counts as being in the field.
        s.index = s.stops.findIndex(function (f) { return f.name === field.name; });
        s.value = el.value;
        show();
    }

    document.addEventListener("input", function (e) {
        if (!s || mirroring || e.target !== s.el) return;
        edited(s.el);
    });

    // Leaving the box ends the template, but the window losing the focus (another program, another tab) does not: the box
    // gets it back on return.
    document.addEventListener("focusout", function (e) {
        if (s && e.target === s.el && document.hasFocus()) stop();
    });
    // Saving ends it too, before the form sends the text (Ctrl+S presses Save without leaving the box).
    window.addEventListener("submit", function (e) {
        if (s && e.target instanceof Element && e.target.contains(s.el)) stop();
    }, true);
    window.addEventListener("resize", later);
    window.addEventListener("scroll", later, true);

    // On window, after the suggestion list (which takes Tab and Escape first while it is open).
    window.addEventListener("keydown", function (e) {
        const el = e.target;
        if (e.defaultPrevented || !(el instanceof HTMLTextAreaElement) || e.ctrlKey || e.metaKey || e.altKey) return;
        if (s && el === s.el && el.value !== s.value) stop(true); // changed from outside (the AI, a re-render)
        if (s && el === s.el) {
            if (e.key === "Escape") {
                stop();
            } else if (e.key === "Tab") {
                if (e.shiftKey) go(Math.max(0, s.index - 1));
                else if (s.index + 1 < s.stops.length) go(s.index + 1);
                else finish();
            } else {
                return;
            }
            e.preventDefault();
            e.stopPropagation();
            return;
        }
        if (e.key !== "Tab" || e.shiftKey || !el.hasAttribute("data-templates") || el.readOnly || el.selectionStart !== el.selectionEnd) return;
        const before = el.value.slice(0, el.selectionStart);
        const word = /(?:^|[^\p{L}\p{N}_-])([\p{L}\p{N}_-]+)$/u.exec(before);
        if (!word) return;
        const typed = word[1].toLowerCase();
        const index = of(el).findIndex(function (t) { return t.a && t.a.toLowerCase() === typed; });
        if (index < 0) return;
        e.preventDefault();
        e.stopPropagation();
        insert(el, index, el.selectionStart - word[1].length, el.selectionStart);
    }, true);

    return { of: of, insert: insert, expand: expand };
})();

// Hovering a mention or a [[link]] in written text (or focusing it from the keyboard) shows a card with the name, what it
// is, its first lines and its picture, from /campaigns/{id}/preview (PreviewEndpoints.cs), which answers only what the
// viewer may read. Screens without hover follow the link instead. Answers are kept a minute: a reveal shows soon after.
(function () {
    if (!window.matchMedia || !window.matchMedia("(hover: hover)").matches) return;
    const SHOW_AFTER = 350, KEEP = 60000;
    const cache = new Map();
    let card = null, timer = 0, current = null;

    function target(link) {
        let url;
        try { url = new URL(link.href, location.href); } catch { return null; }
        if (url.origin !== location.origin) return null;
        let m = url.pathname.match(/^\/campaigns\/([^/]+)\/actors\/([^/]+)$/);
        if (m) return "/campaigns/" + m[1] + "/preview/actor/" + m[2];
        m = url.pathname.match(/^\/campaigns\/([^/]+)\/wiki\/([^/]+)$/);
        if (m && m[2] !== "new") return "/campaigns/" + m[1] + "/preview/page/" + m[2];
        m = url.pathname.match(/^\/campaigns\/([^/]+)\/world$/);
        if (m && url.hash.startsWith("#place-")) return "/campaigns/" + m[1] + "/preview/place/" + url.hash.slice(7);
        return null;
    }

    function load(src) {
        const hit = cache.get(src);
        if (hit && Date.now() - hit.at < KEEP) return hit.data;
        const data = fetch(src, { credentials: "same-origin", headers: { Accept: "application/json" } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; });
        cache.set(src, { at: Date.now(), data: data });
        return data;
    }

    function hide() {
        clearTimeout(timer);
        if (current) current.removeAttribute("aria-describedby");
        current = null;
        if (card) card.hidden = true;
    }

    function place(link) {
        const r = link.getBoundingClientRect(), w = card.offsetWidth, h = card.offsetHeight;
        card.style.left = Math.min(Math.max(8, r.left), window.innerWidth - w - 8) + "px";
        card.style.top = (r.bottom + 6 + h > window.innerHeight - 8 && r.top - h - 6 > 8 ? r.top - h - 6 : r.bottom + 6) + "px";
    }

    function show(link, p) {
        if (!card) {
            card = document.createElement("div");
            card.id = "hover-card";
            card.className = "hover-card";
            card.setAttribute("role", "tooltip");
            card.dataset.testid = "hover-card";
            document.body.appendChild(card);
        }
        const parts = [];
        if (p.image) {
            const img = document.createElement("img");
            img.src = p.image; img.alt = ""; img.className = "hover-card-img";
            parts.push(img);
        }
        const body = document.createElement("div");
        body.className = "hover-card-body";
        const title = document.createElement("strong");
        title.className = "hover-card-title"; title.textContent = p.title;
        const sub = document.createElement("span");
        sub.className = "hover-card-sub"; sub.textContent = p.sub;
        body.append(title, sub);
        if (p.text) {
            const text = document.createElement("p");
            text.className = "hover-card-text"; text.textContent = p.text;
            body.append(text);
        }
        parts.push(body);
        card.replaceChildren.apply(card, parts);
        card.dir = "auto";
        card.hidden = false;
        link.setAttribute("aria-describedby", "hover-card");
        place(link);
    }

    function start(link) {
        if (link === current) return;
        hide();
        const src = target(link);
        if (!src) return;
        current = link;
        timer = setTimeout(function () {
            load(src).then(function (p) { if (p && current === link && link.isConnected) show(link, p); });
        }, SHOW_AFTER);
    }

    const SELECTOR = "a.mention, a.wiki-link:not(.wiki-missing)";
    document.addEventListener("mouseover", function (e) {
        const link = e.target instanceof Element ? e.target.closest(SELECTOR) : null;
        if (link) start(link);
    });
    document.addEventListener("mouseout", function (e) {
        if (!current) return;
        const to = e.relatedTarget instanceof Element ? e.relatedTarget.closest(SELECTOR) : null;
        if (to !== current) hide();
    });
    document.addEventListener("focusin", function (e) {
        const link = e.target instanceof Element ? e.target.closest(SELECTOR) : null;
        if (link) start(link); else hide();
    });
    document.addEventListener("keydown", function (e) { if (e.key === "Escape") hide(); });
    window.addEventListener("scroll", hide, { passive: true, capture: true });
    document.addEventListener("click", hide, true);
})();
